using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class SqliteExportReaderSpecs : IDisposable
{
    private readonly string _dirPath = Path.Combine(
        Path.GetTempPath(),
        "DceLibTest_" + Guid.NewGuid().ToString("N")
    );

    public SqliteExportReaderSpecs() => Directory.CreateDirectory(_dirPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dirPath, true);
        }
        catch { }
    }

    // --- synthetic offline context + message builders (mirrors SqliteMessageWriterSpecs) ---
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

    private async Task<string> WriteDbAsync(
        string fileName,
        params (ulong id, string content)[] messages
    )
    {
        var dbPath = Path.Combine(_dirPath, fileName);
        var author = CreateUser(10, "alice");
        await using var writer = new SqliteMessageWriter(dbPath, CreateContext(dbPath));
        await writer.WritePreambleAsync();
        foreach (var (id, content) in messages)
            await writer.WriteMessageAsync(CreateMessage(id, author, content));
        await writer.WritePostambleAsync();
        return dbPath;
    }

    [Fact]
    public async Task It_finds_messages_matching_a_term()
    {
        var db = await WriteDbAsync("a.db", (1, "the quick brown fox"), (2, "lazy dog sleeps"));

        var hits = await SqliteExportReader.SearchAsync(db, "fox", 50, default);

        hits.Should().ContainSingle();
        hits[0].MessageId.Should().Be("1");
        hits[0].AuthorName.Should().Be("alice");
        hits[0].DatabaseFilePath.Should().Be(db);
        hits[0].Snippet.Should().Contain("fox");
    }

    [Fact]
    public async Task Multi_word_queries_match_all_terms()
    {
        var db = await WriteDbAsync("a.db", (1, "quick brown fox"), (2, "quick lazy cat"));

        // Implicit AND of the two terms -> only message 1 has both.
        var hits = await SqliteExportReader.SearchAsync(db, "quick fox", 50, default);

        hits.Select(h => h.MessageId).Should().Equal("1");
    }

    [Fact]
    public async Task Special_characters_in_the_query_do_not_throw()
    {
        var db = await WriteDbAsync("a.db", (1, "hello world"));

        // FTS5 operator characters would break a raw MATCH; the reader must quote/escape.
        var act = async () => await SqliteExportReader.SearchAsync(db, "\"(foo* AND", 50, default);

        await act.Should().NotThrowAsync();
        (await SqliteExportReader.SearchAsync(db, "\"(foo* AND", 50, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_blank_query_returns_no_hits()
    {
        var db = await WriteDbAsync("a.db", (1, "hello world"));

        (await SqliteExportReader.SearchAsync(db, "   ", 50, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAcross_aggregates_and_skips_bad_paths()
    {
        var db1 = await WriteDbAsync("a.db", (1, "alpha shared"));
        var db2 = await WriteDbAsync("b.db", (2, "beta shared"));
        var missing = Path.Combine(_dirPath, "does-not-exist.db");

        var hits = await SqliteExportReader.SearchAcrossAsync(
            [db1, missing, db2],
            "shared",
            50,
            default
        );

        hits.Select(h => h.DatabaseFilePath).Should().BeEquivalentTo([db1, db2]);
        hits.Should().HaveCount(2);
    }
}
