using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static partial class HtmlExportInspector
{
    private const string MessageContainerClass = "chatlog__message-container";
    private const string MessageIdAttribute = "data-message-id";

    internal readonly record struct MessageContainerTag(int Index, string MessageId);

    // Returns data-message-id values from real message-container tags only. This skips forged ids in
    // message-body text and in user-controlled attributes rendered inside message bodies, such as
    // links with data-message-id in their href or attribute values containing class/data-message-id.
    internal static IReadOnlyList<string> ExtractMessageIdStrings(string html) =>
        FindMessageContainerTags(html).Select(tag => tag.MessageId).ToArray();

    internal static IReadOnlyList<MessageContainerTag> FindMessageContainerTags(string html)
    {
        var tags = new List<MessageContainerTag>();
        for (var searchIndex = 0; searchIndex < html.Length; )
        {
            var tagStart = html.IndexOf("<div", searchIndex, StringComparison.OrdinalIgnoreCase);
            if (tagStart < 0)
                break;

            var tagNameEnd = tagStart + 4;
            if (tagNameEnd < html.Length && !IsTagNameBoundary(html[tagNameEnd]))
            {
                searchIndex = tagNameEnd;
                continue;
            }

            var tagEnd = FindTagEnd(html, tagNameEnd);
            if (tagEnd < 0)
                break;

            if (TryReadMessageContainerId(html, tagNameEnd, tagEnd, out var messageId))
                tags.Add(new MessageContainerTag(tagStart, messageId));

            searchIndex = tagEnd + 1;
        }

        return tags;
    }

    private static bool IsTagNameBoundary(char c) => char.IsWhiteSpace(c) || c is '>' or '/';

    private static int FindTagEnd(string html, int startIndex)
    {
        var quote = '\0';
        for (var i = startIndex; i < html.Length; i++)
        {
            var c = html[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }

            if (c is '\"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
                return i;
        }

        return -1;
    }

    private static bool TryReadMessageContainerId(
        string html,
        int startIndex,
        int tagEnd,
        out string messageId
    )
    {
        var classValue = string.Empty;
        messageId = string.Empty;

        for (var i = startIndex; i < tagEnd; )
        {
            while (i < tagEnd && char.IsWhiteSpace(html[i]))
                i++;

            if (i >= tagEnd || html[i] == '/')
                break;

            var nameStart = i;
            while (i < tagEnd && IsAttributeNameChar(html[i]))
                i++;

            if (nameStart == i)
            {
                i++;
                continue;
            }

            var name = html[nameStart..i];
            while (i < tagEnd && char.IsWhiteSpace(html[i]))
                i++;

            var value = string.Empty;
            if (i < tagEnd && html[i] == '=')
            {
                i++;
                while (i < tagEnd && char.IsWhiteSpace(html[i]))
                    i++;

                var valueStart = i;
                if (i < tagEnd && html[i] is '\"' or '\'')
                {
                    var quote = html[i++];
                    valueStart = i;
                    while (i < tagEnd && html[i] != quote)
                        i++;
                    value = html[valueStart..i];
                    if (i < tagEnd)
                        i++;
                }
                else
                {
                    while (i < tagEnd && !char.IsWhiteSpace(html[i]))
                        i++;
                    value = html[valueStart..i];
                }
            }

            if (name.Equals("class", StringComparison.OrdinalIgnoreCase))
                classValue = value;
            else if (name.Equals(MessageIdAttribute, StringComparison.OrdinalIgnoreCase))
                messageId = value;
        }

        return messageId.Length > 0 && HasClassToken(classValue, MessageContainerClass);
    }

    private static bool IsAttributeNameChar(char c) =>
        !char.IsWhiteSpace(c) && c is not '=' and not '>' and not '/';

    private static bool HasClassToken(string classValue, string expectedToken)
    {
        for (var i = 0; i < classValue.Length; )
        {
            while (i < classValue.Length && char.IsWhiteSpace(classValue[i]))
                i++;

            var tokenStart = i;
            while (i < classValue.Length && !char.IsWhiteSpace(classValue[i]))
                i++;

            if (
                i > tokenStart
                && classValue
                    .AsSpan(tokenStart, i - tokenStart)
                    .SequenceEqual(expectedToken.AsSpan())
            )
            {
                return true;
            }
        }

        return false;
    }

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

        var ids = ExtractMessageIdStrings(text)
            .Select(s => Snowflake.TryParse(s))
            .Where(id => id is not null)
            .Select(id => id!.Value)
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
