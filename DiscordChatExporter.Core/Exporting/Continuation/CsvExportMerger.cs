using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Utils;

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
        string newText;
        try
        {
            newText = await File.ReadAllTextAsync(newRowsFilePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{newRowsFilePath}'.", ex);
        }

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
            AtomicFile.ReplaceWithBackupCleanup(tempPath, existingFilePath);
        }
        catch (Exception ex)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // best-effort
            }

            if (ex is OperationCanceledException)
                throw;

            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidExportException(
                    $"Could not continue the CSV export '{existingFilePath}'.",
                    ex
                );
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
