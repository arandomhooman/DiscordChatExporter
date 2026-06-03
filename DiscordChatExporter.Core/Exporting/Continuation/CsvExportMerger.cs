using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class CsvExportMerger
{
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newRowsFilePath,
        ContinuationCutoff cutoff,
        CancellationToken cancellationToken = default
    )
    {
        var newText = await File.ReadAllTextAsync(newRowsFilePath, cancellationToken);
        var newRows = CsvExportInspector.ParseCsv(newText);

        var tempPath = existingFilePath + ".merging.tmp";
        long added = 0;
        try
        {
            File.Copy(existingFilePath, tempPath, true);
            await using (var writer = new StreamWriter(new FileStream(tempPath, FileMode.Append)))
            {
                for (var i = 1; i < newRows.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fields = newRows[i];
                    if (fields.Count < 3)
                        continue;
                    if (
                        DateTimeOffset.TryParse(
                            fields[2],
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out var date
                        )
                        && Snowflake.FromDate(date).Value <= cutoff.Cutoff.Value
                    )
                        continue;

                    await writer.WriteAsync(EncodeRow(fields));
                    added++;
                }
            }
            File.Replace(tempPath, existingFilePath, existingFilePath + ".bak");
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // best-effort
            }
            throw;
        }
        return added;
    }

    private static string EncodeRow(IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('"')
                .Append(fields[i].Replace("\"", "\"\"", StringComparison.Ordinal))
                .Append('"');
        }
        sb.Append("\r\n");
        return sb.ToString();
    }
}
