using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class SqliteMessageWriterSpecs : IDisposable
{
    private readonly string _dirPath = Path.Combine(
        Path.GetTempPath(),
        "DceSqliteTest_" + Guid.NewGuid().ToString("N")
    );

    public SqliteMessageWriterSpecs() => Directory.CreateDirectory(_dirPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dirPath, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string DbPath => Path.Combine(_dirPath, "export.db");

    // A fully offline context. shouldDownloadAssets=false makes ResolveAssetUrlAsync a no-op,
    // and shouldFormatMarkdown=false keeps message content as the raw string we assert on.
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
            "a topic",
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

    private static User CreateUser(ulong id, string name, bool isBot = false) =>
        new(new Snowflake(id), isBot, null, name, name, "");

    private static Message CreateMessage(
        ulong id,
        User author,
        string content,
        IReadOnlyList<Attachment>? attachments = null,
        IReadOnlyList<Reaction>? reactions = null
    ) =>
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
            attachments ?? [],
            [],
            [],
            reactions ?? [],
            [],
            null,
            null,
            null,
            null
        );

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
    public async Task It_writes_messages_authors_attachments_reactions_and_metadata()
    {
        // Arrange
        var alice = CreateUser(10, "alice");
        var attachment = new Attachment(
            new Snowflake(100),
            "https://cdn.example/pic.png",
            "pic.png",
            null,
            null,
            null,
            FileSize.FromBytes(2048)
        );
        var reaction = new Reaction(new Emoji(null, "👍", false), 3);

        var msg1 = CreateMessage(1001, alice, "the quick brown fox", [attachment], [reaction]);
        var msg2 = CreateMessage(1002, alice, "another plain message");

        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(msg1);
            await writer.WriteMessageAsync(msg2);
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);

        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(2);
        // Both messages share one author => deduped to a single row.
        Count(connection, "SELECT COUNT(*) FROM authors;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM attachments;").Should().Be(1);
        Count(connection, "SELECT COUNT(*) FROM reactions;").Should().Be(1);
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(2);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT content FROM messages WHERE id = '1001';";
            ((string)command.ExecuteScalar()!).Should().Be("the quick brown fox");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count FROM reactions WHERE message_id = '1001';";
            ((long)command.ExecuteScalar()!).Should().Be(3);
        }

        // Standard (non-custom) emoji has no id => NULL.
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT emoji_id FROM reactions WHERE message_id = '1001';";
            command.ExecuteScalar().Should().Be(DBNull.Value);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT guild_name FROM export_info;";
            ((string)command.ExecuteScalar()!).Should().Be("Test Guild");
        }
    }

    [Fact]
    public async Task It_indexes_message_content_for_full_text_search()
    {
        // Arrange
        var alice = CreateUser(10, "alice");
        var msg1 = CreateMessage(2001, alice, "discord export to sqlite");
        var msg2 = CreateMessage(2002, alice, "completely unrelated content");

        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(msg1);
            await writer.WriteMessageAsync(msg2);
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT message_id FROM messages_fts WHERE messages_fts MATCH $term ORDER BY rank;";
        command.Parameters.AddWithValue("$term", "sqlite");

        var matches = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            matches.Add(reader.GetString(0));

        matches.Should().ContainSingle().Which.Should().Be("2001");
    }

    [Fact]
    public async Task It_produces_a_valid_database_for_an_empty_export()
    {
        // Act — no messages written, just preamble + postamble (the empty-channel path).
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WritePostambleAsync();
        }

        // Assert
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(0);
        Count(connection, "SELECT message_count FROM export_info;").Should().Be(0);
        // The FTS table exists and is queryable.
        Count(connection, "SELECT COUNT(*) FROM messages_fts;").Should().Be(0);
    }

    [Fact]
    public async Task It_leaves_a_single_file_with_no_journal_sidecars_and_releases_the_handle()
    {
        // Act
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(CreateMessage(3001, CreateUser(10, "alice"), "hi"));
            await writer.WritePostambleAsync();
        }

        // Assert — DELETE journal + Pooling=False means only "export.db" remains...
        var leftovers = Directory
            .GetFiles(_dirPath, "export.db*")
            .Select(Path.GetFileName)
            .ToArray();
        leftovers.Should().BeEquivalentTo(["export.db"]);

        // ...and the handle is fully released, so the file can be deleted.
        var delete = () => File.Delete(DbPath);
        delete.Should().NotThrow();
    }

    [Fact]
    public async Task It_overwrites_an_existing_database_on_re_export()
    {
        var context = CreateContext(DbPath);

        // First export.
        await using (var writer = new SqliteMessageWriter(DbPath, context))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(4001, CreateUser(10, "alice"), "first run")
            );
            await writer.WritePostambleAsync();
        }

        // Second export over the same path.
        await using (var writer = new SqliteMessageWriter(DbPath, CreateContext(DbPath)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(4002, CreateUser(11, "bob"), "second run")
            );
            await writer.WritePostambleAsync();
        }

        // Assert — only the second run's data is present.
        using var connection = OpenReadOnly(DbPath);
        Count(connection, "SELECT COUNT(*) FROM messages;").Should().Be(1);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM messages;";
        ((string)command.ExecuteScalar()!).Should().Be("4002");
    }
}
