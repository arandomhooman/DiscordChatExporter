using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class CsvExportInspector
{
    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        var channelId =
            FileNameChannelId.TryParse(filePath)
            ?? throw new InvalidExportException(
                "Could not determine the channel for this CSV export. "
                    + "Keep the default file name (it includes the channel id) or re-export."
            );

        if (ContinuationFileName.HasBeforeBoundHint(filePath))
        {
            throw new InvalidExportException(
                "CSV exports with a 'before' date range cannot be continued safely. "
                    + "Continue the original JSON or SQLite export instead."
            );
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{filePath}'.", ex);
        }

        var rows = ParseCsv(text);
        if (rows.Count <= 1)
            throw new InvalidExportException(
                "The CSV export contains no messages to continue from."
            );

        DateTimeOffset? firstDate = null;
        DateTimeOffset? lastDate = null;
        var orderDirection = 0;
        long count = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var fields = rows[i];
            if (fields.Count < 3)
                continue;
            if (
                DateTimeOffset.TryParse(
                    fields[2],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var date
                )
            )
            {
                TrackDateOrder(lastDate, date, ref orderDirection);
                firstDate ??= date;
                lastDate = date;
            }
            count++;
        }

        if (lastDate is null)
            throw new InvalidExportException("The CSV export has no parseable message dates.");

        var isChronological = firstDate <= lastDate;
        return new ContinuationCutoff(
            channelId,
            Snowflake.FromDate(lastDate.Value),
            null,
            isChronological,
            count,
            CutoffIsExact: false
        );
    }

    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }
            switch (c)
            {
                case '"':
                    inQuotes = true;
                    i++;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    i++;
                    break;
                case '\r':
                    i++;
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    i++;
                    break;
                default:
                    field.Append(c);
                    i++;
                    break;
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static void TrackDateOrder(
        DateTimeOffset? previousDate,
        DateTimeOffset currentDate,
        ref int orderDirection
    )
    {
        if (previousDate is null)
            return;

        var comparison = currentDate.CompareTo(previousDate.Value);
        if (comparison == 0)
            return;

        var direction = Math.Sign(comparison);
        if (orderDirection == 0)
        {
            orderDirection = direction;
            return;
        }

        if (orderDirection != direction)
        {
            throw new InvalidExportException(
                "The CSV export's messages are not consistently ordered."
            );
        }
    }
}
