# Continue Export: HTML + CSV support, and Export ETA — Design

**Date:** 2026-06-03
**Status:** Approved (pending spec review)
**Target:** DiscordChatExporter GUI (Avalonia, .NET 10) + Core
**Builds on:** `2026-06-03-incremental-json-export-design.md` (the JSON-only "Continue export" feature)

## Summary

Two independent enhancements to the existing GUI "Continue export" feature and the export UI:

1. **More formats for Continue export** — extend continuation from JSON-only to also support
   **HTML** and **CSV**. (Plain-text/TXT is explicitly *not* supported — see Non-goals.)
2. **Estimated time to completion (ETA)** — show a live "time remaining" estimate next to the
   dashboard progress bar during any export *and* during continue.

## Part A — Multi-format Continue export

### Scope
- Add HTML and CSV to the formats that can be continued. JSON already works.
- Keep the same flow: pick an existing export file → determine the cutoff → resolve the live
  channel → export only newer messages (same format) to a temp file → merge into the original.
- Dispatch on the file's extension to a per-format handler.

### Non-goals (Part A)
- **Plain-text (.txt) continuation.** TXT message headers carry only a minute-precision,
  locale-formatted timestamp (`FormatDate` default `"g"`) and no message ID, so a reliable
  cutoff is not recoverable. Picking a `.txt` file is **refused** with a clear message.
- **Partitioned exports** (`… [part N].ext`) — detected by name/sibling and refused (as in JSON v1).
- **Reverse-ordered exports** — detected (first vs last cutoff) and refused.
- **Media re-download on continue** — unchanged from v1 (off; new messages keep remote URLs).
- **Renamed/custom-named files** that lack the `[<channelId>]` token AND lack an in-file channel
  id (CSV/TXT) — refused with guidance to keep the default filename or re-export.

### Cutoff & channel-identity reality (why this differs per format)

| Format | Message ID in file? | Channel/Guild id in file? | Cutoff precision |
|---|---|---|---|
| JSON | yes (`id`) | yes (`channel.id`, `guild.id`) | exact |
| HTML | yes (`data-message-id="…"`) | no (names only) | exact |
| CSV  | no (cols: `AuthorID,Author,Date,Content,Attachments,Reactions`) | no | ISO `"o"` timestamp (~ms) |

**Channel identity:** CSV (and HTML) do not embed a channel id. The universal source is the
**filename**, which by default embeds `[<channelId>]` (e.g. `Guild - general [12345].csv`,
produced by `ExportRequest.GetDefaultOutputFileName`). Resolution order:
1. Parse `[<digits>]` from the filename (all formats).
2. Fallback to in-file `channel.id` for JSON/HTML.
3. Neither found → refuse with guidance.

Guild id is **not** needed from the file: `GetChannelAsync(channelId)` returns the channel +
`GuildId`; then `GetGuildAsync(channel.GuildId)` (or `Guild.DirectMessages` if `channel.IsDirect`).

**Cutoff value:**
- JSON / HTML → exact last message-id snowflake → exclusive `after` cursor → **no dupes/gaps**,
  immune to deleted messages (same guarantee as JSON v1).
- CSV → last data row's `Date` (ISO `"o"`, culture-invariant, ~ms) → `Snowflake.FromDate`. Because
  `FromDate` truncates to the millisecond, the boundary message can be re-fetched; the CSV merge
  therefore **skips appended rows whose timestamp ≤ the cutoff timestamp** to remove the ≤1
  boundary duplicate. (Rare same-millisecond ambiguity is accepted — CSV has no id to disambiguate.)

### Architecture

A light dispatcher selects a per-format handler by extension. Existing JSON code is reused
unchanged; two new format handlers are added, plus shared helpers.

```
Core/Exporting/Continuation/
  JsonExportInspector.cs      (existing) — exact id cutoff from JSON
  JsonExportMerger.cs         (existing) — token-pump JSON merge
  CsvExportInspector.cs       (new)      — last-row timestamp cutoff from CSV
  CsvExportMerger.cs          (new)      — append rows (skip header + rows ≤ cutoff)
  HtmlExportInspector.cs      (new)      — last data-message-id cutoff from HTML
  HtmlExportMerger.cs         (new)      — splice message groups + bump count
  ContinuationCutoff.cs       (new)      — shared record returned by all inspectors
  FileNameChannelId.cs        (new)      — parse [<id>] from a filename
  ContinuationFormat.cs       (new)      — extension → handler dispatch; resolve cutoff + merge
```

`ContinuationCutoff` (shared):
```csharp
public sealed record ContinuationCutoff(
    Snowflake ChannelId,
    Snowflake Cutoff,        // exact message id (JSON/HTML) or FromDate(lastTimestamp) (CSV)
    Snowflake? Before,       // carried forward if the original had a 'before' bound
    bool IsChronological,
    long ExistingCount,
    bool CutoffIsExact       // true for JSON/HTML, false for CSV (drives boundary skipping)
);
```

`ContinuationFormat` exposes:
```csharp
public static bool IsSupportedExtension(string filePath);   // .json/.html/.htm/.csv
public static ValueTask<ContinuationCutoff> ReadCutoffAsync(string filePath, CancellationToken);
public static ValueTask<long> MergeAsync(string existingPath, string newPath,
    ContinuationCutoff cutoff, DateTimeOffset exportedAt, CancellationToken); // returns total
```
JSON dispatch wraps the existing `JsonExportInspector`/`JsonExportMerger` (mapping `JsonExportInfo`
→ `ContinuationCutoff` with `CutoffIsExact = true`, `ChannelId` from in-file or filename).

### Per-format details

**CSV** (`CsvExportInspector` / `CsvExportMerger`)
- Inspect: read the file; header is line 1. Parse the **first** and **last** data rows' `Date`
  column (ISO `"o"` via `DateTimeOffset.Parse(..., RoundtripKind, InvariantCulture)`). Cutoff =
  `FromDate(lastDate)`; `IsChronological` = firstDate ≤ lastDate; `ExistingCount` = data-row count;
  `ChannelId` from filename. CSV rows can contain quoted, embedded newlines — parse with a minimal
  RFC-4180-aware row splitter (quotes doubled, commas/newlines inside quotes), not naive line split.
- Merge: append the temp export's data rows (everything after its header line), **skipping rows
  whose `Date` ≤ cutoff timestamp**. No header rewrite needed (CSV has no footer/count). Write via
  temp + atomic `File.Replace` + `.bak` (same safety as JSON).

**HTML** (`HtmlExportInspector` / `HtmlExportMerger`)
- Output is minified (WebMarkupMin) — attribute quotes may be stripped. All parsing uses
  quote-tolerant regex (`data-message-id="?(\d+)"?`).
- Inspect: find all `data-message-id` values; first/last give order and the exact cutoff id;
  `ExistingCount` = count of `chatlog__message-container-` occurrences (informational);
  `ChannelId` from filename. `CutoffIsExact = true`.
- Merge (marker-based splice):
  1. In the existing file, locate the chatlog-closing `</div>` immediately preceding
     `<div class="postamble">` (the postamble opens by closing the chatlog container).
  2. From the temp export, extract the message-group HTML: the slice **between** the end of its
     preamble (the chatlog container open) and the start of its postamble (the same
     chatlog-closing `</div>` before `<div class="postamble">`).
  3. Insert that slice at the splice point in the existing file.
  4. Update the existing postamble's `Exported N message(s)` count to `N + M` (regex on the
     `postamble__entry` text; the number is `n0`-formatted with the export culture — match digits +
     grouping separators).
  5. Write via temp + atomic `File.Replace` + `.bak`.
- **Accepted cosmetic seam:** if the last existing message and the first new message are the same
  author within 7 minutes, they render as two adjacent groups (a repeated author header) instead of
  one merged group. Visual only; content is complete and correct.

### GUI changes (Part A)
`DashboardViewModel.ContinueExportAsync` is generalized:
- File picker filter widens to `*.json;*.html;*.htm;*.csv` (label "Supported exports").
- After picking: if `!ContinuationFormat.IsSupportedExtension(path)` (e.g. `.txt`) → snackbar
  "Continuing {ext} exports isn't supported" and return.
- Replace the JSON-specific inspect/merge calls with `ContinuationFormat.ReadCutoffAsync` /
  `MergeAsync`. The temp export's `ExportFormat` is chosen to match the existing file's extension
  (`.json`→Json, `.html`/`.htm`→HtmlDark, `.csv`→Csv). HTML theme for the temp does not affect the
  merge (only message-group markup is spliced, not theme CSS).
- New localization strings: `ContinueExportFormatUnsupportedMessage`,
  `ContinueExportChannelUnknownMessage` (filename/in-file id not found).
- The existing partition/reverse/up-to-date handling stays.

### Testing (Part A)
Core unit tests (no network) with fixtures, per handler:
- `FileNameChannelId`: parses `[<id>]` from default names; returns null for names without it.
- `CsvExportInspector`: cutoff/order/count from a CSV; quoted-field + embedded-comma/newline rows.
- `CsvExportMerger`: append rows, header skipped, boundary rows (`≤ cutoff`) skipped, re-parse valid,
  `.bak` created.
- `HtmlExportInspector`: extracts last `data-message-id` (quoted and unquoted/minified), order from
  first/last, channel id from filename.
- `HtmlExportMerger`: splices new groups before the postamble, count updated to N+M, output still
  contains exactly one `<div class="postamble">` and one `</body>`, all original + new
  `data-message-id`s present in order. Most fixtures here (riskiest merge).
- Dispatch: `.txt` → unsupported; unknown channel id → refusal.

## Part B — Export ETA

### Scope
Show a live "time remaining" estimate beside the dashboard progress bar for normal exports and
continue. Reuses the existing muxed progress fraction (so multi-channel parallel exports get an
aggregate ETA).

### Approach
Discord exposes **no total-message count**, so ETA is derived from the progress **fraction** the
exporter already reports (`GetMessagesAsync` reports a `Gress.Percentage` based on where the current
message's timestamp sits between the first and last message in range).

`EtaEstimator` (Core or GUI util, pure + unit-testable):
```csharp
public sealed class EtaEstimator
{
    public void Report(double fraction, DateTimeOffset now);   // sample
    public TimeSpan? Estimate { get; }                          // null until confident
    public void Reset();
}
```
- Keeps a rolling window of `(fraction, time)` samples (default ~60s, capped count).
- `rate = (fractionNewest − fractionOldest) / (timeNewest − timeOldest)`; `Estimate =
  (1 − fractionNewest) / rate`.
- Returns `null` (UI shows "estimating…") until: window spans ≥ a few seconds AND fraction is in
  `(0,1)` AND rate > 0. Returns `TimeSpan.Zero`/hides at fraction ≥ 1.
- Robust to the non-linearity of timestamp-based progress via the rolling window (matches the
  "rate over the last minute" intuition).

### GUI wiring (Part B)
- `DashboardViewModel`: an `EtaEstimator` instance; subscribe to `Progress` updates (the VM already
  watches `Progress.Current`); on each change call `Report(Progress.Current.Fraction, clock)` and
  expose an observable `EtaText` (e.g. `"~2m14s left"`, `"estimating…"`, or empty when idle/done).
  `Reset()` at the start of each export/continue run; clear when `IsBusy` goes false.
- Time source is injectable (a `Func<DateTimeOffset>`), defaulting to wall clock, so the estimator
  is unit-testable. (Note: scripts/workflows ban `DateTimeOffset.Now`; the GUI runtime does not —
  the estimator reads the injected clock.)
- A `TextBlock` next to the dashboard `ProgressBar` bound to `EtaText`; visible only while busy.
- New localization strings: `EtaEstimating`, `EtaRemainingFormat` (`"~{0} left"`).

### Non-goals (Part B)
- **Messages/sec readout.** The progress signal is fraction-based, not count-based, and there's no
  total count, so a true msg/s rate would require threading a live message counter through the
  Core export → progress pipeline (a separate, more invasive change). Deferred; can be added later
  if desired. ETA shows **time remaining only** for now.
- Per-channel ETA breakdown during multi-channel export (the dashboard shows one aggregate bar;
  ETA is aggregate to match).

### Testing (Part B)
`EtaEstimator` unit tests with an injected clock: returns null before confidence; computes a correct
estimate for a steady rate; updates as rate changes; handles fraction stalls (rate 0 → null);
returns ~0/empty at completion; reset clears samples.

## Failure-mode check
1. **HTML splice markers shift due to minification differences** → both existing and new files are
   produced by the same minifier with the same options, so the `<div class="postamble">` and
   chatlog-close markers are stable; parsing is quote-tolerant. Merge writes to temp + atomic
   replace, so a failed splice cannot corrupt the original. *Mitigated.*
2. **CSV boundary duplicate / same-ms ambiguity** → boundary skip (`≤ cutoff`) removes the common
   dup; rare same-ms edge accepted (documented; CSV has no id). *Minor, documented.*
3. **Filename lacks `[id]`** (renamed/custom) and format has no in-file id (CSV) → refused with
   guidance rather than guessing the wrong channel. *Handled.*
4. **ETA wild early estimates** → "estimating…" gate until the window is confident; rolling window
   smooths spikes. *Mitigated.*
5. **HTML count regex misses a locale-grouped number** → match digits with optional grouping
   separators from the export `CultureInfo`; if not matched, leave the count unchanged (cosmetic)
   rather than corrupt the file. *Minor, contained.*

## Rollout
Rebuild the self-contained win-x64 GUI and redeploy over `outputs/DiscordChatExporter-user-copy/`
(as in v1). No schema changes; no migration.
