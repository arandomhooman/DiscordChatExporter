using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Models;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.Utils.Extensions;
using DiscordChatExporter.Gui.ViewModels.Dialogs;
using Gress;
using Gress.Completable;
using PowerKit;
using PowerKit.Extensions;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly ViewModelManager _viewModelManager;
    private readonly SnackbarManager _snackbarManager;
    private readonly DialogManager _dialogManager;
    private readonly SettingsService _settingsService;

    private readonly IDisposable _eventSubscription;
    private readonly AutoResetProgressMuxer _progressMuxer;

    private DiscordClient? _discord;

    public DashboardViewModel(
        ViewModelManager viewModelManager,
        DialogManager dialogManager,
        SnackbarManager snackbarManager,
        SettingsService settingsService,
        LocalizationManager localizationManager
    )
    {
        _viewModelManager = viewModelManager;
        _dialogManager = dialogManager;
        _snackbarManager = snackbarManager;
        _settingsService = settingsService;
        LocalizationManager = localizationManager;

        _progressMuxer = Progress.CreateMuxer().WithAutoReset();

        _eventSubscription = Disposable.Merge(
            Progress.WatchProperty(
                o => o.Current,
                _ => OnPropertyChanged(nameof(IsProgressIndeterminate))
            ),
            SelectedChannels.WatchProperty(
                o => o.Count,
                _ => ExportCommand.NotifyCanExecuteChanged()
            )
        );
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]
    public partial bool IsBusy { get; set; }

    public LocalizationManager LocalizationManager { get; }

    public ProgressContainer<Percentage> Progress { get; } = new();

    public bool IsProgressIndeterminate => IsBusy && Progress.Current.Fraction is <= 0 or >= 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    public partial string? Token { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<Guild>? AvailableGuilds { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial Guild? SelectedGuild { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ChannelConnection>? AvailableChannels { get; set; }

    public ObservableCollection<ChannelConnection> SelectedChannels { get; } = [];

    public override Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settingsService.LastToken))
            Token = _settingsService.LastToken;

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ShowSettingsAsync() =>
        await _dialogManager.ShowDialogAsync(_viewModelManager.GetSettingsViewModel());

    private bool CanPullGuilds() => !IsBusy && !string.IsNullOrWhiteSpace(Token);

    [RelayCommand(CanExecute = nameof(CanPullGuilds))]
    private async Task PullGuildsAsync()
    {
        IsBusy = true;
        var progress = _progressMuxer.CreateInput();

        try
        {
            var token = Token?.Trim('"', ' ');
            if (string.IsNullOrWhiteSpace(token))
                return;

            AvailableGuilds = null;
            SelectedGuild = null;
            AvailableChannels = null;
            SelectedChannels.Clear();

            _discord = new DiscordClient(token, _settingsService.RateLimitPreference);
            _settingsService.LastToken = token;

            var guilds = await _discord.GetUserGuildsAsync();

            AvailableGuilds = guilds;
            SelectedGuild = guilds.FirstOrDefault();

            await PullChannelsAsync();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorPullingGuildsTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    private bool CanPullChannels() => !IsBusy && _discord is not null && SelectedGuild is not null;

    [RelayCommand(CanExecute = nameof(CanPullChannels))]
    private async Task PullChannelsAsync()
    {
        IsBusy = true;
        var progress = _progressMuxer.CreateInput();

        try
        {
            if (_discord is null || SelectedGuild is null)
                return;

            AvailableChannels = null;
            SelectedChannels.Clear();

            var channels = new List<Channel>();

            // Regular channels
            await foreach (var channel in _discord.GetGuildChannelsAsync(SelectedGuild.Id))
                channels.Add(channel);

            // Threads
            if (_settingsService.ThreadInclusionMode != ThreadInclusionMode.None)
            {
                await foreach (
                    var thread in _discord.GetGuildThreadsAsync(
                        SelectedGuild.Id,
                        _settingsService.ThreadInclusionMode == ThreadInclusionMode.All
                    )
                )
                {
                    channels.Add(thread);
                }
            }

            // Build a hierarchy of channels
            var channelTree = ChannelConnection.BuildTree(
                channels
                    .OrderByDescending(c => c.IsDirect ? c.LastMessageId : null)
                    .ThenBy(c => c.Position)
                    .ToArray()
            );

            AvailableChannels = channelTree;
            SelectedChannels.Clear();
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorPullingChannelsTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
        }
    }

    private bool CanExport() =>
        !IsBusy && _discord is not null && SelectedGuild is not null && SelectedChannels.Any();

    private static async ValueTask SetClipboardTextAsync(string text)
    {
        var clipboard =
            Avalonia.Application.Current?.ApplicationLifetime?.TryGetTopLevel()?.Clipboard
            ?? throw new ApplicationException("Could not access the clipboard.");

        await clipboard.SetTextAsync(text);
    }

    private async ValueTask CopyUserMessagesAsync(
        ExportSetupViewModel dialog,
        ChannelExporter exporter
    )
    {
        var channel = dialog.Channels!.Single();
        var progress = _progressMuxer.CreateInput();
        var outputPath = Path.Combine(Path.GetTempPath(), $"{Program.Name}-{Guid.NewGuid():N}.txt");

        try
        {
            var request = new ExportRequest(
                dialog.Guild!,
                channel,
                outputPath,
                null,
                ExportFormat.PlainText,
                dialog.After?.Pipe(Snowflake.FromDate),
                dialog.Before?.Pipe(Snowflake.FromDate),
                PartitionLimit.Null,
                dialog.CopyUserMessagesFilter,
                dialog.IsReverseMessageOrder,
                dialog.ShouldFormatMarkdown,
                false,
                false,
                _settingsService.Locale,
                _settingsService.IsUtcNormalizationEnabled
            );

            await exporter.ExportChannelAsync(request, progress);

            var text = await File.ReadAllTextAsync(outputPath);
            var user = dialog.CopyUserMessagesUserValue?.Trim();

            if (text.Length > 0)
            {
                await SetClipboardTextAsync(text);

                _snackbarManager.Notify(
                    string.Format(LocalizationManager.SuccessfulCopyUserMessagesMessage, user)
                );
            }
            else
            {
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.NoCopyUserMessagesFoundMessage, user)
                );
            }
        }
        finally
        {
            progress.ReportCompletion();

            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // Best-effort cleanup of the temporary export.
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        IsBusy = true;

        try
        {
            if (_discord is null || SelectedGuild is null || !SelectedChannels.Any())
                return;

            var dialog = _viewModelManager.GetExportSetupViewModel(
                SelectedGuild,
                SelectedChannels.Select(c => c.Channel).ToArray()
            );

            if (await _dialogManager.ShowDialogAsync(dialog) != true)
                return;

            var exporter = new ChannelExporter(_discord);

            if (dialog.ShouldCopyUserMessages)
            {
                await CopyUserMessagesAsync(dialog, exporter);
                return;
            }

            var channelProgressPairs = dialog
                .Channels!.Select(c => new { Channel = c, Progress = _progressMuxer.CreateInput() })
                .ToArray();

            var successfulExportCount = 0;

            await Parallel.ForEachAsync(
                channelProgressPairs,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, _settingsService.ParallelLimit),
                },
                async (pair, cancellationToken) =>
                {
                    var channel = pair.Channel;
                    var progress = pair.Progress;

                    try
                    {
                        var request = new ExportRequest(
                            dialog.Guild!,
                            channel,
                            dialog.OutputPath!,
                            dialog.AssetsDirPath,
                            dialog.SelectedFormat,
                            dialog.After?.Pipe(Snowflake.FromDate),
                            dialog.Before?.Pipe(Snowflake.FromDate),
                            dialog.PartitionLimit,
                            dialog.MessageFilter,
                            dialog.IsReverseMessageOrder,
                            dialog.ShouldFormatMarkdown,
                            dialog.ShouldDownloadAssets,
                            dialog.ShouldReuseAssets,
                            _settingsService.Locale,
                            _settingsService.IsUtcNormalizationEnabled
                        );

                        await exporter.ExportChannelAsync(request, progress, cancellationToken);

                        Interlocked.Increment(ref successfulExportCount);
                    }
                    catch (ChannelEmptyException ex)
                    {
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                    }
                    catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                    {
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                    }
                    finally
                    {
                        progress.ReportCompletion();
                    }
                }
            );

            // Notify of the overall completion
            if (successfulExportCount > 0)
            {
                _snackbarManager.Notify(
                    string.Format(
                        LocalizationManager.SuccessfulExportMessage,
                        successfulExportCount
                    )
                );
            }
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanContinueExport() => !IsBusy && _discord is not null;

    [RelayCommand(CanExecute = nameof(CanContinueExport))]
    private async Task ContinueExportAsync()
    {
        if (_discord is null)
            return;

        // Pick the existing JSON export
        var filePath = await _dialogManager.PromptSingleFilePathAsync([
            new FilePickerFileType("JSON export") { Patterns = ["*.json"] },
        ]);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        // Refuse partitioned exports (name- or sibling-based detection)
        if (IsPartitionedExportPath(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportPartitionedUnsupportedMessage.TrimEnd('.')
            );
            return;
        }

        IsBusy = true;
        var progress = _progressMuxer.CreateInput();
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"{Program.Name}-continue-{Guid.NewGuid():N}.json"
        );

        try
        {
            var info = await JsonExportInspector.InspectAsync(filePath);

            if (!info.IsChronological)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportReverseUnsupportedMessage.TrimEnd('.')
                );
                return;
            }

            // Resolve live channel + guild
            var channel = await _discord.GetChannelAsync(info.ChannelId);
            var guild = channel.IsDirect
                ? Guild.DirectMessages
                : await _discord.GetGuildAsync(channel.GuildId);

            // Export only messages after the recorded cutoff into a temp file
            var request = new ExportRequest(
                guild,
                channel,
                tempPath,
                null,
                ExportFormat.Json,
                info.LastMessageId, // exact, exclusive cursor
                info.Before,
                PartitionLimit.Null,
                MessageFilter.Null,
                false,
                _settingsService.LastShouldFormatMarkdown,
                false,
                false,
                _settingsService.Locale,
                _settingsService.IsUtcNormalizationEnabled
            );

            var exporter = new ChannelExporter(_discord);

            try
            {
                await exporter.ExportChannelAsync(request, progress);
            }
            catch (ChannelEmptyException)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                );
                return;
            }

            // Merge the new messages into the original file
            var addedBefore = info.MessageCount;
            var total = await JsonExportMerger.MergeAsync(filePath, tempPath, DateTimeOffset.Now);
            var added = total - addedBefore;

            if (added <= 0)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                );
            }
            else
            {
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ContinueExportSuccessMessage, added)
                );
            }
        }
        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
        {
            _snackbarManager.Notify(ex.Message.TrimEnd('.'));
        }
        catch (Exception ex)
        {
            var dialog = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );
            await _dialogManager.ShowDialogAsync(dialog);
        }
        finally
        {
            progress.ReportCompletion();
            IsBusy = false;
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of the temporary export.
            }
        }
    }

    private static bool IsPartitionedExportPath(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.Contains(" [part ", StringComparison.OrdinalIgnoreCase))
            return true;

        var dir = Path.GetDirectoryName(filePath);
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(dir))
            return false;

        var sibling = Path.Combine(dir, $"{fileName} [part 2]{ext}");
        return File.Exists(sibling);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _eventSubscription.Dispose();
        }

        base.Dispose(disposing);
    }
}
