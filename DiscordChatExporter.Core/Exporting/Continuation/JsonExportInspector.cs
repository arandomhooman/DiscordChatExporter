using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class JsonExportInspector
{
    public static async ValueTask<JsonExportInfo> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidJsonExportException($"Could not read '{filePath}'.", ex);
        }

        try
        {
            return Inspect(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidJsonExportException(
                "The selected file is not a valid JSON export.",
                ex
            );
        }
    }

    private static JsonExportInfo Inspect(byte[] bytes)
    {
        Snowflake? guildId = null;
        Snowflake? channelId = null;
        Snowflake? before = null;
        Snowflake? firstId = null;
        Snowflake? lastId = null;
        DateTimeOffset? firstTs = null;
        DateTimeOffset? lastTs = null;
        long count = 0;

        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("The selected file is not a JSON export.");

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read(); // advance to value

            switch (name)
            {
                case "guild":
                    guildId = ReadIdOf(ref reader);
                    break;
                case "channel":
                    channelId = ReadIdOf(ref reader);
                    break;
                case "dateRange":
                    before = ReadBeforeOf(ref reader);
                    break;
                case "messages":
                    if (reader.TokenType != JsonTokenType.StartArray)
                        throw new InvalidJsonExportException("Malformed 'messages' array.");
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        var (id, ts) = ReadMessageHeader(ref reader);
                        firstId ??= id;
                        firstTs ??= ts;
                        lastId = id;
                        lastTs = ts;
                        count++;
                    }
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (guildId is null || channelId is null)
            throw new InvalidJsonExportException(
                "The selected file is not a DiscordChatExporter JSON export."
            );

        if (count == 0 || lastId is null)
            throw new InvalidJsonExportException(
                "The selected export contains no messages to continue from."
            );

        var isChronological = firstTs is null || lastTs is null || firstTs <= lastTs;

        return new JsonExportInfo(
            guildId.Value,
            channelId.Value,
            before,
            lastId.Value,
            count,
            isChronological
        );
    }

    // Reader is positioned on the StartObject of an object that has a string "id" property.
    // Returns that id and leaves the reader on the object's EndObject.
    private static Snowflake ReadIdOf(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("Expected an object.");

        Snowflake? id = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "id" && reader.TokenType == JsonTokenType.String)
                id = Snowflake.Parse(reader.GetString()!);
            else
                reader.Skip();
        }
        return id ?? throw new InvalidJsonExportException("Missing 'id'.");
    }

    private static Snowflake? ReadBeforeOf(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        Snowflake? before = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "before" && reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (
                    !string.IsNullOrWhiteSpace(raw)
                    && DateTimeOffset.TryParse(
                        raw,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var dto
                    )
                )
                {
                    before = Snowflake.FromDate(dto);
                }
            }
            else
            {
                reader.Skip();
            }
        }
        return before;
    }

    // Reader positioned on the StartObject of a message. Returns id + timestamp,
    // leaves the reader on the message's EndObject.
    private static (Snowflake Id, DateTimeOffset? Timestamp) ReadMessageHeader(
        ref Utf8JsonReader reader
    )
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidJsonExportException("Malformed message entry.");

        Snowflake? id = null;
        DateTimeOffset? ts = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var prop = reader.GetString();
            reader.Read();
            if (prop == "id" && reader.TokenType == JsonTokenType.String)
                id = Snowflake.Parse(reader.GetString()!);
            else if (prop == "timestamp" && reader.TokenType == JsonTokenType.String)
                ts = reader.TryGetDateTimeOffset(out var dto) ? dto : null;
            else
                reader.Skip();
        }
        return (id ?? throw new InvalidJsonExportException("Message missing 'id'."), ts);
    }
}
