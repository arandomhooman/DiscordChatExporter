using System;
using System.IO;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class ExportContextSeedSpecs
{
    private static ExportContext CreateContext()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "conversion-seed.json");
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "test-channel",
            0,
            null,
            null,
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            null,
            ExportFormat.Json,
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

    [Fact]
    public void SeedFromConversionData_populates_member_role_and_channel_caches()
    {
        var context = CreateContext();
        var data = new ConversionData(
            [
                new ConversionMember(
                    "10",
                    "Alice Display",
                    "https://cdn.example/avatar.png",
                    "#FF0000",
                    ["20"]
                ),
            ],
            [new ConversionRole("20", "red", "#FF0000", 5)],
            [new ConversionChannel("30", "other-channel")]
        );

        context.SeedFromConversionData(data);

        context.TryGetMember(new Snowflake(10))!.DisplayName.Should().Be("Alice Display");
        context.TryGetUserColor(new Snowflake(10))!.Value.Name.Should().Be("ffff0000");
        context.TryGetChannel(new Snowflake(30))!.Name.Should().Be("other-channel");
    }
}
