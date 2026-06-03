using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class ContinuationFormat
{
    public static bool IsSupportedExtension(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() is ".json" or ".html" or ".htm" or ".csv";

    public static ExportFormat FormatFor(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => ExportFormat.Json,
            ".html" or ".htm" => ExportFormat.HtmlDark,
            ".csv" => ExportFormat.Csv,
            var ext => throw new InvalidExportException(
                $"Continuing {ext} exports is not supported."
            ),
        };

    public static async ValueTask<ContinuationCutoff> ReadCutoffAsync(
        string filePath,
        CancellationToken cancellationToken = default
    ) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => await ReadJsonAsync(filePath, cancellationToken),
            ".html" or ".htm" => await HtmlExportInspector.InspectAsync(
                filePath,
                cancellationToken
            ),
            ".csv" => await CsvExportInspector.InspectAsync(filePath, cancellationToken),
            var ext => throw new InvalidExportException(
                $"Continuing {ext} exports is not supported."
            ),
        };

    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    ) =>
        Path.GetExtension(existingFilePath).ToLowerInvariant() switch
        {
            ".json" => await JsonExportMerger.MergeAsync(
                existingFilePath,
                newMessagesFilePath,
                exportedAt,
                cancellationToken
            ),
            ".html" or ".htm" => await HtmlExportMerger.MergeAsync(
                existingFilePath,
                newMessagesFilePath,
                cutoff,
                cancellationToken
            ),
            // CsvExportMerger returns only the number of rows it appended (it has no count to
            // recompute, unlike Json/Html). The dispatcher contract is "return the merged TOTAL",
            // so add the existing data-row count back on to keep parity with the other formats and
            // make the consumer's (total - ExistingCount) added-count come out right.
            ".csv" => cutoff.ExistingCount
                + await CsvExportMerger.MergeAsync(
                    existingFilePath,
                    newMessagesFilePath,
                    cutoff,
                    cancellationToken
                ),
            var ext => throw new InvalidExportException(
                $"Continuing {ext} exports is not supported."
            ),
        };

    private static async ValueTask<ContinuationCutoff> ReadJsonAsync(
        string filePath,
        CancellationToken ct
    )
    {
        var info = await JsonExportInspector.InspectAsync(filePath, ct);
        var channelId = FileNameChannelId.TryParse(filePath) ?? info.ChannelId;
        return new ContinuationCutoff(
            channelId,
            info.LastMessageId,
            info.Before,
            info.IsChronological,
            info.MessageCount,
            CutoffIsExact: true
        );
    }
}
