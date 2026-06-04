using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Models;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.Utils;
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
    private readonly EtaEstimator _etaEstimator = new();

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
                _ =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        OnPropertyChanged(nameof(IsProgressIndeterminate));
                        UpdateEta();
                    })
            ),
            SelectedChannels.WatchProperty(
                o => o.Count,
                _ =>
                {
                    ExportCommand.NotifyCanExecuteChanged();
                    ContinueExportCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(AllChannelsSelected));
                    OnPropertyChanged(nameof(SelectAllChannelsButtonText));
                }
            )
        );
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(PullGuildsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullChannelsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllChannelsCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEta))]
    public partial string? EtaText { get; set; }

    public bool HasEta => !string.IsNullOrEmpty(EtaText);

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
    [NotifyCanExecuteChangedFor(nameof(ContinueExportCommand))]
    public partial Guild? SelectedGuild { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AllChannelsSelected))]
    [NotifyPropertyChangedFor(nameof(SelectAllChannelsButtonText))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllChannelsCommand))]
    public partial IReadOnlyList<ChannelConnection>? AvailableChannels { get; set; }

    public ObservableCollection<ChannelConnection> SelectedChannels { get; } = [];

    // True when every exportable (non-category) channel in the current guild is selected.
    public bool AllChannelsSelected
    {
        get
        {
            if (AvailableChannels is null)
                return false;

            var exportableCount = FlattenExportableChannels(AvailableChannels).Count();
            return exportableCount > 0 && SelectedChannels.Count >= exportableCount;
        }
    }

    public string SelectAllChannelsButtonText =>
        AllChannelsSelected
            ? LocalizationManager.DeselectAllChannelsButton
            : LocalizationManager.SelectAllChannelsButton;

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

    // Walks the channel tree and yields every exportable (non-category) channel,
    // including nested threads. Categories are not exportable and are skipped.
    private static IEnumerable<ChannelConnection> FlattenExportableChannels(
        IReadOnlyList<ChannelConnection> connections
    )
    {
        foreach (var connection in connections)
        {
            if (!connection.Channel.IsCategory)
                yield return connection;

            if (connection.Children.Count > 0)
            {
                foreach (var child in FlattenExportableChannels(connection.Children))
                    yield return child;
            }
        }
    }

    private bool CanSelectAllChannels() =>
        !IsBusy
        && AvailableChannels is not null
        && FlattenExportableChannels(AvailableChannels).Any();

    [RelayCommand(CanExecute = nameof(CanSelectAllChannels))]
    private void SelectAllChannels()
    {
        if (AvailableChannels is null)
            return;

        // Capture the toggle state before mutating, since AllChannelsSelected
        // is derived from SelectedChannels.Count and would flip after Clear().
        var wasAllSelected = AllChannelsSelected;
        SelectedChannels.Clear();

        if (!wasAllSelected)
        {
            foreach (var connection in FlattenExportableChannels(AvailableChannels))
                SelectedChannels.Add(connection);
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
        _etaEstimator.Reset();

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

            var manifestData =
                new ConcurrentBag<(string Dir, ManifestChannelInfo Info, ExportResult Result)>();

            var exportStats = new ConcurrentBag<ChannelExportStats>();
            var failedExportCount = 0;
            var stopwatch = Stopwatch.StartNew();

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

                    static ManifestChannelInfo BuildInfo(ExportRequest r) =>
                        new(
                            r.Guild.Id.ToString(),
                            r.Guild.Name,
                            r.Channel.Id.ToString(),
                            r.Channel.Name,
                            r.Channel.Parent?.Name,
                            r.Format.ToString()
                        );

                    // Built outside the try so the ChannelEmptyException handler can still
                    // catalog the empty output file (construction is pure path-building).
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

                    try
                    {
                        var result = await exporter.ExportChannelAsync(
                            request,
                            progress,
                            cancellationToken
                        );

                        manifestData.Add((request.OutputDirPath, BuildInfo(request), result));

                        exportStats.Add(
                            new ChannelExportStats(
                                result.MessageCount,
                                result.AssetCount,
                                SumFileSizes(result.Files)
                            )
                        );

                        Interlocked.Increment(ref successfulExportCount);
                    }
                    catch (ChannelEmptyException ex)
                    {
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));

                        // Empty channels still produce an (empty) output file via exporter
                        // disposal; catalog it for consistency with the filtered-to-empty case.
                        manifestData.Add(
                            (
                                request.OutputDirPath,
                                BuildInfo(request),
                                new ExportResult(
                                    [
                                        new ExportedFile(
                                            request.OutputFilePath,
                                            0,
                                            null,
                                            null,
                                            null,
                                            null
                                        ),
                                    ],
                                    0,
                                    0
                                )
                            )
                        );
                    }
                    catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                    {
                        Interlocked.Increment(ref failedExportCount);
                        _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                    }
                    finally
                    {
                        progress.ReportCompletion();
                    }
                }
            );

            // Write/update the export catalog (best-effort: never fail the export over it).
            // Each output directory is guarded independently so one bad directory neither
            // aborts the others nor spams the same failure notification.
            if (!manifestData.IsEmpty)
            {
                var now = DateTimeOffset.Now;
                var catalogFailed = false;

                foreach (var group in manifestData.GroupBy(d => d.Dir))
                {
                    try
                    {
                        var entries = group
                            .SelectMany(d => ManifestBuilder.Build(d.Info, d.Result, now))
                            .ToArray();

                        await ManifestWriter.WriteAsync(group.Key, entries, now);
                    }
                    catch (Exception ex)
                        when (ex is IOException or UnauthorizedAccessException or JsonException)
                    {
                        catalogFailed = true;
                    }
                }

                if (catalogFailed)
                {
                    _snackbarManager.Notify(
                        LocalizationManager.ExportCatalogWriteFailedMessage.TrimEnd('.')
                    );
                }
            }

            // Notify of the overall completion with a summary
            stopwatch.Stop();
            if (successfulExportCount > 0)
            {
                var summary = ExportSummarizer.Summarize(
                    exportStats.ToArray(),
                    failedExportCount,
                    stopwatch.Elapsed
                );

                _snackbarManager.Notify(FormatExportSummary(summary));
            }

            // Flash the taskbar if the user isn't watching (best-effort, Windows only)
            CompletionAttention.FlashIfUnfocused();
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
            EtaText = null;
        }
    }

    private bool CanContinueExport() =>
        !IsBusy && _discord is not null && SelectedGuild is not null && SelectedChannels.Any();

    [RelayCommand(CanExecute = nameof(CanContinueExport))]
    private async Task ContinueExportAsync()
    {
        if (_discord is null)
            return;

        // Pick the existing export (JSON, HTML, or CSV)
        var filePath = await _dialogManager.PromptSingleFilePathAsync([
            new FilePickerFileType("Supported exports (JSON, HTML, CSV)")
            {
                Patterns = ["*.json", "*.html", "*.htm", "*.csv"],
            },
        ]);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        // Refuse unsupported file types
        if (!ContinuationFormat.IsSupportedExtension(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportFormatUnsupportedMessage.TrimEnd('.')
            );
            return;
        }

        // Refuse partitioned exports (name- or sibling-based detection)
        if (IsPartitionedExportPath(filePath))
        {
            _snackbarManager.Notify(
                LocalizationManager.ContinueExportPartitionedUnsupportedMessage.TrimEnd('.')
            );
            return;
        }

        IsBusy = true;
        _etaEstimator.Reset();
        var progress = _progressMuxer.CreateInput();
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"{Program.Name}-continue-{Guid.NewGuid():N}{Path.GetExtension(filePath)}"
        );

        try
        {
            var cutoff = await ContinuationFormat.ReadCutoffAsync(filePath);

            if (!cutoff.IsChronological)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportReverseUnsupportedMessage.TrimEnd('.')
                );
                return;
            }

            var channel = await _discord.GetChannelAsync(cutoff.ChannelId);
            var guild = channel.IsDirect
                ? Guild.DirectMessages
                : await _discord.GetGuildAsync(channel.GuildId);

            var request = new ExportRequest(
                guild,
                channel,
                tempPath,
                null,
                ContinuationFormat.FormatFor(filePath),
                cutoff.Cutoff,
                cutoff.Before,
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

            var countBefore = cutoff.ExistingCount;
            var total = await ContinuationFormat.MergeAsync(
                filePath,
                tempPath,
                cutoff,
                DateTimeOffset.Now
            );
            var newMessages = total - countBefore;

            if (newMessages <= 0)
            {
                _snackbarManager.Notify(
                    LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                );
            }
            else
            {
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ContinueExportSuccessMessage, newMessages)
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
            EtaText = null;
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

    private void UpdateEta()
    {
        if (!IsBusy)
        {
            EtaText = null;
            return;
        }
        _etaEstimator.Report(Progress.Current.Fraction, DateTimeOffset.Now);
        var estimate = _etaEstimator.Estimate;
        EtaText =
            estimate is null ? LocalizationManager.EtaEstimatingText
            : estimate.Value <= TimeSpan.Zero ? null
            : string.Format(LocalizationManager.EtaRemainingFormat, FormatDuration(estimate.Value));
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:D2}s"
        : $"{t.Seconds}s";

    private string FormatExportSummary(ExportSummary summary)
    {
        var message = string.Format(
            LocalizationManager.ExportSummaryMessage,
            summary.SucceededChannels,
            summary.TotalMessages.ToString("N0", CultureInfo.CurrentCulture),
            summary.TotalAssets.ToString("N0", CultureInfo.CurrentCulture),
            FormatBytes(summary.TotalBytes),
            FormatDuration(summary.Duration)
        );

        if (summary.FailedChannels > 0)
            message += string.Format(
                LocalizationManager.ExportSummaryFailedSuffix,
                summary.FailedChannels
            );

        return message;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB"
        : $"{bytes} B";

    private static long SumFileSizes(IReadOnlyList<ExportedFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            try
            {
                total += new FileInfo(file.FilePath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a size we can't read just doesn't contribute to the total.
            }
        }

        return total;
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
