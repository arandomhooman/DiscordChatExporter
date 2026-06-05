using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    public partial string? SearchQuery { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool HasSearchableExports { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

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

            HasSearchableExports = Entries.Any(IsSqlite);
            SearchResults.Clear();
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

        var query = SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var dbPaths = Entries.Where(IsSqlite).Select(e => e.File).ToArray();

        IsBusy = true;
        try
        {
            var hits = await SqliteExportReader.SearchAcrossAsync(dbPaths, query, 200);

            // Label each hit by joining its source db path back to the catalog entry.
            var byFile = Entries.ToDictionary(e => e.File, StringComparer.OrdinalIgnoreCase);
            foreach (var hit in hits)
            {
                byFile.TryGetValue(hit.DatabaseFilePath, out var source);
                SearchResults.Add(new LibrarySearchResult(hit, source));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsSqlite(ManifestEntry entry) =>
        string.Equals(entry.Format, "db", StringComparison.OrdinalIgnoreCase)
        || entry.File.EndsWith(".db", StringComparison.OrdinalIgnoreCase);
}

// A search hit paired with the catalog entry of the export it came from (for display labels).
public sealed record LibrarySearchResult(SqliteSearchHit Hit, ManifestEntry? Source)
{
    public string SourceLabel =>
        Source is not null ? $"{Source.GuildName} / {Source.ChannelName}" : Hit.DatabaseFilePath;
}
