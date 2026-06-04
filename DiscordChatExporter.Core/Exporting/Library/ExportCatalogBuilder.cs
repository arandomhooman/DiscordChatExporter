using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Manifest;

namespace DiscordChatExporter.Core.Exporting.Library;

// Builds the export catalog by reading manifest.json from a set of directories and flattening
// their entries, de-duped by file path. Also scans a root folder recursively for manifests.
public static class ExportCatalogBuilder
{
    public static async ValueTask<IReadOnlyList<ManifestEntry>> BuildFromDirectoriesAsync(
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default
    )
    {
        var byFile = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(dir, ExportManifest.FileName);
            var manifest = await ManifestReader.TryReadAsync(manifestPath, cancellationToken);
            if (manifest is null)
                continue;

            foreach (var entry in manifest.Entries)
                byFile[entry.File] = entry;
        }

        return byFile.Values.OrderByDescending(e => e.ExportedAt).ToArray();
    }

    // Returns the directories under rootDir (inclusive) that contain a manifest.json.
    public static ValueTask<IReadOnlyList<string>> ScanForExportDirsAsync(
        string rootDir,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(rootDir))
            return new ValueTask<IReadOnlyList<string>>([]);

        try
        {
            var dirs = Directory
                .EnumerateFiles(rootDir, ExportManifest.FileName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrEmpty(d))
                .Select(d => d!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ValueTask<IReadOnlyList<string>>(dirs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValueTask<IReadOnlyList<string>>([]);
        }
    }
}
