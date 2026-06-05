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
            string? lastIdText;
            await using (var messages = connection.CreateCommand())
            {
                messages.CommandText =
                    "SELECT COUNT(*), "
                    + "(SELECT id FROM messages ORDER BY CAST(id AS INTEGER) DESC LIMIT 1) "
                    + "FROM messages;";
                await using var reader = await messages.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                count = reader.GetInt64(0);
                lastIdText = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            var channelId = new Snowflake(ulong.Parse(channelIdText, CultureInfo.InvariantCulture));
            var before = ParseSnowflakeDate(beforeText);

            Snowflake cutoff;
            bool exact;
            if (lastIdText is not null)
            {
                cutoff = new Snowflake(ulong.Parse(lastIdText, CultureInfo.InvariantCulture));
                exact = true;
            }
            else
            {
                cutoff = ParseSnowflakeDate(afterText) ?? new Snowflake(0);
                exact = false;
            }

            return new ContinuationCutoff(channelId, cutoff, before, true, count, exact);
        }
        catch (SqliteException ex)
        {
            throw new InvalidExportException(
                $"'{filePath}' is not a valid SQLite chat export.",
                ex
            );
        }
    }

    private static Snowflake? ParseSnowflakeDate(string? text) =>
        !string.IsNullOrEmpty(text)
        && DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var date
        )
            ? Snowflake.FromDate(date)
            : null;
}
