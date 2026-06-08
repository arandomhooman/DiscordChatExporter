using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Gui.Framework;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;

namespace DiscordChatExporter.Gui.ViewModels.Components;

public partial class LibraryViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly DialogManager _dialogManager;

    // Cached file -> catalog-entry lookup, rebuilt on each reload, so search labelling doesn't
    // rebuild a dictionary over the whole catalog on every query.
    private Dictionary<string, ManifestEntry> _entriesByFile = new(
        StringComparer.OrdinalIgnoreCase
    );

    public LibraryViewModel(
        SettingsService settingsService,
        DialogManager dialogManager,
        LocalizationManager localizationManager
    )
    {
        _settingsService = settingsService;
        _dialogManager = dialogManager;
        LocalizationManager = localizationManager;
    }

    public LocalizationManager LocalizationManager { get; }

    public event EventHandler? BackRequested;

    public ObservableCollection<ManifestEntry> Entries { get; } = [];

    public ObservableCollection<LibrarySearchResult> SearchResults { get; } = [];

    internal static Task<IReadOnlyList<SqliteSearchHit>> RunSearchOffUiThreadAsync(
        Func<ValueTask<IReadOnlyList<SqliteSearchHit>>> searchAsync,
        CancellationToken cancellationToken = default
    ) => Task.Run(async () => await searchAsync(), cancellationToken);

    [ObservableProperty]
    public partial string? SearchQuery { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool HasSearchableExports { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // Empty-state flags, kept mutually exclusive so only one hint ever renders:
    //  - ShowNoResults: a non-blank search returned nothing.
    //  - ShowSearchUnavailable: there are exports, but none are searchable (.db).
    // (The "no exports catalogued" hint is driven directly off Entries.Count in XAML.)
    [ObservableProperty]
    public partial bool ShowNoResults { get; set; }

    [ObservableProperty]
    public partial bool ShowSearchUnavailable { get; set; }

    public override async Task InitializeAsync()
    {
        await ReloadAsync(_settingsService.KnownExportDirs);
    }

    private async Task ReloadAsync(IReadOnlyList<string> directories)
    {
        IsBusy = true;
        try
        {
            var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync(directories);

            Entries.Clear();
            foreach (var entry in catalog)
                Entries.Add(entry);

            // Rebuild the file -> entry lookup used to label search hits.
            _entriesByFile = Entries.ToDictionary(e => e.File, StringComparer.OrdinalIgnoreCase);

            HasSearchableExports = Entries.Any(IsSqlite);
            SearchResults.Clear();

            // A reload/scan resets search state so stale empty-state hints don't linger.
            ShowNoResults = false;
            ShowSearchUnavailable = Entries.Count > 0 && !HasSearchableExports;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var root = await _dialogManager.PromptDirectoryPathAsync();
        if (string.IsNullOrWhiteSpace(root))
            return;

        var found = await ExportCatalogBuilder.ScanForExportDirsAsync(root);

        // Merge scanned dirs into the persisted list (most-recent-first), then reload.
        foreach (var dir in found)
            _settingsService.KnownExportDirs = RecentExportDirs
                .Add(_settingsService.KnownExportDirs, dir, 200)
                .ToArray();
        _settingsService.Save();

        await ReloadAsync(_settingsService.KnownExportDirs);
    }

    private bool CanSearch() => HasSearchableExports;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        SearchResults.Clear();
        ShowNoResults = false;

        var query = SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var dbPaths = Entries.Where(IsSqlite).Select(e => e.File).ToArray();

        IsBusy = true;
        try
        {
            var hits = await RunSearchOffUiThreadAsync(() =>
                SqliteExportReader.SearchAcrossAsync(dbPaths, query, 200)
            );

            // Label each hit by joining its source db path back to the cached catalog lookup.
            foreach (var hit in hits)
            {
                _entriesByFile.TryGetValue(hit.DatabaseFilePath, out var source);
                SearchResults.Add(new LibrarySearchResult(hit, source));
            }

            ShowNoResults = SearchResults.Count == 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsSqlite(ManifestEntry entry) =>
        string.Equals(entry.Format, ExportFormat.Db.ToString(), StringComparison.OrdinalIgnoreCase)
        || entry.File.EndsWith(
            "." + ExportFormat.Db.GetFileExtension(),
            StringComparison.OrdinalIgnoreCase
        );
}

// A search hit paired with the catalog entry of the export it came from (for display labels).
public sealed record LibrarySearchResult(SqliteSearchHit Hit, ManifestEntry? Source)
{
    public string SourceLabel =>
        Source is not null ? $"{Source.GuildName} / {Source.ChannelName}" : Hit.DatabaseFilePath;
}
