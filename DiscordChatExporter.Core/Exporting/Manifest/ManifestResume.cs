using System;
using System.Collections.Generic;
using System.Linq;

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
}
