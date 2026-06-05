using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using Microsoft.Data.Sqlite;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class SqliteExportInspector
{
    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            throw new InvalidExportException($"The file '{filePath}' does not exist.");

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );

        try
        {
            await connection.OpenAsync(cancellationToken);

            string channelIdText;
            string? afterText;
            string? beforeText;
            await using (var info = connection.CreateCommand())
            {
                info.CommandText = "SELECT channel_id, after, before FROM export_info LIMIT 1;";
                await using var reader = await info.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidExportException(
                        "The SQLite export has no export_info row to continue from."
                    );

                channelIdText = reader.GetString(0);
                afterText = reader.IsDBNull(1) ? null : reader.GetString(1);
                beforeText = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            long count;
            string? firstIdText;
            string? lastIdText;
            await using (var messages = connection.CreateCommand())
            {
                messages.CommandText =
                    "SELECT COUNT(*), "
                    + "(SELECT id FROM messages ORDER BY rowid ASC LIMIT 1), "
                    + "(SELECT id FROM messages ORDER BY rowid DESC LIMIT 1) "
                    + "FROM messages;";
                await using var reader = await messages.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                count = reader.GetInt64(0);
                firstIdText = reader.IsDBNull(1) ? null : reader.GetString(1);
                lastIdText = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            var channelId = new Snowflake(ulong.Parse(channelIdText, CultureInfo.InvariantCulture));
            var before = ParseOptionalSnowflakeDate(beforeText, "before");
            var after = ParseOptionalSnowflakeDate(afterText, "after");

            Snowflake cutoff;
            bool exact;
            var isChronological = true;
            if (lastIdText is not null)
            {
                cutoff = new Snowflake(ulong.Parse(lastIdText, CultureInfo.InvariantCulture));
                exact = true;
                if (firstIdText is not null)
                {
                    var first = new Snowflake(
                        ulong.Parse(firstIdText, CultureInfo.InvariantCulture)
                    );
                    isChronological = first.Value <= cutoff.Value;
                }
            }
            else
            {
                cutoff = after ?? new Snowflake(0);
                exact = false;
            }

            return new ContinuationCutoff(channelId, cutoff, before, isChronological, count, exact);
        }
        catch (SqliteException ex)
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a valid SQLite chat export.",
                ex
            );
        }
        catch (Exception ex)
            when (ex
                    is FormatException
                        or InvalidCastException
                        or InvalidOperationException
                        or OverflowException
            )
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a valid SQLite chat export.",
                ex
            );
        }
    }

    private static Snowflake? ParseOptionalSnowflakeDate(string? text, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var date
            )
        )
        {
            return Snowflake.FromDate(date);
        }

        throw new InvalidExportException(
            $"The SQLite export has a malformed '{fieldName}' date bound."
        );
    }
}
