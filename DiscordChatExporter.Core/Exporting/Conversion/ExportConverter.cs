using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
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
        if (targetFormat is ExportFormat.Json)
            throw new InvalidExportException("JSON is not a supported conversion target.");

        var parsed = await JsonExportReader.ParseAsync(jsonFilePath, cancellationToken);
        return await ConvertAsync(
            parsed,
            jsonFilePath,
            outputFilePath,
            targetFormat,
            cancellationToken
        );
    }

    public static async ValueTask<ExportResult> ConvertAsync(
        ParsedExport parsed,
        string jsonFilePath,
        string outputFilePath,
        ExportFormat targetFormat,
        CancellationToken cancellationToken = default
    )
    {
        if (targetFormat is ExportFormat.Json)
            throw new InvalidExportException("JSON is not a supported conversion target.");

        var referencedUsers = parsed.Messages.SelectMany(m => m.GetReferencedUsers()).ToArray();
        var request = new ExportRequest(
            parsed.Guild,
            parsed.Channel,
            outputFilePath,
            null,
            targetFormat,
            parsed.After,
            parsed.Before,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: true,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );

        var context = new ExportContext(new DiscordClient("conversion-offline"), request, true);
        if (parsed.ConversionData is not null)
        {
            try
            {
                context.SeedFromConversionData(parsed.ConversionData, referencedUsers);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new InvalidExportException(
                    $"'{jsonFilePath}' contains malformed conversion metadata.",
                    ex
                );
            }
        }

        var exporter = new MessageExporter(context);
        try
        {
            foreach (var message in parsed.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var user in message.GetReferencedUsers())
                    await context.PopulateMemberAsync(user, cancellationToken);

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
