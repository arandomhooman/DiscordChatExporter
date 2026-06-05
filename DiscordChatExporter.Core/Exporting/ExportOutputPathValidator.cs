using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DiscordChatExporter.Core.Exporting;

public static class ExportOutputPathValidator
{
    public static IReadOnlyList<string> GetDuplicateOutputFilePaths(
        IEnumerable<ExportRequest> requests
    ) =>
        requests
            .Select(r => Path.GetFullPath(r.OutputFilePath))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
