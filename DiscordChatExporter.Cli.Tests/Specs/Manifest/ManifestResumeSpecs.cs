using System;
using DiscordChatExporter.Core.Exporting.Manifest;
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
}
