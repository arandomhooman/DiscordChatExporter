using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class ExportConverterSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceConverter_" + Guid.NewGuid().ToString("N")
    );

    public ExportConverterSpecs() => Directory.CreateDirectory(_dir);

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

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
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

    private async Task<string> WriteJsonAsync()
    {
        var path = Path.Combine(_dir, "chat.json");
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        await writer.WriteMessageAsync(CreateMessage(1001, author, "the quick brown fox"));
        await writer.WritePostambleAsync();
        return path;
    }

    [Fact]
    public async Task Converter_writes_sqlite_and_csv_outputs_from_json()
    {
        var jsonPath = await WriteJsonAsync();
        var dbOut = Path.Combine(_dir, "out.db");
        var csvOut = Path.Combine(_dir, "out.csv");

        await ExportConverter.ConvertAsync(jsonPath, dbOut, ExportFormat.Db);
        await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);

        (await SqliteExportReader.SearchAsync(dbOut, "quick", 50, default))
            .Should()
            .ContainSingle();
        (await File.ReadAllTextAsync(csvOut)).Should().Contain("quick brown fox");
    }
}
