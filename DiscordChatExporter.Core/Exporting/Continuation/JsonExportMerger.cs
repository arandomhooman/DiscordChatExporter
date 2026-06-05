using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Conversion;

namespace DiscordChatExporter.Core.Exporting.Continuation;

public static class JsonExportMerger
{
    // Merges the 'messages' from newMessagesFilePath into existingFilePath, preserving the
    // existing preamble (guild/channel/dateRange), refreshing exportedAt, and recomputing
    // messageCount. Writes to a temp file then atomically replaces the original (with .bak).
    // Returns the merged total message count.
    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken = default
    )
    {
        var existingBytes = await File.ReadAllBytesAsync(existingFilePath, cancellationToken);
        var newBytes = await File.ReadAllBytesAsync(newMessagesFilePath, cancellationToken);

        var tempPath = existingFilePath + ".merging.tmp";
        long total;

        try
        {
            await using (var outStream = File.Create(tempPath))
            {
                await using var writer = new Utf8JsonWriter(
                    outStream,
                    new JsonWriterOptions
                    {
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        Indented = true,
                        SkipValidation = true,
                    }
                );

                total = Merge(existingBytes, newBytes, exportedAt, writer);
                await writer.FlushAsync(cancellationToken);
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
            { /* best-effort temp cleanup */
            }
            throw;
        }

        return total;
    }

    private static long Merge(
        byte[] existingBytes,
        byte[] newBytes,
        DateTimeOffset exportedAt,
        Utf8JsonWriter writer
    )
    {
        long total = 0;
        var hasMergedConversionData =
            HasConversionData(existingBytes) || HasConversionData(newBytes);
        var wroteConversionData = false;

        var reader = new Utf8JsonReader(existingBytes);
        reader.Read(); // StartObject (root)
        writer.WriteStartObject();

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read(); // advance to value

            switch (name)
            {
                case "exportedAt":
                    writer.WriteString("exportedAt", exportedAt);
                    // Discard the original timestamp token; we write a fresh exportedAt above.
                    break;

                case "messageCount":
                    break; // drop; recomputed below

                case "messages":
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        CopyValue(ref reader, writer);
                        total++;
                    }
                    total += AppendNewMessages(newBytes, writer);
                    writer.WriteEndArray();
                    break;

                case "conversionData":
                    if (hasMergedConversionData)
                    {
                        WriteMergedConversionData(existingBytes, newBytes, writer);
                        wroteConversionData = true;
                    }
                    reader.Skip();
                    break;

                default:
                    writer.WritePropertyName(name!);
                    CopyValue(ref reader, writer);
                    break;
            }
        }

        if (hasMergedConversionData && !wroteConversionData)
            WriteMergedConversionData(existingBytes, newBytes, writer);

        writer.WriteNumber("messageCount", total);
        writer.WriteEndObject();
        return total;
    }

    private static long AppendNewMessages(byte[] newBytes, Utf8JsonWriter writer)
    {
        long count = 0;
        var reader = new Utf8JsonReader(newBytes);
        reader.Read(); // StartObject (root)

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (name == "messages")
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    CopyValue(ref reader, writer);
                    count++;
                }
                break;
            }
            reader.Skip();
        }
        return count;
    }

    private static bool HasConversionData(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.TryGetProperty("conversionData", out var conversionData)
            && conversionData.ValueKind == JsonValueKind.Object;
    }

    private static void WriteMergedConversionData(
        byte[] existingBytes,
        byte[] newBytes,
        Utf8JsonWriter writer
    )
    {
        using var existingDocument = JsonDocument.Parse(existingBytes);
        using var newDocument = JsonDocument.Parse(newBytes);
        var existing = existingDocument.RootElement.TryGetProperty(
            "conversionData",
            out var existingData
        )
            ? existingData
            : default;
        var fresh = newDocument.RootElement.TryGetProperty("conversionData", out var freshData)
            ? freshData
            : default;

        writer.WritePropertyName("conversionData");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", ConversionData.CurrentSchemaVersion);
        WriteMergedArray(writer, "members", existing, fresh, GetIdKey);
        WriteMergedArray(writer, "roles", existing, fresh, GetIdKey);
        WriteMergedArray(writer, "channels", existing, fresh, GetIdKey);
        WriteMergedArray(writer, "emojis", existing, fresh, GetEmojiKey);
        writer.WriteEndObject();
    }

    private static void WriteMergedArray(
        Utf8JsonWriter writer,
        string propertyName,
        JsonElement existing,
        JsonElement fresh,
        Func<JsonElement, string?> getKey
    )
    {
        var valuesByKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        AddValues(existing);
        AddValues(fresh);

        writer.WriteStartArray(propertyName);
        foreach (var value in valuesByKey.Values)
            value.WriteTo(writer);

        writer.WriteEndArray();

        void AddValues(JsonElement conversionData)
        {
            if (
                conversionData.ValueKind != JsonValueKind.Object
                || !conversionData.TryGetProperty(propertyName, out var values)
                || values.ValueKind != JsonValueKind.Array
            )
            {
                return;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (getKey(value) is { } key)
                    valuesByKey[key] = value;
            }
        }
    }

    private static string? GetIdKey(JsonElement value) =>
        value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    private static string? GetEmojiKey(JsonElement value)
    {
        var id =
            value.TryGetProperty("id", out var idProperty)
            && idProperty.ValueKind == JsonValueKind.String
                ? idProperty.GetString()
                : "";
        var name =
            value.TryGetProperty("name", out var nameProperty)
            && nameProperty.ValueKind == JsonValueKind.String
                ? nameProperty.GetString()
                : "";
        var isAnimated =
            value.TryGetProperty("isAnimated", out var isAnimatedProperty)
            && isAnimatedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? isAnimatedProperty.GetBoolean()
                : false;

        return string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name)
            ? null
            : $"{id}|{name}|{isAnimated}";
    }

    // Copies the complete JSON value the reader is currently positioned on (scalar or
    // container subtree), leaving the reader on that value's final token.
    private static void CopyValue(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            var depth = 0;
            do
            {
                CopyToken(ref reader, writer);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    depth++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                    depth--;

                if (depth == 0)
                    break;
                reader.Read();
            } while (true);
        }
        else
        {
            CopyToken(ref reader, writer);
        }
    }

    private static void CopyToken(ref Utf8JsonReader reader, Utf8JsonWriter writer)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                writer.WriteStartObject();
                break;
            case JsonTokenType.EndObject:
                writer.WriteEndObject();
                break;
            case JsonTokenType.StartArray:
                writer.WriteStartArray();
                break;
            case JsonTokenType.EndArray:
                writer.WriteEndArray();
                break;
            case JsonTokenType.PropertyName:
                writer.WritePropertyName(reader.GetString()!);
                break;
            case JsonTokenType.String:
                writer.WriteStringValue(reader.GetString());
                break;
            case JsonTokenType.Number:
                writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                break;
            case JsonTokenType.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonTokenType.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonTokenType.Null:
                writer.WriteNullValue();
                break;
        }
    }
}
