using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;

namespace DiscordChatExporter.Core.Exporting.Conversion;

public static class ExportConverter
{
    public static async ValueTask<ExportResult> ConvertAsync(
        string jsonFilePath,
        string outputFilePath,
        ExportFormat targetFormat,
        CancellationToken cancellationToken = default
    )
    {
        var parsed = await JsonExportReader.ParseAsync(jsonFilePath, cancellationToken);
        var request = new ExportRequest(
            parsed.Guild,
            parsed.Channel,
            outputFilePath,
            null,
            targetFormat,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

        var context = new ExportContext(new DiscordClient("conversion-offline"), request);
        if (parsed.ConversionData is not null)
            context.SeedFromConversionData(parsed.ConversionData);

        var exporter = new MessageExporter(context);
        try
        {
            foreach (var message in parsed.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await exporter.ExportMessageAsync(message, cancellationToken);
            }
        }
        finally
        {
            await exporter.DisposeAsync();
        }

        return new ExportResult(exporter.Files, exporter.MessagesExported, 0);
    }
}
