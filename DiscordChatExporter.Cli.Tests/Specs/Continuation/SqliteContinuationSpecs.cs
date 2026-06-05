using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class SqliteContinuationSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceSqliteCont_" + Guid.NewGuid().ToString("N")
    );

    public SqliteContinuationSpecs() => Directory.CreateDirectory(_dir);

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

    private async Task<string> WriteDbAsync(
        string fileName,
        params (ulong id, string content)[] messages
    )
    {
        var path = Path.Combine(_dir, fileName);
        var author = CreateUser(10, "alice");
        await using var writer = new SqliteMessageWriter(path, CreateContext(path));
        await writer.WritePreambleAsync();
        foreach (var (id, content) in messages)
            await writer.WriteMessageAsync(CreateMessage(id, author, content));
        await writer.WritePostambleAsync();
        return path;
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString()
        );
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Inspector_reads_channel_id_cutoff_and_count()
    {
        var path = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"), (1003, "c"));

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.ChannelId.Should().Be(new Snowflake(2));
        cutoff.Cutoff.Should().Be(new Snowflake(1003));
        cutoff.ExistingCount.Should().Be(3);
        cutoff.IsChronological.Should().BeTrue();
        cutoff.CutoffIsExact.Should().BeTrue();
    }

    [Fact]
    public async Task Inspector_handles_an_empty_export()
    {
        var path = await WriteDbAsync("empty.db");

        var cutoff = await SqliteExportInspector.InspectAsync(path);

        cutoff.ExistingCount.Should().Be(0);
        cutoff.CutoffIsExact.Should().BeFalse();
    }

    [Fact]
    public async Task Inspector_rejects_a_non_sqlite_file()
    {
        var path = Path.Combine(_dir, "notadb.db");
        await File.WriteAllTextAsync(path, "this is not a database");

        var act = async () => await SqliteExportInspector.InspectAsync(path);

        await act.Should().ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Merger_appends_new_messages_and_updates_count_and_fts()
    {
        var existing = await WriteDbAsync(
            "chat.db",
            (1001, "old one"),
            (1002, "old two"),
            (1003, "old three")
        );
        var incoming = await WriteDbAsync("new.db", (1004, "fresh four"), (1005, "fresh five"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        var total = await SqliteExportMerger.MergeAsync(
            existing,
            incoming,
            cutoff,
            DateTimeOffset.UnixEpoch
        );

        total.Should().Be(5);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(5);
        Count(connection, "SELECT COUNT(*) FROM messages_fts;").Should().Be(5);
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(5);

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'fresh';";
        ((long)query.ExecuteScalar()!).Should().Be(2);
    }

    [Fact]
    public async Task Merger_ignores_a_boundary_duplicate_message()
    {
        var existing = await WriteDbAsync("chat.db", (1001, "a"), (1002, "b"), (1003, "c"));
        var incoming = await WriteDbAsync("new.db", (1003, "c again"), (1004, "d"), (1005, "e"));
        var cutoff = await SqliteExportInspector.InspectAsync(existing);

        var total = await SqliteExportMerger.MergeAsync(
            existing,
            incoming,
            cutoff,
            DateTimeOffset.UnixEpoch
        );

        total.Should().Be(5);
        using var connection = OpenReadOnly(existing);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(5);

        using var query = connection.CreateCommand();
        query.CommandText = "SELECT content FROM messages WHERE id = '1003';";
        ((string)query.ExecuteScalar()!).Should().Be("c");
    }

    [Fact]
    public void ContinuationFormat_supports_sqlite_exports()
    {
        ContinuationFormat.IsSupportedExtension("chat.db").Should().BeTrue();
        ContinuationFormat.FormatFor("chat.db").Should().Be(ExportFormat.Db);
    }
}
