using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class HtmlExportInspector
{
    [GeneratedRegex("data-message-id=\"?(\\d+)\"?")]
    internal static partial Regex MessageIdRegex();

    public static async ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        var channelId =
            FileNameChannelId.TryParse(filePath)
            ?? throw new InvalidExportException(
                "Could not determine the channel for this HTML export. "
                    + "Keep the default file name (it includes the channel id) or re-export."
            );

        string text;
        try
        {
            text = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidExportException($"Could not read '{filePath}'.", ex);
        }

        var ids = MessageIdRegex()
            .Matches(text)
            .Select(m => Snowflake.Parse(m.Groups[1].Value))
            .ToArray();

        if (ids.Length == 0)
            throw new InvalidExportException(
                "The HTML export contains no messages to continue from."
            );

        var first = ids[0];
        var last = ids[^1];
        return new ContinuationCutoff(
            channelId,
            last,
            null,
            IsChronological: first.Value <= last.Value,
            ExistingCount: ids.Length,
            CutoffIsExact: true
        );
    }
}
