using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public sealed class MessageExporterDisposeSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceMessageExporterDispose_" + Guid.NewGuid().ToString("N")
    );

    public MessageExporterDisposeSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static ExportContext CreateContext(string outputPath) =>
        new(
            new DiscordClient("fake-token"),
            new ExportRequest(
                new Guild(new Snowflake(1), "Test Guild", ""),
                new Channel(
                    new Snowflake(2),
                    ChannelKind.GuildTextChat,
                    new Snowflake(1),
                    null,
                    "general",
                    null,
                    null,
                    null,
                    false,
                    null
                ),
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
            )
        );

    [Fact]
    public async Task Dispose_with_canceled_token_does_not_create_an_empty_export()
    {
        var outputPath = Path.Combine(_dir, "canceled.json");
        var exporter = new MessageExporter(CreateContext(outputPath));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await exporter.DisposeAsync(cancellation.Token);

        File.Exists(outputPath).Should().BeFalse();
    }
}
