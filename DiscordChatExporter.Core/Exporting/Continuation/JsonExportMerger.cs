using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

                default:
                    writer.WritePropertyName(name!);
                    CopyValue(ref reader, writer);
                    break;
            }
        }

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
