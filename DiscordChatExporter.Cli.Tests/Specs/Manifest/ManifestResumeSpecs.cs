using System;
using System.IO;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Manifest;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Manifest;

public class ManifestResumeSpecs
{
    private static ManifestEntry Entry(string file) =>
        new(
            "1",
            "g",
            "2",
            "c",
            null,
            file,
            "Json",
            0,
            null,
            null,
            null,
            null,
            null,
            0,
            "x",
            false,
            DateTimeOffset.UnixEpoch
        );

    private static ExportManifest Manifest(params string[] files) =>
        new(
            ExportManifest.CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            Array.ConvertAll(files, Entry)
        );

    private static ExportRequest Request(string filePath, ulong guildId, ulong channelId) =>
        new(
            new Guild(new Snowflake(guildId), "g", ""),
            new Channel(
                new Snowflake(channelId),
                ChannelKind.GuildTextChat,
                new Snowflake(guildId),
                null,
                "c",
                null,
                null,
                null,
                false,
                null
            ),
            filePath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

    [Fact]
    public void Returns_the_candidates_that_are_already_in_the_manifest()
    {
        var done = ManifestResume.AlreadyExported(
            Manifest("a.json", "b.json"),
            ["a.json", "c.json"]
        );

        done.Should().BeEquivalentTo(["a.json"]);
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var done = ManifestResume.AlreadyExported(
            Manifest("Server - General [22].json"),
            ["server - general [22].json"]
        );

        done.Should().HaveCount(1);
    }

    [Fact]
    public void A_null_manifest_means_nothing_is_already_exported()
    {
        var done = ManifestResume.AlreadyExported(null, ["a.json", "b.json"]);

        done.Should().BeEmpty();
    }

    [Fact]
    public void Strict_resume_matching_requires_the_same_channel_and_an_existing_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "DceManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, "archive.json");
        File.WriteAllText(filePath, "{}");

        try
        {
            var manifest = Manifest("archive.json");

            ManifestResume
                .IsAlreadyExported(manifest, dir, Request(filePath, guildId: 1, channelId: 2))
                .Should()
                .BeTrue();

            ManifestResume
                .IsAlreadyExported(manifest, dir, Request(filePath, guildId: 1, channelId: 3))
                .Should()
                .BeFalse();

            File.Delete(filePath);
            ManifestResume
                .IsAlreadyExported(manifest, dir, Request(filePath, guildId: 1, channelId: 2))
                .Should()
                .BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
