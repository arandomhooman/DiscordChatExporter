using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiscordChatExporter.Core.Exporting;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestResume
{
    // Given a directory's manifest (or null) and a set of candidate output file names, returns the
    // subset of candidates already present in the manifest — i.e. already exported, skippable on resume.
    // Match is by file name, case-insensitive, consistent with the manifest's one-entry-per-file grain.
    public static IReadOnlySet<string> AlreadyExported(
        ExportManifest? manifest,
        IEnumerable<string> candidateFileNames
    )
    {
        var have = manifest is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : manifest.Entries.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidateFileNames.Where(have.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAlreadyExported(
        ExportManifest? manifest,
        string dirPath,
        ExportRequest request
    )
    {
        if (manifest is null)
            return false;

        var fileName = Path.GetFileName(request.OutputFilePath);
        var entry = manifest.Entries.FirstOrDefault(e =>
            string.Equals(e.File, fileName, StringComparison.OrdinalIgnoreCase)
            && e.GuildId == request.Guild.Id.ToString()
            && e.ChannelId == request.Channel.Id.ToString()
            && e.Format == request.Format.ToString()
        );

        if (entry is null)
            return false;

        var filePath = Path.Combine(dirPath, entry.File);
        if (!File.Exists(filePath))
            return false;

        try
        {
            return new FileInfo(filePath).Length == entry.FileSizeBytes
                && string.Equals(
                    ManifestBuilder.ComputeSha256(filePath),
                    entry.Sha256,
                    StringComparison.OrdinalIgnoreCase
                );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
