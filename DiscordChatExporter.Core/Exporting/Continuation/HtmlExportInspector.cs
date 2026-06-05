using System;
using System.Collections.Generic;
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

        if (ContinuationFileName.HasBeforeBoundHint(filePath))
        {
            throw new InvalidExportException(
                "HTML exports with a 'before' date range cannot be continued safely. "
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

        var ids = MessageIdRegex()
            .Matches(text)
            .Select(m => Snowflake.Parse(m.Groups[1].Value))
            .ToArray();

        if (ids.Length == 0)
            throw new InvalidExportException(
                "The HTML export contains no messages to continue from."
            );

        EnsureConsistentMessageOrder(ids);

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

    private static void EnsureConsistentMessageOrder(IReadOnlyList<Snowflake> ids)
    {
        var direction = 0;
        for (var i = 1; i < ids.Count; i++)
        {
            var comparison = ids[i].Value.CompareTo(ids[i - 1].Value);
            if (comparison == 0)
                throw new InvalidExportException("The HTML export contains duplicate messages.");

            var currentDirection = Math.Sign(comparison);
            if (direction == 0)
            {
                direction = currentDirection;
                continue;
            }

            if (direction != currentDirection)
            {
                throw new InvalidExportException(
                    "The HTML export's messages are not consistently ordered."
                );
            }
        }
    }
}
