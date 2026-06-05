using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class SqliteExportMerger
{
    public static async ValueTask<long> MergeAsync(
        string existingDatabaseFilePath,
        string newDatabaseFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = existingDatabaseFilePath,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=DELETE;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS incoming;";
            attach.Parameters.AddWithValue("$path", newDatabaseFilePath);
            await attach.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using var transaction = (SqliteTransaction)
                await connection.BeginTransactionAsync(cancellationToken);

            await using (var copy = connection.CreateCommand())
            {
                copy.Transaction = transaction;
                copy.CommandText = """
                    INSERT OR IGNORE INTO main.authors SELECT * FROM incoming.authors;
                    INSERT OR IGNORE INTO main.messages
                        SELECT * FROM incoming.messages WHERE CAST(id AS INTEGER) > $cutoff;
                    INSERT INTO main.attachments
                        SELECT * FROM incoming.attachments WHERE CAST(message_id AS INTEGER) > $cutoff;
                    INSERT INTO main.reactions
                        SELECT * FROM incoming.reactions WHERE CAST(message_id AS INTEGER) > $cutoff;
                    INSERT INTO main.messages_fts (content, message_id)
                        SELECT content, message_id FROM incoming.messages_fts
                        WHERE CAST(message_id AS INTEGER) > $cutoff;
                    """;
                copy.Parameters.AddWithValue("$cutoff", (long)cutoff.Cutoff.Value);
                await copy.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE export_info SET message_count = (SELECT COUNT(*) FROM main.messages), "
                    + "exported_at = $now;";
                update.Parameters.AddWithValue(
                    "$now",
                    exportedAt.ToString("o", CultureInfo.InvariantCulture)
                );
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            await using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE incoming;";
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM main.messages;";
            return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
    }
}
