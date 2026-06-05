using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
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

    // Production-shape regression guard: a real .db sitting next to a manifest whose
    // ManifestEntry.File is the BARE filename "export.db" (exactly what ManifestBuilder writes
    // via Path.GetFileName). The catalog must resolve that to an ABSOLUTE path so the Library
    // layer's File.Exists / SqliteExportReader can actually open the db. Before the fix the
    // bare name resolved against the process CWD, File.Exists was false, and global search
    // returned zero hits in the real app — the existing absolute-path fixtures masked this.
    [Fact]
    public async Task Resolves_bare_manifest_file_to_absolute_path_so_search_works()
    {
        var dir = Path.Combine(_root, "prod");
        Directory.CreateDirectory(dir);

        // 1. Write a real .db at <dir>/export.db containing a searchable message.
        await WriteDbAsync(dir, "export.db", (1, "searchable content here"));

        // 2. Write a manifest whose entry.File is the BARE filename (production shape), Format "Db".
        var entry = new ManifestEntry(
            "1",
            "Guild",
            "2",
            "channel",
            null,
            "export.db",
            "Db",
            5,
            null,
            null,
            null,
            null,
            null,
            100,
            "sha",
            false,
            DateTimeOffset.UnixEpoch
        );
        await ManifestWriter.WriteAsync(dir, [entry], DateTimeOffset.UnixEpoch);

        // 3. The catalog entry must carry the ABSOLUTE path, and that path must exist.
        var catalog = await ExportCatalogBuilder.BuildFromDirectoriesAsync([dir]);

        var resolved = catalog.Should().ContainSingle().Subject;
        resolved.File.Should().Be(Path.Combine(dir, "export.db"));
        File.Exists(resolved.File).Should().BeTrue();

        // 4. The resolved path must drive a real search hit (red before the fix, green after).
        var hits = await SqliteExportReader.SearchAsync(resolved.File, "searchable", 50, default);
        hits.Should().ContainSingle();
    }

    // --- synthetic offline context + sqlite db writer (mirrors SqliteExportReaderSpecs) ---
    private static ExportContext CreateContext(string outputPath)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "test-channel",
            0,
            null,
            "topic",
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            null,
            ExportFormat.Db,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: false,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            content,
            [],
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            null
        );

    private static async Task WriteDbAsync(
        string dir,
        string fileName,
        params (ulong id, string content)[] messages
    )
    {
        var dbPath = Path.Combine(dir, fileName);
        var author = CreateUser(10, "alice");
        await using var writer = new SqliteMessageWriter(dbPath, CreateContext(dbPath));
        await writer.WritePreambleAsync();
        foreach (var (id, content) in messages)
            await writer.WriteMessageAsync(CreateMessage(id, author, content));
        await writer.WritePostambleAsync();
    }
}
