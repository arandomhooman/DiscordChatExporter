using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestWriter
{
    // Writes/updates <dirPath>/manifest.json by merging the given entries into any existing
    // manifest, keyed by file name (a new entry for the same file replaces the old one).
    // Atomic: writes a temp file then swaps it in, keeping a .bak of the previous manifest.
    public static async ValueTask WriteAsync(
        string dirPath,
        IReadOnlyList<ManifestEntry> newEntries,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    )
    {
        var manifestPath = Path.Combine(dirPath, ExportManifest.FileName);

        var byFile = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        var existing = await ManifestReader.TryReadAsync(manifestPath, cancellationToken);
        if (existing is not null)
        {
            foreach (var entry in existing.Entries)
                byFile[entry.File] = entry;
        }

        foreach (var entry in newEntries)
            byFile[entry.File] = entry;

        var merged = new ExportManifest(
            ExportManifest.CurrentSchemaVersion,
            now,
            byFile.Values.OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase).ToArray()
        );

        Directory.CreateDirectory(dirPath);

        var tempPath = manifestPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                merged,
                ManifestJson.Options,
                cancellationToken
            );
        }

        if (File.Exists(manifestPath))
            File.Replace(tempPath, manifestPath, manifestPath + ".bak");
        else
            File.Move(tempPath, manifestPath);
    }
}
