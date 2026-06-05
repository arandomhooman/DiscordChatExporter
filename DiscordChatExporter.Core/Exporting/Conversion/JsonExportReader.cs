using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Exporting.Continuation;

namespace DiscordChatExporter.Core.Exporting.Conversion;

public sealed record ParsedExport(
    Guild Guild,
    Channel Channel,
    IReadOnlyList<Message> Messages,
    ConversionData? ConversionData,
    bool HasConversionDataBlock,
    Snowflake? After,
    Snowflake? Before
);

public static class JsonExportReader
{
    public static async ValueTask<ParsedExport> ParseAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            throw new InvalidExportException($"The file '{filePath}' does not exist.");

        try
        {
            await using var stream = File.OpenRead(filePath);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken
            );
            var root = document.RootElement;

            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("guild", out var guildJson)
                || !root.TryGetProperty("channel", out var channelJson)
                || !root.TryGetProperty("messages", out var messagesJson)
                || messagesJson.ValueKind != JsonValueKind.Array
            )
            {
                throw new InvalidExportException(
                    "This file is not a DiscordChatExporter JSON export."
                );
            }

            var guild = ParseGuild(guildJson);
            var channel = ParseChannel(channelJson, guild.Id);
            var (after, before) = root.TryGetProperty("dateRange", out var dateRangeJson)
                ? ParseDateRange(dateRangeJson)
                : (null, null);
            var messages = RelinkReferencedMessages(
                messagesJson.EnumerateArray().Select(ParseMessage).ToArray()
            );
            var inlineEmojis = messagesJson
                .EnumerateArray()
                .SelectMany(ParseInlineEmojis)
                .DistinctBy(emoji => (emoji.Id, emoji.Name, emoji.IsAnimated))
                .ToArray();
            var hasConversionDataBlock = root.TryGetProperty(
                "conversionData",
                out var conversionDataJson
            );
            var conversionData = hasConversionDataBlock ? ParseConversionData(conversionDataJson) : null;
            conversionData = MergeConversionData(conversionData, inlineEmojis);

            return new ParsedExport(
                guild,
                channel,
                messages,
                conversionData,
                hasConversionDataBlock,
                after,
                before
            );
        }
        catch (InvalidExportException)
        {
            throw;
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or UnauthorizedAccessException
                        or JsonException
                        or FormatException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or OverflowException
            )
        {
            throw new InvalidExportException($"'{filePath}' is not a valid JSON chat export.", ex);
        }
    }

    private static Guild ParseGuild(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "name"),
            GetString(json, "iconUrl")
        );

    private static Channel ParseChannel(JsonElement json, Snowflake guildId)
    {
        var parent = GetStringOrNull(json, "categoryId") is { } categoryId
            ? new Channel(
                ParseSnowflake(categoryId),
                ChannelKind.GuildCategory,
                guildId,
                null,
                GetStringOrNull(json, "category") ?? categoryId,
                null,
                null,
                null,
                false,
                null
            )
            : null;

        return new Channel(
            ParseSnowflake(json.GetProperty("id")),
            ParseEnum(GetString(json, "type"), ChannelKind.GuildTextChat),
            guildId,
            parent,
            GetString(json, "name"),
            null,
            GetStringOrNull(json, "iconUrl"),
            GetStringOrNull(json, "topic"),
            false,
            null
        );
    }

    private static Message ParseMessage(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            ParseEnum(GetString(json, "type"), MessageKind.Default),
            (MessageFlags)GetInt32(json, "flags"),
            ParseUser(json.GetProperty("author")),
            ParseDate(GetString(json, "timestamp")),
            ParseDateOrNull(json, "timestampEdited"),
            ParseDateOrNull(json, "callEndedTimestamp"),
            GetBoolean(json, "isPinned"),
            GetStringOrNull(json, "contentRaw") ?? GetString(json, "content"),
            ParseArray(json, "attachments", ParseAttachment),
            ParseArray(json, "embeds", ParseEmbed),
            ParseArray(json, "stickers", ParseSticker),
            ParseArray(json, "reactions", ParseReaction),
            ParseArray(json, "mentions", ParseUser),
            json.TryGetProperty("reference", out var referenceJson)
                ? ParseMessageReference(referenceJson)
                : null,
            json.TryGetProperty("referencedMessage", out var referencedJson)
                ? ParseMessage(referencedJson)
                : null,
            json.TryGetProperty("forwardedMessage", out var forwardedJson)
                ? ParseMessageSnapshot(forwardedJson)
                : null,
            json.TryGetProperty("interaction", out var interactionJson)
                ? ParseInteraction(interactionJson)
                : null
        );

    private static User ParseUser(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetBoolean(json, "isBot"),
            int.TryParse(GetStringOrNull(json, "discriminator"), out var discriminator)
            && discriminator > 0
                ? discriminator
                : null,
            GetString(json, "name"),
            GetStringOrNull(json, "nickname") ?? GetString(json, "name"),
            GetStringOrNull(json, "avatarUrl") ?? ""
        );

    private static Attachment ParseAttachment(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "url"),
            GetString(json, "fileName"),
            GetStringOrNull(json, "description"),
            GetInt32OrNull(json, "width"),
            GetInt32OrNull(json, "height"),
            FileSize.FromBytes(GetInt64(json, "fileSizeBytes"))
        );

    private static Embed ParseEmbed(JsonElement json) =>
        new(
            GetStringOrNull(json, "titleRaw") ?? GetStringOrNull(json, "title"),
            ParseEnum(GetStringOrNull(json, "type"), InferLegacyEmbedKind(json)),
            GetStringOrNull(json, "url"),
            ParseDateOrNull(json, "timestamp"),
            ParseColor(GetStringOrNull(json, "color")),
            json.TryGetProperty("author", out var author) ? ParseEmbedAuthor(author) : null,
            GetStringOrNull(json, "descriptionRaw") ?? GetStringOrNull(json, "description"),
            ParseArray(json, "fields", ParseEmbedField),
            json.TryGetProperty("thumbnail", out var thumbnail) ? ParseEmbedImage(thumbnail) : null,
            ParseArray(json, "images", ParseEmbedImage),
            json.TryGetProperty("video", out var video) ? ParseEmbedVideo(video) : null,
            json.TryGetProperty("footer", out var footer) ? ParseEmbedFooter(footer) : null
        );

    private static EmbedKind InferLegacyEmbedKind(JsonElement json)
    {
        if (json.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object)
            return HasGifvUrl(json) ? EmbedKind.Gifv : EmbedKind.Video;

        if (HasImage(json) && IsBareMediaEmbed(json))
            return EmbedKind.Image;

        return !string.IsNullOrWhiteSpace(GetStringOrNull(json, "url"))
            ? EmbedKind.Link
            : EmbedKind.Rich;
    }

    private static bool HasGifvUrl(JsonElement json)
    {
        var urls = new[]
        {
            GetStringOrNull(json, "url"),
            json.TryGetProperty("video", out var video) ? GetStringOrNull(video, "url") : null,
            json.TryGetProperty("video", out video) ? GetStringOrNull(video, "canonicalUrl") : null,
        };

        return urls.Any(url =>
            url?.EndsWith(".gifv", StringComparison.OrdinalIgnoreCase) == true
            || url?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true
        );
    }

    private static bool HasImage(JsonElement json) =>
        json.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.Object
        || json.TryGetProperty("thumbnail", out var thumbnail)
            && thumbnail.ValueKind == JsonValueKind.Object
        || json.TryGetProperty("images", out var images)
            && images.ValueKind == JsonValueKind.Array
            && images.GetArrayLength() > 0;

    private static bool IsBareMediaEmbed(JsonElement json) =>
        string.IsNullOrWhiteSpace(GetStringOrNull(json, "title"))
        && string.IsNullOrWhiteSpace(GetStringOrNull(json, "description"))
        && !HasNonNullProperty(json, "author")
        && !HasNonNullProperty(json, "footer")
        && !HasNonNullProperty(json, "color")
        && !HasNonNullProperty(json, "timestamp")
        && (
            !json.TryGetProperty("fields", out var fields)
            || fields.ValueKind != JsonValueKind.Array
            || fields.GetArrayLength() == 0
        );

    private static bool HasNonNullProperty(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind != JsonValueKind.Null;

    private static EmbedAuthor ParseEmbedAuthor(JsonElement json) =>
        new(
            GetStringOrNull(json, "name"),
            GetStringOrNull(json, "url"),
            GetStringOrNull(json, "iconCanonicalUrl") ?? GetStringOrNull(json, "iconUrl"),
            GetStringOrNull(json, "iconUrl")
        );

    private static EmbedImage ParseEmbedImage(JsonElement json) =>
        new(
            GetStringOrNull(json, "canonicalUrl") ?? GetStringOrNull(json, "url"),
            GetStringOrNull(json, "url"),
            GetInt32OrNull(json, "width"),
            GetInt32OrNull(json, "height")
        );

    private static EmbedVideo ParseEmbedVideo(JsonElement json) =>
        new(
            GetStringOrNull(json, "canonicalUrl") ?? GetStringOrNull(json, "url"),
            GetStringOrNull(json, "url"),
            GetInt32OrNull(json, "width"),
            GetInt32OrNull(json, "height")
        );

    private static EmbedFooter ParseEmbedFooter(JsonElement json) =>
        new(
            GetString(json, "text"),
            GetStringOrNull(json, "iconCanonicalUrl") ?? GetStringOrNull(json, "iconUrl"),
            GetStringOrNull(json, "iconUrl")
        );

    private static EmbedField ParseEmbedField(JsonElement json) =>
        new(
            GetStringOrNull(json, "nameRaw") ?? GetString(json, "name"),
            GetStringOrNull(json, "valueRaw") ?? GetString(json, "value"),
            GetBoolean(json, "isInline")
        );

    private static Sticker ParseSticker(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "name"),
            ParseEnum(GetString(json, "format"), StickerFormat.Png),
            GetString(json, "sourceUrl")
        );

    private static Reaction ParseReaction(JsonElement json) =>
        new(ParseEmoji(json.GetProperty("emoji")), GetInt32(json, "count"));

    private static Emoji ParseEmoji(JsonElement json) =>
        new Emoji(
            GetStringOrNull(json, "id") is { } id && !string.IsNullOrWhiteSpace(id)
                ? ParseSnowflake(id)
                : null,
            GetString(json, "name"),
            GetBoolean(json, "isAnimated")
        )
        {
            ImageUrlOverride = GetStringOrNull(json, "imageUrl"),
        };

    private static MessageReference ParseMessageReference(JsonElement json) =>
        new(
            ParseEnum(GetString(json, "type"), MessageReferenceKind.Default),
            GetStringOrNull(json, "messageId") is { } messageId ? ParseSnowflake(messageId) : null,
            GetStringOrNull(json, "channelId") is { } channelId ? ParseSnowflake(channelId) : null,
            GetStringOrNull(json, "guildId") is { } guildId ? ParseSnowflake(guildId) : null
        );

    private static MessageSnapshot ParseMessageSnapshot(JsonElement json) =>
        new(
            ParseDate(GetString(json, "timestamp")),
            ParseDateOrNull(json, "timestampEdited"),
            GetStringOrNull(json, "contentRaw") ?? GetString(json, "content"),
            ParseArray(json, "attachments", ParseAttachment),
            ParseArray(json, "embeds", ParseEmbed),
            ParseArray(json, "stickers", ParseSticker)
        );

    private static Interaction ParseInteraction(JsonElement json) =>
        new(
            ParseSnowflake(json.GetProperty("id")),
            GetString(json, "name"),
            ParseUser(json.GetProperty("user"))
        );

    private static ConversionData ParseConversionData(JsonElement json) =>
        new(
            ParseArray(
                json,
                "members",
                member => new ConversionMember(
                    GetString(member, "id"),
                    GetString(member, "displayName"),
                    GetStringOrNull(member, "avatarUrl"),
                    GetStringOrNull(member, "colorHex"),
                    ParseArray(member, "roleIds", roleId => GetString(roleId))
                )
            ),
            ParseArray(
                json,
                "roles",
                role => new ConversionRole(
                    GetString(role, "id"),
                    GetString(role, "name"),
                    GetStringOrNull(role, "colorHex"),
                    GetInt32(role, "position")
                )
            ),
            ParseArray(
                json,
                "channels",
                channel => new ConversionChannel(
                    GetString(channel, "id"),
                    GetString(channel, "name"),
                    GetStringOrNull(channel, "type"),
                    GetBoolean(channel, "isVoice")
                )
            ),
            ParseArray(json, "emojis", ParseConversionEmoji)
        );

    private static ConversionEmoji ParseConversionEmoji(JsonElement json) =>
        new(
            GetStringOrNull(json, "id"),
            GetString(json, "name"),
            GetBoolean(json, "isAnimated"),
            GetString(json, "imageUrl")
        );

    private static IReadOnlyList<ConversionEmoji> ParseInlineEmojis(JsonElement json)
    {
        var emojis = new List<ConversionEmoji>();

        emojis.AddRange(ParseArray(json, "inlineEmojis", ParseConversionEmoji));

        if (json.TryGetProperty("referencedMessage", out var referencedMessage))
            emojis.AddRange(ParseInlineEmojis(referencedMessage));

        if (json.TryGetProperty("forwardedMessage", out var forwardedMessage))
            emojis.AddRange(ParseInlineEmojis(forwardedMessage));

        foreach (var embed in ParseArray(json, "embeds", embed => embed))
            emojis.AddRange(ParseArray(embed, "inlineEmojis", ParseConversionEmoji));

        return emojis.Where(emoji => !string.IsNullOrWhiteSpace(emoji.ImageUrl)).ToArray();
    }

    private static ConversionData? MergeConversionData(
        ConversionData? conversionData,
        IReadOnlyList<ConversionEmoji> inlineEmojis
    )
    {
        if (inlineEmojis.Count <= 0)
            return conversionData;

        if (conversionData is null)
            return new ConversionData([], [], [], inlineEmojis);

        return conversionData with
        {
            Emojis = conversionData
                .Emojis.Concat(inlineEmojis)
                .DistinctBy(emoji => (emoji.Id, emoji.Name, emoji.IsAnimated))
                .ToArray(),
        };
    }

    private static IReadOnlyList<Message> RelinkReferencedMessages(IReadOnlyList<Message> messages)
    {
        var messagesById = messages
            .GroupBy(message => message.Id)
            .ToDictionary(g => g.Key, g => g.First());

        return messages
            .Select(message =>
                message is { ReferencedMessage: null, Reference.MessageId: { } referencedMessageId }
                && referencedMessageId != message.Id
                && messagesById.TryGetValue(referencedMessageId, out var referencedMessage)
                    ? message with
                    {
                        ReferencedMessage = referencedMessage,
                    }
                    : message
            )
            .ToArray();
    }

    private static (Snowflake? After, Snowflake? Before) ParseDateRange(JsonElement json)
    {
        Snowflake? after = null;
        Snowflake? before = null;

        if (GetStringOrNull(json, "after") is { } afterText)
            after = Snowflake.FromDate(ParseDate(afterText));

        if (GetStringOrNull(json, "before") is { } beforeText)
            before = Snowflake.FromDate(ParseDate(beforeText));

        return (after, before);
    }

    private static IReadOnlyList<T> ParseArray<T>(
        JsonElement json,
        string propertyName,
        Func<JsonElement, T> parse
    ) =>
        json.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(parse).ToArray()
            : [];

    private static Snowflake ParseSnowflake(JsonElement json) => ParseSnowflake(GetString(json));

    private static Snowflake ParseSnowflake(string text) =>
        new(ulong.Parse(text, CultureInfo.InvariantCulture));

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback)
        where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<TEnum>(text, true, out var value)
            ? value
            : fallback;

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseDateOrNull(JsonElement json, string propertyName) =>
        GetStringOrNull(json, propertyName) is { } text ? ParseDate(text) : null;

    private static Color? ParseColor(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : ColorTranslator.FromHtml(text);

    private static string GetString(JsonElement json, string propertyName) =>
        GetStringOrNull(json, propertyName) ?? "";

    private static string GetString(JsonElement json) =>
        json.ValueKind == JsonValueKind.String ? json.GetString() ?? "" : json.GetRawText();

    private static string? GetStringOrNull(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind != JsonValueKind.Null
            ? GetString(property)
            : null;

    private static bool GetBoolean(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static int GetInt32(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property) ? property.GetInt32() : 0;

    private static int? GetInt32OrNull(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property)
        && property.ValueKind != JsonValueKind.Null
            ? property.GetInt32()
            : null;

    private static long GetInt64(JsonElement json, string propertyName) =>
        json.TryGetProperty(propertyName, out var property) ? property.GetInt64() : 0;
}
