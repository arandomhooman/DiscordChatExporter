using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;

namespace DiscordChatExporter.Core.Exporting.Library;

public static class ContinueExportDiscovery
{
    public static async ValueTask<ContinueDiscoveryResult> ResolveAsync(
        IReadOnlyList<string> directories,
        IReadOnlyCollection<Snowflake> selectedChannelIds,
        CancellationToken cancellationToken = default
    )
    {
        var entries = await ExportCatalogBuilder.BuildFromDirectoriesAsync(
            directories,
            cancellationToken
        );
        var resolved = new List<ResolvedCatalogEntry>();
        var unresolved = new List<UnresolvedCatalogChannel>();

        foreach (var channelId in selectedChannelIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = entries.Where(e => e.ChannelId == channelId.ToString()).ToArray();

            if (candidates.Length == 0)
            {
                unresolved.Add(
                    new UnresolvedCatalogChannel(channelId, ContinueSkipReason.NoPriorExport)
                );
                continue;
            }

            ContinueSkipReason? firstFailureReason = null;

            foreach (var candidate in candidates)
            {
                var reason = GetUnusableReason(candidate);
                if (reason is not null)
                {
                    firstFailureReason ??= reason;
                    continue;
                }

                if (!Enum.TryParse<ExportFormat>(candidate.Format, out var format))
                {
                    firstFailureReason ??= ContinueSkipReason.UnknownFormat;
                    continue;
                }

                resolved.Add(new ResolvedCatalogEntry(channelId, candidate.File, format));
                firstFailureReason = null;
                break;
            }

            if (firstFailureReason is { } unresolvedReason)
                unresolved.Add(new UnresolvedCatalogChannel(channelId, unresolvedReason));
        }

        return new ContinueDiscoveryResult(resolved, unresolved);
    }

    private static ContinueSkipReason? GetUnusableReason(Manifest.ManifestEntry entry)
    {
        if (!File.Exists(entry.File))
            return ContinueSkipReason.FileMissing;

        if (entry.Partitioned)
            return ContinueSkipReason.Partitioned;

        if (!ContinuationFormat.IsSupportedExtension(entry.File))
            return ContinueSkipReason.UnsupportedFormat;

        return null;
    }
}

public sealed record ResolvedCatalogEntry(
    Snowflake ChannelId,
    string FilePath,
    ExportFormat Format
);

public sealed record UnresolvedCatalogChannel(Snowflake ChannelId, ContinueSkipReason Reason);

public sealed record ContinueDiscoveryResult(
    IReadOnlyList<ResolvedCatalogEntry> Resolved,
    IReadOnlyList<UnresolvedCatalogChannel> Unresolved
);

public enum ContinueSkipReason
{
    NoPriorExport,
    FileMissing,
    Partitioned,
    UnsupportedFormat,
    UnknownFormat,
    ReverseChronological,

    // The existing export file can't yield a resume point: it has no messages, or its cutoff
    // couldn't be read (empty/corrupt). Emitted during VM hydration, not by the Core resolver.
    CutoffUnreadable,
}
