using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordChatExporter.Core.Exporting.Continuation;

// Splices freshly-rendered message groups into an existing HTML export (one growing file).
// Old messages can't be re-rendered (no Message objects survive), so we lift the already-minified
// new groups out of the fresh export and insert them just before the chatlog-closing </div>.
// Overlap from the cutoff window is deduped at message-CONTAINER granularity (a single group can
// hold several messages, only some of which are new). The validate gate + temp file + atomic
// File.Replace + .bak mean a botched merge aborts and keeps the original intact.
public static partial class HtmlExportMerger
{
    [GeneratedRegex("<div class=\"?postamble\"?>")]
    private static partial Regex PostambleOpenRegex();

    [GeneratedRegex("<div class=\"chatlog\">")]
    private static partial Regex ChatlogOpenRegex();

    [GeneratedRegex("(Exported )(.+?)( message\\(s\\))")]
    private static partial Regex CountRegex();

    // Matches the opening of a message container's class attribute, tolerating both the unquoted
    // single-token normal case (class=chatlog__message-container) and the QUOTED multi-token pinned
    // case (class="chatlog__message-container chatlog__message-container--pinned" — multi-token
    // values keep their quotes through the minifier). The literal "class=" prefix means it never
    // false-matches id="chatlog__message-container-N" or href="#chatlog__message-container-N", and
    // the "--pinned" token (not preceded by class=) yields no second match within a container.
    [GeneratedRegex("class=\"?chatlog__message-container")]
    private static partial Regex ContainerMarkerRegex();

    public static async ValueTask<long> MergeAsync(
        string existingFilePath,
        string newMessagesFilePath,
        // unused here (HTML dedupes by id); kept for ContinuationFormat dispatcher signature parity
        ContinuationCutoff cutoff,
        CancellationToken cancellationToken = default
    )
    {
        var oldHtml = await File.ReadAllTextAsync(existingFilePath, cancellationToken);
        var newHtml = await File.ReadAllTextAsync(newMessagesFilePath, cancellationToken);

        var oldIds = HtmlExportInspector
            .MessageIdRegex()
            .Matches(oldHtml)
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        // A malformed fresh export (no chatlog container at all) is a real failure — abort so we
        // never replace the original with a no-op against garbage input. A WELL-FORMED fresh export
        // whose every message is overlap (all deduped away) is a legitimate no-op, handled below.
        if (!ChatlogOpenRegex().IsMatch(newHtml))
            throw new InvalidExportException(
                "The new-messages HTML is not a recognizable export (no chatlog container)."
            );

        var newSlice = ExtractGroups(newHtml);
        newSlice = DedupeContainers(newSlice, oldIds);

        var spliceAt = FindChatlogCloseBeforePostamble(oldHtml);
        var merged = oldHtml[..spliceAt] + newSlice + oldHtml[spliceAt..];

        var totalCount = HtmlExportInspector.MessageIdRegex().Matches(merged).Count;
        merged = RewriteCount(merged, totalCount);

        Validate(merged);

        var tempPath = existingFilePath + ".merging.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, merged, cancellationToken);
            File.Replace(tempPath, existingFilePath, existingFilePath + ".bak");
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
                // best-effort
            }
            throw;
        }
        return totalCount;
    }

    // The chatlog closes with a </div> that the postamble's wmm:ignore block preserves verbatim,
    // immediately before "<div class=postamble>". Anchor on the LAST postamble (there is only one
    // in a valid export) and back up to the nearest preceding </div>.
    private static int FindChatlogCloseBeforePostamble(string html)
    {
        var post = PostambleOpenRegex().Matches(html).LastOrDefault();
        if (post is null)
            throw new InvalidExportException("Not a recognizable HTML export (no postamble).");
        var close = html.LastIndexOf("</div>", post.Index, StringComparison.Ordinal);
        if (close < 0)
            throw new InvalidExportException(
                "Could not locate the chatlog boundary in the HTML export."
            );
        return close;
    }

    // Everything between "<div class=\"chatlog\">" and the chatlog-closing </div>: i.e. the
    // message groups, byte-for-byte as the minifier emitted them.
    private static string ExtractGroups(string html)
    {
        var openMatch = ChatlogOpenRegex().Match(html);
        if (!openMatch.Success)
            return "";
        var start = openMatch.Index + openMatch.Length;
        var end = FindChatlogCloseBeforePostamble(html);
        return end > start ? html[start..end] : "";
    }

    // Dedup at message-CONTAINER granularity. A fresh group from the cutoff overlap window can
    // contain both already-exported messages (drop) and brand-new ones (keep), so dropping whole
    // groups would lose new siblings. We excise each container div whose data-message-id is already
    // present, then drop a group entirely only if nothing is left in it.
    private static string DedupeContainers(string slice, HashSet<string> existingIds)
    {
        if (string.IsNullOrEmpty(slice))
            return slice;

        var kept = new List<string>();
        foreach (var group in SplitMessageGroups(slice))
        {
            var deduped = DedupeContainersInGroup(group, existingIds, out var hasContainers);
            // Keep the group only if it still has at least one (new) container. A group that lost
            // all of its containers is pure overlap and contributes nothing.
            if (hasContainers)
                kept.Add(deduped);
        }
        return string.Concat(kept);
    }

    // A group is "<div ...class=chatlog__message-group...>" + one or more message containers +
    // the group-closing "</div>". We keep the group prefix and the closing </div> intact, and
    // filter the containers in between. Splitting is done by the quote-agnostic
    // "class=chatlog__message-container" marker (single-token class minifies unquoted, same as
    // postamble), backing up to the owning "<div".
    private static string DedupeContainersInGroup(
        string group,
        HashSet<string> existingIds,
        out bool hasContainers
    )
    {
        var firstContainer = FindFirstContainerStart(group);
        if (firstContainer < 0)
        {
            // No recognizable container in this group — keep it verbatim and treat as non-empty
            // so we never silently discard content we don't understand.
            hasContainers = true;
            return group;
        }

        var prefix = group[..firstContainer];

        // The group-closing </div> is the LAST </div> in the group; everything before it is the
        // container region. Reserving it separately means container slicing can never swallow the
        // structural close, even when the final container is the one being dropped.
        var groupClose = group.LastIndexOf("</div>", StringComparison.Ordinal);
        if (groupClose < firstContainer)
        {
            hasContainers = true;
            return group;
        }
        var suffix = group[groupClose..];
        var containerRegion = group[firstContainer..groupClose];

        var kept = new List<string>();
        foreach (var container in SplitByContainerMarker(containerRegion))
        {
            var ids = HtmlExportInspector
                .MessageIdRegex()
                .Matches(container)
                .Select(m => m.Groups[1].Value)
                .ToArray();
            // Drop a container only if every id it carries is already present. (Each container has
            // exactly one data-message-id in practice; the All() is just defensive.)
            if (ids.Length > 0 && ids.All(existingIds.Contains))
                continue;
            kept.Add(container);
        }

        hasContainers = kept.Count > 0;
        return hasContainers ? prefix + string.Concat(kept) + suffix : "";
    }

    private static int FindFirstContainerStart(string group)
    {
        var marker = ContainerMarkerRegex().Match(group);
        if (!marker.Success)
            return -1;
        var divStart = group.LastIndexOf("<div", marker.Index, StringComparison.Ordinal);
        return divStart;
    }

    private static IEnumerable<string> SplitByContainerMarker(string containerRegion)
    {
        var starts = ContainerMarkerDivStarts(containerRegion);
        if (starts.Count == 0)
        {
            yield return containerRegion;
            yield break;
        }
        for (var k = 0; k < starts.Count; k++)
        {
            var start = starts[k];
            var end = k + 1 < starts.Count ? starts[k + 1] : containerRegion.Length;
            yield return containerRegion[start..end];
        }
    }

    // Quote-tolerant container-start locator (see ContainerMarkerRegex). For each class-attr match,
    // back up to the owning "<div".
    private static List<int> ContainerMarkerDivStarts(string text)
    {
        var starts = new List<int>();
        foreach (Match m in ContainerMarkerRegex().Matches(text))
        {
            var divStart = text.LastIndexOf("<div", m.Index, StringComparison.Ordinal);
            if (divStart >= 0)
                starts.Add(divStart);
        }
        return starts;
    }

    private static IEnumerable<string> SplitMessageGroups(string slice)
    {
        const string marker = "class=chatlog__message-group";
        var starts = MarkerDivStarts(slice, marker);
        if (starts.Count == 0)
        {
            yield return slice;
            yield break;
        }
        for (var k = 0; k < starts.Count; k++)
        {
            var start = starts[k];
            var end = k + 1 < starts.Count ? starts[k + 1] : slice.Length;
            yield return slice[start..end];
        }
    }

    // For every literal occurrence of `marker`, return the index of the "<div" that owns it. Used
    // only for the group marker (class=chatlog__message-group), which the template always emits
    // clean/unquoted — containers use the quote-tolerant ContainerMarkerDivStarts instead. The group
    // and container class names diverge after "message-", so the two markers never cross-match.
    private static List<int> MarkerDivStarts(string text, string marker)
    {
        var starts = new List<int>();
        for (
            var m = text.IndexOf(marker, StringComparison.Ordinal);
            m >= 0;
            m = text.IndexOf(marker, m + 1, StringComparison.Ordinal)
        )
        {
            var divStart = text.LastIndexOf("<div", m, StringComparison.Ordinal);
            if (divStart >= 0)
                starts.Add(divStart);
        }
        return starts;
    }

    // Locate the count match ("Exported N message(s)") within the POSTAMBLE only. Message content
    // is HTML-escaped but the digits/words of that phrase survive verbatim, so a user message can
    // contain the exact phrase earlier in the file — scanning the whole document and taking the
    // first match would rewrite the user's text and leave the real count stale (silent corruption).
    // Anchoring the scan at the postamble open (the postamble is the last block and the phrase's
    // only legitimate home) makes the first match the real one. Returns null if not found.
    private static Match? FindCountInPostamble(string html)
    {
        var post = PostambleOpenRegex().Match(html);
        if (!post.Success)
            return null;
        var match = CountRegex().Match(html, post.Index);
        return match.Success ? match : null;
    }

    // Rewrite the recomputed total into the postamble count, in the same "n0" form the renderer
    // used. The count is a validated invariant (see Validate), not cosmetic: a real export always
    // carries it, so its absence means the input isn't a recognizable export and we abort.
    private static string RewriteCount(string html, long total)
    {
        var match =
            FindCountInPostamble(html)
            ?? throw new InvalidExportException(
                "Could not locate the message count in the HTML export's postamble."
            );
        var replacement = match.Groups[1].Value + total.ToString("n0") + match.Groups[3].Value;
        return html[..match.Index] + replacement + html[(match.Index + match.Length)..];
    }

    // Key safety net. If any of these fail the merge is aborted (caller keeps the .bak/original).
    // These structural invariants — exactly one container, exactly one postamble, no duplicate ids,
    // ascending ids — are what would break if the splice/dedupe logic mangled the file, so they are
    // the real guard. (No growth assertion: malformed fresh input is rejected upstream, and an
    // all-overlap no-op is a legitimate outcome, consistent with the Json/Csv sibling mergers.)
    //
    // Ascending is asserted because the export — and this append-at-end splice — assume chronological
    // order (the only mode the continuation feature produces). Reverse-order exports are out of scope.
    private static void Validate(string merged)
    {
        if (ChatlogOpenRegex().Matches(merged).Count != 1)
            throw new InvalidExportException(
                "HTML merge produced an invalid file (chatlog container count)."
            );
        if (PostambleOpenRegex().Matches(merged).Count != 1)
            throw new InvalidExportException(
                "HTML merge produced an invalid file (postamble count)."
            );
        var ids = HtmlExportInspector
            .MessageIdRegex()
            .Matches(merged)
            .Select(m => ulong.Parse(m.Groups[1].Value))
            .ToArray();
        if (ids.Distinct().Count() != ids.Length)
            throw new InvalidExportException("HTML merge produced duplicate message ids.");
        for (var i = 1; i < ids.Length; i++)
            if (ids[i] < ids[i - 1])
                throw new InvalidExportException("HTML merge produced out-of-order message ids.");

        // Count-consistency: the postamble's DISPLAYED count must equal the actual id count. Rather
        // than parse the displayed digits (culture/grouping-separator hazard), we round-trip the
        // expected value through the same "n0" formatting RewriteCount used and string-compare — so
        // this is exact by construction. This catches a count that was written to the wrong place
        // (e.g. into message content) or never updated. A no-op merge still passes (count unchanged).
        var countMatch =
            FindCountInPostamble(merged)
            ?? throw new InvalidExportException(
                "HTML merge produced a file with no message count in the postamble."
            );
        var displayed = countMatch.Groups[2].Value;
        var expected = ((long)ids.Length).ToString("n0");
        if (displayed != expected)
            throw new InvalidExportException(
                $"HTML merge produced an inconsistent message count (postamble shows '{displayed}', expected '{expected}')."
            );
    }
}
