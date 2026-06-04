using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestReader
{
    // Reads and parses a manifest.json. Returns null (never throws) when the file is missing,
    // unreadable, or not valid manifest JSON, so callers can treat "no usable manifest" uniformly.
    public static async ValueTask<ExportManifest?> TryReadAsync(
        string filePath,
        CancellationToken cancellationToken = default
    )
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(filePath);
            return await JsonSerializer.DeserializeAsync(
                stream,
                ManifestJsonContext.Default.ExportManifest,
                cancellationToken
            );
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or IOException
                        or NotSupportedException
                        or UnauthorizedAccessException
            )
        {
            return null;
        }
    }
}
