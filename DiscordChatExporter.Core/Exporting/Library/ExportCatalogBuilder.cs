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

            // ManifestEntry.File is a BARE filename relative to the manifest's own directory
            // (see ManifestBuilder, which sets it via Path.GetFileName). Resolve it to an
            // ABSOLUTE path here so downstream consumers (File.Exists, SqliteExportReader,
            // and the display/join-back in LibraryViewModel) all see a real, openable path.
            // Path.Combine is a no-op when entry.File is already rooted, so this is safe and
            // also collapses cross-folder bare-name collisions to distinct absolute keys.
            foreach (var entry in manifest.Entries)
            {
                var absoluteFile = Path.Combine(dir, entry.File);
                byFile[absoluteFile] = entry with { File = absoluteFile };
            }
        }

        return byFile.Values.OrderByDescending(e => e.ExportedAt).ToArray();
    }

    // Returns the directories under rootDir (inclusive) that contain a manifest.json.
    public static async ValueTask<IReadOnlyList<string>> ScanForExportDirsAsync(
        string rootDir,
        CancellationToken cancellationToken = default
    )
    {
        if (!Directory.Exists(rootDir))
            return [];

        // The recursive walk is synchronous and can be slow on a large/deep tree, so run it off
        // the calling (UI) thread. IgnoreInaccessible skips protected subdirectories instead of
        // throwing partway through and discarding every manifest found so far — the SearchOption
        // overload defaults IgnoreInaccessible=false, which turned one locked subdir into an
        // empty result for the whole scan.
        return await Task.Run<IReadOnlyList<string>>(
            () =>
            {
                try
                {
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                    };

                    return Directory
                        .EnumerateFiles(rootDir, ExportManifest.FileName, options)
                        .Select(Path.GetDirectoryName)
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Select(d => d!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return [];
                }
            },
            cancellationToken
        );
    }
}
