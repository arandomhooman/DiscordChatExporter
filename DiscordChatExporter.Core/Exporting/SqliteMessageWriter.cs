using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Markdown.Parsing;
using Microsoft.Data.Sqlite;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal class SqliteMessageWriter : MessageWriter
{
    private const string SchemaSql = """
        CREATE TABLE export_info (
            guild_id       TEXT,
            guild_name     TEXT,
            guild_icon_url TEXT,
            channel_id     TEXT,
            channel_name   TEXT,
            channel_topic  TEXT,
            category_id    TEXT,
            category       TEXT,
            after          TEXT,
            before         TEXT,
            exported_at    TEXT,
            message_count  INTEGER
        );

        CREATE TABLE authors (
            id            TEXT PRIMARY KEY,
            name          TEXT NOT NULL,
            discriminator TEXT,
            nickname      TEXT,
            color         TEXT,
            is_bot        INTEGER NOT NULL,
            avatar_url    TEXT
        );

        CREATE TABLE messages (
            id                   TEXT PRIMARY KEY,
            type                 TEXT NOT NULL,
            timestamp            TEXT NOT NULL,
            timestamp_edited     TEXT,
            call_ended_timestamp TEXT,
            is_pinned            INTEGER NOT NULL,
            content              TEXT NOT NULL,
            author_id            TEXT NOT NULL,
            reference_message_id TEXT
        );

        CREATE TABLE attachments (
            message_id      TEXT NOT NULL,
            id              TEXT NOT NULL,
            url             TEXT NOT NULL,
            file_name       TEXT NOT NULL,
            file_size_bytes INTEGER NOT NULL
        );

        CREATE TABLE reactions (
            message_id  TEXT NOT NULL,
            emoji_id    TEXT,
            emoji_name  TEXT NOT NULL,
            emoji_code  TEXT NOT NULL,
            is_animated INTEGER NOT NULL,
            count       INTEGER NOT NULL
        );

        CREATE VIRTUAL TABLE messages_fts USING fts5(content, message_id UNINDEXED);
        """;

    private readonly string _databaseFilePath;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;

    public SqliteMessageWriter(string databaseFilePath, ExportContext context)
        : base(Stream.Null, context)
    {
        _databaseFilePath = databaseFilePath;
    }

    private async ValueTask<string> FormatMarkdownAsync(
        string markdown,
        CancellationToken cancellationToken = default
    ) =>
        Context.Request.ShouldFormatMarkdown
            ? await PlainTextMarkdownVisitor.FormatAsync(Context, markdown, cancellationToken)
            : markdown;

    private string? NormalizeOrNull(DateTimeOffset? instant) =>
        instant is { } value
            ? Context.NormalizeDate(value).ToString("o", CultureInfo.InvariantCulture)
            : null;

    private static void AddParameter(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public override async ValueTask WritePreambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Start from a clean slate so a re-export never merges into stale data and the
        // manifest/hashing layer (#1) sees a single self-contained file.
        DeleteDatabaseFiles(_databaseFilePath);

        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databaseFilePath,
                // Pooling keeps the OS file handle alive after Close(), which collides with the
                // manifest hashing (#1) and the delete-on-re-export step (#3). Disable it.
                Pooling = false,
            }.ToString()
        );
        await _connection.OpenAsync(cancellationToken);

        // DELETE journal => no -wal/-shm sidecars; the export stays a single file.
        // Must run before the transaction begins.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=DELETE;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        _transaction = (SqliteTransaction)
            await _connection.BeginTransactionAsync(cancellationToken);

        using (var schema = _connection.CreateCommand())
        {
            schema.Transaction = _transaction;
            schema.CommandText = SchemaSql;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        // One self-describing row. message_count is a placeholder finalized in the postamble.
        using var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = """
            INSERT INTO export_info
                (guild_id, guild_name, guild_icon_url, channel_id, channel_name, channel_topic,
                 category_id, category, after, before, exported_at, message_count)
            VALUES
                ($guildId, $guildName, $guildIconUrl, $channelId, $channelName, $channelTopic,
                 $categoryId, $category, $after, $before, $exportedAt, 0);
            """;
        AddParameter(command, "$guildId", Context.Request.Guild.Id.ToString());
        AddParameter(command, "$guildName", Context.Request.Guild.Name);
        AddParameter(
            command,
            "$guildIconUrl",
            await Context.ResolveAssetUrlAsync(Context.Request.Guild.IconUrl, cancellationToken)
        );
        AddParameter(command, "$channelId", Context.Request.Channel.Id.ToString());
        AddParameter(command, "$channelName", Context.Request.Channel.Name);
        AddParameter(command, "$channelTopic", Context.Request.Channel.Topic);
        AddParameter(command, "$categoryId", Context.Request.Channel.Parent?.Id.ToString());
        AddParameter(command, "$category", Context.Request.Channel.Parent?.Name);
        AddParameter(command, "$after", NormalizeOrNull(Context.Request.After?.ToDate()));
        AddParameter(command, "$before", NormalizeOrNull(Context.Request.Before?.ToDate()));
        AddParameter(
            command,
            "$exportedAt",
            Context.NormalizeDate(DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture)
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async ValueTask WriteMessageAsync(
        Message message,
        CancellationToken cancellationToken = default
    )
    {
        await base.WriteMessageAsync(message, cancellationToken);

        var content = message.IsSystemNotification
            ? message.GetFallbackContent()
            : await FormatMarkdownAsync(message.Content, cancellationToken);

        await WriteAuthorAsync(message.Author, cancellationToken);

        using (var command = _connection!.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText = """
                INSERT INTO messages
                    (id, type, timestamp, timestamp_edited, call_ended_timestamp,
                     is_pinned, content, author_id, reference_message_id)
                VALUES
                    ($id, $type, $timestamp, $timestampEdited, $callEnded,
                     $isPinned, $content, $authorId, $referenceMessageId);
                """;
            AddParameter(command, "$id", message.Id.ToString());
            AddParameter(command, "$type", message.Kind.ToString());
            AddParameter(
                command,
                "$timestamp",
                Context.NormalizeDate(message.Timestamp).ToString("o", CultureInfo.InvariantCulture)
            );
            AddParameter(command, "$timestampEdited", NormalizeOrNull(message.EditedTimestamp));
            AddParameter(command, "$callEnded", NormalizeOrNull(message.CallEndedTimestamp));
            AddParameter(command, "$isPinned", message.IsPinned ? 1 : 0);
            AddParameter(command, "$content", content);
            AddParameter(command, "$authorId", message.Author.Id.ToString());
            AddParameter(command, "$referenceMessageId", message.Reference?.MessageId?.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var command = _connection!.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText =
                "INSERT INTO messages_fts (content, message_id) VALUES ($content, $messageId);";
            AddParameter(command, "$content", content);
            AddParameter(command, "$messageId", message.Id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var attachment in message.Attachments)
        {
            using var command = _connection!.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = """
                INSERT INTO attachments (message_id, id, url, file_name, file_size_bytes)
                VALUES ($messageId, $id, $url, $fileName, $fileSizeBytes);
                """;
            AddParameter(command, "$messageId", message.Id.ToString());
            AddParameter(command, "$id", attachment.Id.ToString());
            AddParameter(
                command,
                "$url",
                await Context.ResolveAssetUrlAsync(attachment.Url, cancellationToken)
            );
            AddParameter(command, "$fileName", attachment.FileName);
            AddParameter(command, "$fileSizeBytes", attachment.FileSize.TotalBytes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Reactions: counts only. We deliberately do NOT call GetMessageReactionsAsync
        // (the per-user fetch), keeping the writer network-free.
        foreach (var reaction in message.Reactions)
        {
            using var command = _connection!.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = """
                INSERT INTO reactions (message_id, emoji_id, emoji_name, emoji_code, is_animated, count)
                VALUES ($messageId, $emojiId, $emojiName, $emojiCode, $isAnimated, $count);
                """;
            AddParameter(command, "$messageId", message.Id.ToString());
            AddParameter(command, "$emojiId", reaction.Emoji.Id?.ToString());
            AddParameter(command, "$emojiName", reaction.Emoji.Name);
            AddParameter(command, "$emojiCode", reaction.Emoji.Code);
            AddParameter(command, "$isAnimated", reaction.Emoji.IsAnimated ? 1 : 0);
            AddParameter(command, "$count", reaction.Count);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async ValueTask WriteAuthorAsync(User user, CancellationToken cancellationToken)
    {
        using var command = _connection!.CreateCommand();
        command.Transaction = _transaction;
        // OR IGNORE dedupes by the author id primary key.
        command.CommandText = """
            INSERT OR IGNORE INTO authors (id, name, discriminator, nickname, color, is_bot, avatar_url)
            VALUES ($id, $name, $discriminator, $nickname, $color, $isBot, $avatarUrl);
            """;
        AddParameter(command, "$id", user.Id.ToString());
        AddParameter(command, "$name", user.Name);
        AddParameter(command, "$discriminator", user.DiscriminatorFormatted);
        AddParameter(
            command,
            "$nickname",
            Context.TryGetMember(user.Id)?.DisplayName ?? user.DisplayName
        );
        AddParameter(command, "$color", Context.TryGetUserColor(user.Id)?.ToHexString());
        AddParameter(command, "$isBot", user.IsBot ? 1 : 0);
        AddParameter(
            command,
            "$avatarUrl",
            await Context.ResolveAssetUrlAsync(
                Context.TryGetMember(user.Id)?.AvatarUrl ?? user.AvatarUrl,
                cancellationToken
            )
        );
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override async ValueTask WritePostambleAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_connection is null || _transaction is null)
            return;

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = _transaction;
            command.CommandText = "UPDATE export_info SET message_count = $count;";
            AddParameter(command, "$count", MessagesWritten);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await _transaction.CommitAsync(cancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        // If the postamble never ran (e.g. an exception mid-export), disposing the transaction
        // rolls it back; disposing the connection releases the file handle (Pooling=False).
        if (_transaction is not null)
            await _transaction.DisposeAsync();

        if (_connection is not null)
            await _connection.DisposeAsync();

        await base.DisposeAsync();
    }

    private static void DeleteDatabaseFiles(string databaseFilePath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = databaseFilePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
