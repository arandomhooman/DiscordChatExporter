using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestWriterSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"dce-manifest-{Guid.NewGuid():N}"
    );

    public ManifestWriterSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static ManifestEntry Entry(string file, long messageCount) =>
        new(
            GuildId: "1",
            GuildName: "g",
            ChannelId: "2",
            ChannelName: "c",
            CategoryName: null,
            File: file,
            Format: "Json",
            MessageCount: messageCount,
            FirstMessageId: "10",
            FirstMessageTimestamp: DateTimeOffset.UnixEpoch,
            LastMessageId: "20",
            LastMessageTimestamp: DateTimeOffset.UnixEpoch,
            AssetCount: 0,
            FileSizeBytes: 1,
            Sha256: "x",
            Partitioned: false,
            ExportedAt: DateTimeOffset.UnixEpoch
        );

    [Fact]
    public async Task Writing_creates_a_manifest_with_the_given_entries_and_schema_version()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest.Should().NotBeNull();
        manifest!.SchemaVersion.Should().Be(ExportManifest.CurrentSchemaVersion);
        manifest.Entries.Should().ContainSingle(e => e.File == "a.json");
    }

    [Fact]
    public async Task Writing_again_replaces_entries_for_the_same_file_and_keeps_the_others()
    {
        await ManifestWriter.WriteAsync(
            _dir,
            [Entry("a.json", 1), Entry("b.json", 1)],
            DateTimeOffset.UnixEpoch
        );
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 99)], DateTimeOffset.UnixEpoch);

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest!.Entries.Should().HaveCount(2);
        manifest.Entries.Single(e => e.File == "a.json").MessageCount.Should().Be(99);
        manifest.Entries.Single(e => e.File == "b.json").MessageCount.Should().Be(1);
    }

    [Fact]
    public async Task Writing_over_an_existing_manifest_leaves_a_backup()
    {
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 1)], DateTimeOffset.UnixEpoch);
        await ManifestWriter.WriteAsync(_dir, [Entry("a.json", 2)], DateTimeOffset.UnixEpoch);

        File.Exists(Path.Combine(_dir, ExportManifest.FileName + ".bak")).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_writes_to_the_same_manifest_do_not_lose_entries()
    {
        // Fire many parallel writes, each adding a distinct file. Without serialization,
        // the read-merge-write race would clobber entries (last-writer-wins on the whole file).
        const int count = 50;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            async (i, ct) =>
                await ManifestWriter.WriteAsync(
                    _dir,
                    [Entry($"file{i}.json", i)],
                    DateTimeOffset.UnixEpoch,
                    ct
                )
        );

        var manifest = await ManifestReader.TryReadAsync(
            Path.Combine(_dir, ExportManifest.FileName)
        );
        manifest.Should().NotBeNull();
        manifest!.Entries.Should().HaveCount(count);
    }
}
