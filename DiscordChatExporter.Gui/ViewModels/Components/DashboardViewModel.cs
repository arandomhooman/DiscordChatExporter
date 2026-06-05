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
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
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
    private readonly object _exportProgressLock = new();

    private long?[] _estimatedMessagesByChannel = [];
    private long[] _messagesReadByChannel = [];
    private DateTimeOffset?[] _currentTimestampByChannel = [];
    private bool[] _completedChannels = [];
    private long _lastRateMessagesRead;
    private double _messageRate;
    private DateTimeOffset? _lastRateUpdate;
    private bool _isExportProgressRunActive;

    private DiscordClient? _discord;

    private ExportSetupViewModel? _lastExportSetup;
    private IReadOnlyList<Channel> _lastFailedChannels = [];

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
                        if (!_isExportProgressRunActive || !HasCompleteCountEstimate())
                            DisplayedProgressFraction = Progress.Current.Fraction;

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
    [NotifyCanExecuteChangedFor(nameof(RetryFailedExportCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllChannelsCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEta))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? EtaText { get; set; }

    public bool HasEta => !string.IsNullOrEmpty(EtaText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    public partial double DisplayedProgressFraction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? MessagesReadText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? RateText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? ExportedThroughText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressStatus))]
    [NotifyPropertyChangedFor(nameof(HasProgressDisplay))]
    public partial string? ChannelProgressText { get; set; }

    public bool HasProgressStatus =>
        !string.IsNullOrEmpty(MessagesReadText)
        || !string.IsNullOrEmpty(RateText)
        || !string.IsNullOrEmpty(ExportedThroughText)
        || !string.IsNullOrEmpty(ChannelProgressText);

    public bool HasProgressDisplay => HasProgressStatus || HasEta;

    public LocalizationManager LocalizationManager { get; }

    public ProgressContainer<Percentage> Progress { get; } = new();

    public bool IsProgressIndeterminate => IsBusy && DisplayedProgressFraction is <= 0 or >= 1;

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

    // Raised when the user opens the Library from the Dashboard. MainViewModel handles the switch.
    public event EventHandler? LibraryRequested;

    public event EventHandler? ConversionRequested;

    [RelayCommand]
    private void NavigateToLibrary() => LibraryRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NavigateToConversion() => ConversionRequested?.Invoke(this, EventArgs.Empty);

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

    private void ResetExportProgressDisplay()
    {
        lock (_exportProgressLock)
        {
            _estimatedMessagesByChannel = [];
            _messagesReadByChannel = [];
            _currentTimestampByChannel = [];
            _completedChannels = [];
            _lastRateMessagesRead = 0;
            _messageRate = 0;
            _lastRateUpdate = null;
            _isExportProgressRunActive = false;
        }

        _etaEstimator.Reset();
        DisplayedProgressFraction = 0;
        EtaText = null;
        MessagesReadText = null;
        RateText = null;
        ExportedThroughText = null;
        ChannelProgressText = null;
    }

    private void StartExportProgressRun(IReadOnlyList<long?> estimatedMessagesByChannel)
    {
        lock (_exportProgressLock)
        {
            _estimatedMessagesByChannel = estimatedMessagesByChannel.ToArray();
            _messagesReadByChannel = new long[_estimatedMessagesByChannel.Length];
            _currentTimestampByChannel = new DateTimeOffset?[_estimatedMessagesByChannel.Length];
            _completedChannels = new bool[_estimatedMessagesByChannel.Length];
            _lastRateMessagesRead = 0;
            _messageRate = 0;
            _lastRateUpdate = null;
            _isExportProgressRunActive = true;
        }

        DisplayedProgressFraction = 0;
    }

    private async Task<long?[]> EstimateMessageTotalsAsync(IReadOnlyList<ExportRequest> requests)
    {
        var totals = new long?[requests.Count];
        if (_discord is null)
            return totals;

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var total =
                await _discord.CountMessagesAsync(request.Channel, request.After, request.Before)
                ?? await _discord.EstimateMessageCountByDensityAsync(
                    request.Channel,
                    request.After,
                    request.Before
                );

            totals[i] = total is > 0 ? total : null;
        }

        return totals;
    }

    private IProgress<ExportProgress> CreateExportProgressInput(
        int index,
        IProgress<Percentage> fallbackProgress
    ) =>
        new System.Progress<ExportProgress>(progress =>
        {
            fallbackProgress.Report(progress.Fraction);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                ApplyExportProgress(index, progress)
            );
        });

    private bool HasCompleteCountEstimate()
    {
        lock (_exportProgressLock)
        {
            return _estimatedMessagesByChannel.Length > 0
                && _estimatedMessagesByChannel.All(t => t is > 0);
        }
    }

    private (long MessagesRead, long? EstimatedTotal, DateTimeOffset? CurrentTimestamp) SnapshotExportProgress()
    {
        lock (_exportProgressLock)
        {
            var messagesRead = _messagesReadByChannel.Sum();
            var estimatedTotal = _estimatedMessagesByChannel.All(t => t is > 0)
                ? _estimatedMessagesByChannel.Sum(t => t!.Value)
                : (long?)null;
            var currentTimestamp = _currentTimestampByChannel.LastOrDefault(t => t is not null);
            return (messagesRead, estimatedTotal, currentTimestamp);
        }
    }

    private void ApplyExportProgress(int index, ExportProgress progress)
    {
        lock (_exportProgressLock)
        {
            if (index >= _messagesReadByChannel.Length)
                return;

            _messagesReadByChannel[index] = Math.Max(
                _messagesReadByChannel[index],
                progress.MessagesRead
            );
            _currentTimestampByChannel[index] = progress.CurrentTimestamp;
        }

        var (messagesRead, estimatedTotal, currentTimestamp) = SnapshotExportProgress();
        UpdateRate(messagesRead, DateTimeOffset.Now);

        if (estimatedTotal is > 0)
        {
            var correctedTotal = Math.Max(estimatedTotal.Value, messagesRead);
            var countFraction = correctedTotal > 0 ? (double)messagesRead / correctedTotal : 0;
            DisplayedProgressFraction = Math.Max(
                DisplayedProgressFraction,
                Math.Clamp(countFraction, 0, 1)
            );
        }
        else
        {
            DisplayedProgressFraction = progress.Fraction.Fraction;
        }

        UpdateProgressStatusText(messagesRead, currentTimestamp);
        UpdateEta();
    }

    private void MarkExportProgressCompleted(int index)
    {
        lock (_exportProgressLock)
        {
            if (index < _completedChannels.Length)
                _completedChannels[index] = true;
        }

        UpdateProgressStatusText(SnapshotExportProgress().MessagesRead, null);
    }

    private void UpdateRate(long messagesRead, DateTimeOffset now)
    {
        if (_lastRateUpdate is { } previousTime)
        {
            var elapsed = (now - previousTime).TotalSeconds;
            var delta = messagesRead - _lastRateMessagesRead;
            if (elapsed > 0 && delta > 0)
            {
                var instantRate = delta / elapsed;
                var alpha = 1 - Math.Exp(-elapsed / 5);
                _messageRate =
                    _messageRate <= 0
                        ? instantRate
                        : _messageRate + alpha * (instantRate - _messageRate);
            }
        }

        _lastRateUpdate = now;
        _lastRateMessagesRead = messagesRead;
    }

    private void UpdateProgressStatusText(long messagesRead, DateTimeOffset? currentTimestamp)
    {
        MessagesReadText =
            messagesRead > 0
                ? string.Format(
                    LocalizationManager.MessagesReadFormat,
                    messagesRead.ToString("N0", CultureInfo.CurrentCulture)
                )
                : null;

        RateText =
            _messageRate > 0
                ? string.Format(
                    LocalizationManager.MessageRateFormat,
                    _messageRate.ToString("N0", CultureInfo.CurrentCulture)
                )
                : null;

        if (currentTimestamp is not null)
        {
            ExportedThroughText = string.Format(
                LocalizationManager.ExportedThroughFormat,
                currentTimestamp.Value.ToString("MMM yyyy", CultureInfo.CurrentCulture)
            );
        }

        lock (_exportProgressLock)
        {
            ChannelProgressText =
                _completedChannels.Length > 1
                    ? string.Format(
                        LocalizationManager.ChannelProgressFormat,
                        Math.Min(_completedChannels.Count(c => c) + 1, _completedChannels.Length),
                        _completedChannels.Length
                    )
                    : null;
        }
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

            StartExportProgressRun(await EstimateMessageTotalsAsync([request]));
            await exporter.ExportChannelAsync(request, CreateExportProgressInput(0, progress));

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
            MarkExportProgressCompleted(0);
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
        ResetExportProgressDisplay();

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

            var channels = dialog.Channels!.ToArray();
            var failed = await RunExportCoreAsync(exporter, dialog, channels);

            _lastExportSetup = dialog;
            UpdateFailedChannels(failed);
        }
        catch (Exception ex)
        {
            var messageBox = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(messageBox);
        }
        finally
        {
            IsBusy = false;
            EtaText = null;
        }
    }

    // Builds an ExportRequest for a channel using the dialog's parameters. Pure path-building.
    private ExportRequest BuildExportRequest(ExportSetupViewModel dialog, Channel channel) =>
        new(
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

    private static ManifestChannelInfo BuildManifestInfo(ExportRequest r) =>
        new(
            r.Guild.Id.ToString(),
            r.Guild.Name,
            r.Channel.Id.ToString(),
            r.Channel.Name,
            r.Channel.Parent?.Name,
            r.Format.ToString()
        );

    // Best-effort per-channel manifest checkpoint. Failure must never fail the channel's export.
    // Returns true if the manifest was written, false if a write failure was swallowed (the caller
    // notifies once per run rather than once per channel).
    private async ValueTask<bool> CheckpointManifestAsync(
        ExportRequest request,
        ExportResult result
    )
    {
        try
        {
            var entries = ManifestBuilder.Build(
                BuildManifestInfo(request),
                result,
                DateTimeOffset.Now
            );
            await ManifestWriter.WriteAsync(request.OutputDirPath, entries, DateTimeOffset.Now);
            return true;
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // Adds the given output folders to the persisted KnownExportDirs (most-recent-first, deduped,
    // capped) in a single settings write, so the Library home view can discover past exports. The
    // shared seam every write path (export, retry, continue) funnels its output folder through.
    private void RegisterExportedDirs(IReadOnlyCollection<string> dirs)
    {
        if (dirs.Count == 0)
            return;

        var known = _settingsService.KnownExportDirs;
        foreach (var dir in dirs)
            known = RecentExportDirs.Add(known, dir, 200).ToArray();

        _settingsService.KnownExportDirs = known;
        _settingsService.Save();
    }

    // After a continue grows an existing export file, refresh its manifest entry (fresh count,
    // size, and hash) and register its folder so the Library catalog stays in sync. Best-effort:
    // a catalog failure must never fail the continue itself.
    private async Task RefreshContinuedExportCatalogAsync(
        string filePath,
        Guild guild,
        Channel channel,
        long messageCount
    )
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir))
                return;

            var fileName = Path.GetFileName(filePath);
            var existing = await ManifestReader.TryReadAsync(
                Path.Combine(dir, ExportManifest.FileName)
            );
            var prior = existing?.Entries.FirstOrDefault(e =>
                string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase)
            );

            var info = new ManifestChannelInfo(
                guild.Id.ToString(),
                guild.Name,
                channel.Id.ToString(),
                channel.Name,
                channel.Parent?.Name,
                // Preserve the original format string (e.g. HtmlLight vs HtmlDark, which the file
                // extension alone can't distinguish); fall back to the extension mapping.
                prior?.Format
                    ?? ContinuationFormat.FormatFor(filePath).ToString()
            );

            var result = new ExportResult(
                [new ExportedFile(filePath, messageCount, null, null, null, null)],
                messageCount,
                0
            );

            var entries = ManifestBuilder.Build(info, result, DateTimeOffset.Now);
            if (entries.Count == 0)
                return;

            // Continue doesn't re-scan the whole file, so carry the first-message metadata forward
            // from the prior entry rather than nulling it; count/size/hash above are freshly read.
            if (prior is not null)
                entries =
                [
                    entries[0] with
                    {
                        FirstMessageId = prior.FirstMessageId,
                        FirstMessageTimestamp = prior.FirstMessageTimestamp,
                    },
                ];

            await ManifestWriter.WriteAsync(dir, entries, DateTimeOffset.Now);
            RegisterExportedDirs([dir]);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Best-effort: a catalog refresh failure must not fail the continue itself.
        }
    }

    // The export core, shared by ExportAsync and the retry command. Returns the channels that failed.
    private async Task<IReadOnlyList<Channel>> RunExportCoreAsync(
        ChannelExporter exporter,
        ExportSetupViewModel dialog,
        IReadOnlyList<Channel> channels
    )
    {
        var requests = channels
            .Select(c => (Channel: c, Request: BuildExportRequest(dialog, c)))
            .ToArray();

        // Resume detection: which selected channels are already exported in their target directory?
        var manifestsByDir = new Dictionary<string, ExportManifest?>(
            StringComparer.OrdinalIgnoreCase
        );
        var alreadyDone = new List<(Channel Channel, ExportRequest Request)>();

        foreach (var r in requests)
        {
            var dir = r.Request.OutputDirPath;
            if (!manifestsByDir.TryGetValue(dir, out var manifest))
            {
                manifest = await ManifestReader.TryReadAsync(
                    Path.Combine(dir, ExportManifest.FileName)
                );
                manifestsByDir[dir] = manifest;
            }

            if (
                ManifestResume
                    .AlreadyExported(manifest, [Path.GetFileName(r.Request.OutputFilePath)])
                    .Count > 0
            )
            {
                alreadyDone.Add(r);
            }
        }

        var toExport = requests;

        if (alreadyDone.Count == requests.Length)
        {
            // Everything is already exported here — nothing to do.
            _snackbarManager.Notify(LocalizationManager.ResumeAllUpToDateMessage.TrimEnd('.'));
            return [];
        }

        if (alreadyDone.Count > 0)
        {
            var prompt = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ResumePromptTitle,
                string.Format(LocalizationManager.ResumePromptMessage, alreadyDone.Count),
                LocalizationManager.ResumeSkipButton, // default -> true -> skip (resume)
                LocalizationManager.ResumeExportAllButton // cancel (EXPORT ALL) -> null -> export all
            );

            if (await _dialogManager.ShowDialogAsync(prompt) == true)
            {
                var doneSet = alreadyDone.Select(d => d.Channel).ToHashSet();
                toExport = requests.Where(r => !doneSet.Contains(r.Channel)).ToArray();
                _snackbarManager.Notify(
                    string.Format(LocalizationManager.ResumeSkippedMessage, alreadyDone.Count)
                );
            }
        }

        var pairs = toExport
            .Select((r, index) => new
            {
                Index = index,
                r.Channel,
                r.Request,
                Progress = _progressMuxer.CreateInput(),
            })
            .ToArray();
        StartExportProgressRun(
            await EstimateMessageTotalsAsync(pairs.Select(p => p.Request).ToArray())
        );

        var exportStats = new ConcurrentBag<ChannelExportStats>();
        var failedChannels = new ConcurrentBag<Channel>();
        var exportedDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var successfulExportCount = 0;
        var catalogWriteFailed = 0;
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            pairs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, _settingsService.ParallelLimit),
            },
            async (pair, cancellationToken) =>
            {
                var request = pair.Request;
                var progress = pair.Progress;

                try
                {
                    var result = await exporter.ExportChannelAsync(
                        request,
                        CreateExportProgressInput(pair.Index, progress),
                        cancellationToken
                    );

                    if (!await CheckpointManifestAsync(request, result))
                        Interlocked.Exchange(ref catalogWriteFailed, 1);

                    exportStats.Add(
                        new ChannelExportStats(
                            result.MessageCount,
                            result.AssetCount,
                            SumFileSizes(result.Files)
                        )
                    );

                    Interlocked.Increment(ref successfulExportCount);

                    // Remember this export's output folder so the Library can catalog it later.
                    exportedDirs.TryAdd(request.OutputDirPath, 0);
                }
                catch (ChannelEmptyException ex)
                {
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));

                    // Empty channels still produce an (empty) file via exporter disposal; checkpoint it
                    // so it counts as "done" for resume, consistent with the filtered-to-empty case.
                    if (
                        !await CheckpointManifestAsync(
                            request,
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
                    )
                        Interlocked.Exchange(ref catalogWriteFailed, 1);

                    // Track the folder even for an empty export so the Library can catalog it.
                    exportedDirs.TryAdd(request.OutputDirPath, 0);
                }
                catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                {
                    failedChannels.Add(pair.Channel);
                    _snackbarManager.Notify(ex.Message.TrimEnd('.'));
                }
                finally
                {
                    MarkExportProgressCompleted(pair.Index);
                    progress.ReportCompletion();
                }
            }
        );

        stopwatch.Stop();

        if (successfulExportCount > 0)
        {
            var summary = ExportSummarizer.Summarize(
                exportStats.ToArray(),
                failedChannels.Count,
                stopwatch.Elapsed
            );

            _snackbarManager.Notify(FormatExportSummary(summary));
        }

        // Persist every folder we wrote into (including empty-channel exports) so the Library home
        // view can discover them — even when no channel had messages.
        RegisterExportedDirs(exportedDirs.Keys.ToArray());

        // Best-effort: a manifest write failure never fails the export, but surface it once per run
        // rather than once per affected channel.
        if (catalogWriteFailed == 1)
            _snackbarManager.Notify(
                LocalizationManager.ExportCatalogWriteFailedMessage.TrimEnd('.')
            );

        CompletionAttention.FlashIfUnfocused();

        return failedChannels.ToArray();
    }

    // Records which channels failed the last run and refreshes the retry command's state. Shared by
    // the export and retry paths so this bookkeeping can't drift between them.
    private void UpdateFailedChannels(IReadOnlyList<Channel> failed)
    {
        _lastFailedChannels = failed;
        OnPropertyChanged(nameof(HasFailedExport));
        RetryFailedExportCommand.NotifyCanExecuteChanged();
    }

    private bool CanRetryFailedExport() =>
        !IsBusy
        && _discord is not null
        && _lastExportSetup is not null
        && _lastFailedChannels.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRetryFailedExport))]
    private async Task RetryFailedExportAsync()
    {
        if (_discord is null || _lastExportSetup is null || _lastFailedChannels.Count == 0)
            return;

        IsBusy = true;
        ResetExportProgressDisplay();

        try
        {
            var exporter = new ChannelExporter(_discord);
            var failed = await RunExportCoreAsync(exporter, _lastExportSetup, _lastFailedChannels);
            UpdateFailedChannels(failed);
        }
        catch (Exception ex)
        {
            var messageBox = _viewModelManager.GetMessageBoxViewModel(
                LocalizationManager.ErrorExportingTitle,
                ex.ToString()
            );

            await _dialogManager.ShowDialogAsync(messageBox);
        }
        finally
        {
            IsBusy = false;
            EtaText = null;
        }
    }

    public bool HasFailedExport => _lastFailedChannels.Count > 0;

    private bool CanContinueExport() =>
        !IsBusy && _discord is not null && SelectedGuild is not null && SelectedChannels.Any();

    [RelayCommand(CanExecute = nameof(CanContinueExport))]
    private async Task ContinueExportAsync()
    {
        if (_discord is null)
            return;

        // Pick the existing export (JSON, HTML, CSV, or SQLite)
        var filePath = await _dialogManager.PromptSingleFilePathAsync(
            CreateContinueExportFileTypes()
        );
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
        ResetExportProgressDisplay();
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
                StartExportProgressRun(await EstimateMessageTotalsAsync([request]));
                await exporter.ExportChannelAsync(request, CreateExportProgressInput(0, progress));
            }
            catch (ChannelEmptyException)
            {
                if (ContinuationFormat.FormatFor(filePath) is not ExportFormat.Db)
                {
                    _snackbarManager.Notify(
                        LocalizationManager.ContinueExportUpToDateMessage.TrimEnd('.')
                    );
                    return;
                }
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

            // Keep the Library catalog in sync with the file we just grew, and remember its folder.
            // Continue is the third write path and previously updated neither, leaving a stale
            // message count and never surfacing continue-only folders.
            await RefreshContinuedExportCatalogAsync(filePath, guild, channel, total);
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
            MarkExportProgressCompleted(0);
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
        _etaEstimator.Report(DisplayedProgressFraction, DateTimeOffset.Now);
        var estimate = _etaEstimator.Estimate;
        EtaText =
            estimate is null ? LocalizationManager.EtaEstimatingText
            : estimate.Value <= TimeSpan.Zero ? null
            : string.Format(
                LocalizationManager.EtaRemainingFormat,
                _etaEstimator.IsCapped ? "> 12 h" : FormatDuration(estimate.Value)
            );
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
            FileSize.FromBytes(summary.TotalBytes).ToString(),
            FormatDuration(summary.Duration)
        );

        if (summary.FailedChannels > 0)
            message += string.Format(
                LocalizationManager.ExportSummaryFailedSuffix,
                summary.FailedChannels
            );

        return message;
    }

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

    private static IReadOnlyList<FilePickerFileType> CreateContinueExportFileTypes() =>
        [
            new FilePickerFileType("Supported exports (JSON, HTML, CSV, SQLite)")
            {
                Patterns = ["*.json", "*.html", "*.htm", "*.csv", "*.db"],
            },
        ];

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _eventSubscription.Dispose();
        }

        base.Dispose(disposing);
    }
}
