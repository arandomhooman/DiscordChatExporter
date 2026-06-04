using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class ExportCatalogBuilderSpecs : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DceCatTest_" + Guid.NewGuid().ToString("N")
    );

    public ExportCatalogBuilderSpecs() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private Task<string> WriteManifestAsync(string subDir, string file, string channelName) =>
        WriteManifestEntryAsync(
            subDir,
            channelName,
            Path.Combine(_root, subDir, file),
            DateTimeOffset.UnixEpoch
        );

    private async Task<string> WriteManifestEntryAsync(
        string subDir,
        string channelName,
        string filePath,
        DateTimeOffset exportedAt
    )
    {
        var dir = Path.Combine(_root, subDir);
        Directory.CreateDirectory(dir);
        var entry = new ManifestEntry(
            "1",
            "Guild",
            "2",
            channelName,
            null,
            filePath,
            "json",
            5,
            null,
            null,
            null,
            null,
            null,
            100,
            "sha",
            false,
            exportedAt
        );
        await ManifestWriter.WriteAsync(dir, [entry], DateTimeOffset.UnixEpoch);
        return dir;
    }

    [Fact]
    public async Task Aggregates_entries_from_multiple_directories()
    {
        var d1 = await WriteManifestAsync("one", "x.json", "alpha");
        var d2 = await WriteManifestAsync("two", "y.json", "beta");

        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([d1, d2]);

        catalog.Select(e => e.ChannelName).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public async Task Dedupes_entries_by_file_path()
    {
        // Two DIFFERENT directories whose manifests reference the SAME File path. This exercises
        // the by-file dictionary dedup, not just the directory-level Distinct.
        var sharedFile = Path.Combine(_root, "shared", "x.json");
        var d1 = await WriteManifestEntryAsync(
            "one",
            "alpha",
            sharedFile,
            DateTimeOffset.UnixEpoch
        );
        var d2 = await WriteManifestEntryAsync("two", "beta", sharedFile, DateTimeOffset.UnixEpoch);

        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([d1, d2]);

        catalog.Should().ContainSingle();
    }

    [Fact]
    public async Task Orders_entries_by_exported_at_descending()
    {
        var older = await WriteManifestEntryAsync(
            "old",
            "oldest",
            Path.Combine(_root, "old", "x.json"),
            DateTimeOffset.UnixEpoch
        );
        var newer = await WriteManifestEntryAsync(
            "new",
            "newest",
            Path.Combine(_root, "new", "y.json"),
            DateTimeOffset.UnixEpoch.AddDays(1)
        );

        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([older, newer]);

        catalog.Select(e => e.ChannelName).Should().Equal("newest", "oldest");
    }

    [Fact]
    public async Task Tolerates_missing_or_manifestless_directories()
    {
        var d1 = await WriteManifestAsync("one", "x.json", "alpha");
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        var missing = Path.Combine(_root, "ghost");

        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([d1, empty, missing]);

        catalog.Should().ContainSingle();
    }

    [Fact]
    public async Task Scan_finds_manifests_recursively_under_a_root()
    {
        await WriteManifestAsync("nested/deep", "x.json", "alpha");

        var dirs = await ExportCatalogBuilder.ScanForExportDirsAsync(_root);

        dirs.Should().ContainSingle().Which.Should().Be(Path.Combine(_root, "nested", "deep"));
    }
}
