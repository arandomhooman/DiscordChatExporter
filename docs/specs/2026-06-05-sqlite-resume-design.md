# SQLite (.db) Resume — Design

**Status:** Approved (2026-06-05). Feature ① of a two-feature batch; feature ② (JSON → everything
conversion + Conversion tab) is deferred to its own spec/plan cycle (see "Deferred" below).

## Goal

Make SQLite (`.db`) exports resumable through the existing **Continue export** feature, exactly as
JSON / HTML / CSV already are. After a resume, the existing `.db` gains the messages posted since it
was made — content, authors, attachments, reaction counts, and the FTS index — and its `export_info`
metadata is refreshed.

## Background

The Continue-export flow (in `DashboardViewModel.ContinueExportAsync`) is format-generic:

1. User picks an existing export file.
2. `ContinuationFormat.ReadCutoffAsync(file)` → a `ContinuationCutoff` (the resume point).
3. A fresh export of just the new messages is written to a **temp** file in the same format, using
   the channel id + `before` boundary from the cutoff and `cutoff.Cutoff` as the exclusive Discord
   `after` cursor.
4. `ContinuationFormat.MergeAsync(existingFile, tempFile, cutoff, now)` merges the temp file into the
   existing file and returns the new total message count.
5. The temp file is deleted; the catalog/manifest is refreshed (added 2026-06-05).

Each format supplies a pair under `Core/Exporting/Continuation/`: an **Inspector** (step 2, reads the
cutoff) and a **Merger** (step 4, appends). `ContinuationFormat` dispatches to them by file extension.
Today `.db` is absent from that dispatcher, so the Continue button rejects it as unsupported.

**Key simplification:** the `SqliteMessageWriter` needs **no changes**. The temp export in step 3 is a
brand-new `.db`, so the writer's existing clean-slate behavior is correct. The append happens entirely
in the merger via SQL — the existing `.db` is never reopened by the writer.

## Architecture

Two new Core units in `DiscordChatExporter.Core.Exporting.Continuation`, plus wiring `.db` into
`ContinuationFormat`, plus one GUI file-picker line. No writer changes, no engine changes.

### `SqliteExportInspector` (new)

```csharp
public static class SqliteExportInspector
{
    public static ValueTask<ContinuationCutoff> InspectAsync(
        string filePath,
        CancellationToken cancellationToken = default
    );
}
```

Opens the `.db` **read-only** (`Mode=ReadOnly`, `Pooling=False`, matching `SqliteExportReader`) and
builds a `ContinuationCutoff`:

`ContinuationCutoff` shape (existing record, unchanged):
`(Snowflake ChannelId, Snowflake Cutoff, Snowflake? Before, bool IsChronological, long ExistingCount, bool CutoffIsExact)`

- **ChannelId** — `SELECT channel_id FROM export_info LIMIT 1`, parsed to `Snowflake`. The `.db` is
  self-describing, so unlike CSV/HTML there is no filename parsing.
- **Cutoff** — newest message: `SELECT id FROM messages ORDER BY CAST(id AS INTEGER) DESC LIMIT 1`.
  Snowflakes fit SQLite's signed 64-bit INTEGER; a lexicographic `ORDER BY id` would be wrong for
  numbers, hence the `CAST`. `CutoffIsExact = true`.
- **Before** — `export_info.before` is stored as an ISO-8601 date string (the writer persists
  `NormalizeOrNull(Request.Before?.ToDate())`). Reconstruct as `Snowflake.FromDate(parsedDate)` when
  present and parseable; otherwise `null`. (Round-trips the original `Before = Snowflake.FromDate(...)`.)
- **ExistingCount** — `SELECT COUNT(*) FROM messages`.
- **IsChronological** — **always `true`** for `.db`. A database is an unordered row set merged by id,
  so resuming is safe even if the original was exported `--reverse` (the text formats must refuse that
  because their on-disk order matters; the `.db` has none).

**Empty-database edge case:** if `messages` is empty, there is no last-message id. Fall back to the
original lower bound: `Cutoff = Snowflake.FromDate(export_info.after)` when `after` is present, else
`new Snowflake(0)` (resume from the beginning). Set `CutoffIsExact = false`. This makes "continue an
export that was empty at creation" fetch everything posted since the original start.

**Validation / failure:** if the file is missing, not a SQLite database, or lacks the
`export_info` / `messages` tables (e.g. a foreign or older-schema `.db`), throw
`InvalidExportException` (the existing exception the other inspectors throw for unparseable files), so
the GUI surfaces the standard "couldn't read this export" message rather than crashing.

### `SqliteExportMerger` (new)

```csharp
public static class SqliteExportMerger
{
    public static ValueTask<long> MergeAsync(
        string existingDatabaseFilePath,
        string newDatabaseFilePath,
        ContinuationCutoff cutoff,
        DateTimeOffset now,
        CancellationToken cancellationToken = default
    );
}
```

Opens the **existing** `.db` read-write (`Pooling=False`, `journal_mode=DELETE` to keep it a single
file with no lingering sidecars, matching the writer), then within one transaction:

1. `ATTACH DATABASE '<newDatabaseFilePath>' AS incoming;`
2. Copy the new rows (new messages are strictly after `cutoff.Cutoff`, so there is no real overlap;
   `OR IGNORE` on the id-keyed tables is a boundary safety net):
   - `INSERT OR IGNORE INTO main.messages   SELECT * FROM incoming.messages;`
   - `INSERT OR IGNORE INTO main.authors    SELECT * FROM incoming.authors;`
   - `INSERT INTO main.attachments          SELECT * FROM incoming.attachments;`
   - `INSERT INTO main.reactions            SELECT * FROM incoming.reactions;`
   - `INSERT INTO main.messages_fts (content, message_id) SELECT content, message_id FROM incoming.messages_fts;`
3. `UPDATE export_info SET message_count = (SELECT COUNT(*) FROM main.messages), exported_at = $now;`
4. `COMMIT;`, `DETACH DATABASE incoming;`
5. Return `SELECT COUNT(*) FROM main.messages` (the new total).

`attachments` / `reactions` have no primary key, but because the incoming set is strictly newer than
the cutoff there is no duplication to guard against; copying them straight is correct. Disposal
releases the file handle (`Pooling=False`) so the subsequent catalog hash/refresh can read the file.

### `ContinuationFormat` wiring (modify)

Add a `.db` arm in each of the four dispatch points:

- `IsSupportedExtension` — add `".db"` to the recognized set.
- `FormatFor` — `".db" => ExportFormat.Db`.
- `ReadCutoffAsync` — `".db" => SqliteExportInspector.InspectAsync(...)`.
- `MergeAsync` — `".db" => SqliteExportMerger.MergeAsync(...)`.

### GUI (modify)

`DashboardViewModel.ContinueExportAsync` — add `*.db` to the file-picker `Patterns` and update the
picker label to include SQLite. Everything else in `ContinueExportAsync` is already format-generic:
the request is built from the cutoff, and `RefreshContinuedExportCatalogAsync` (added 2026-06-05) will
refresh the `.db`'s manifest entry and re-register its folder — so after a resume the Library's catalog
count and FTS search index both reflect the appended messages with no extra work.

## Data flow (resume a `.db`)

```
User picks chat.db
  → ContinuationFormat.ReadCutoffAsync("chat.db")
      → SqliteExportInspector: channelId+before from export_info, cutoff = MAX(id), count
  → export new messages (after cutoff, before boundary) to <temp>.db  [fresh SqliteMessageWriter]
  → ContinuationFormat.MergeAsync("chat.db", "<temp>.db", cutoff, now)
      → SqliteExportMerger: ATTACH temp, INSERT OR IGNORE new rows + FTS, update export_info, COMMIT
  → newMessages = total - cutoff.ExistingCount  → success snackbar
  → RefreshContinuedExportCatalogAsync → manifest count/size/hash refreshed, dir registered
  → temp .db deleted
```

## Error handling

- Unreadable / non-SQLite / wrong-schema `.db` → `InvalidExportException` → standard GUI message.
- Channel no longer accessible / token invalid → the existing `ContinueExportAsync` catch paths handle
  it (unchanged).
- "Up to date" (no new messages) → `newMessages <= 0` → existing up-to-date snackbar; the merge still
  runs harmlessly (zero rows copied) and `exported_at` refreshes.
- Merge failure (IO, locked file) → the transaction is not committed (rolled back on dispose) and the
  exception propagates to the existing catch; the original `.db` is left intact.

## Testing strategy

All token-free (no Discord token needed), using the real `SqliteMessageWriter` to build fixtures, in
`DiscordChatExporter.Cli.Tests/Specs/Continuation/`:

- **Inspector:** write a `.db` with messages 1001–1003 → `InspectAsync` returns `Cutoff == 1003`,
  `ChannelId` from `export_info`, `ExistingCount == 3`, `IsChronological == true`.
- **Inspector (empty):** write a `.db` with no messages → `Cutoff` falls back to the original `after`
  (or `0`), `CutoffIsExact == false`, `ExistingCount == 0`.
- **Inspector (bad file):** a non-SQLite file and a SQLite file missing `export_info` → throws
  `InvalidExportException`.
- **Merger:** existing `.db` (1001–1003) + temp `.db` (1004–1005) → `MergeAsync` returns 5; existing db
  then has 5 message rows, 5 FTS rows, `export_info.message_count == 5`, and an FTS query for a 1004/1005
  message returns it.
- **Merger (boundary dup):** temp also contains 1003 → still 5 rows total (dup ignored, not doubled),
  and 1003's attachments/reactions are not duplicated.
- **Round-trip:** inspector cutoff from the merged db == 1005.

GUI continue flow itself is covered by the existing manual smoke (needs a token); no new headless GUI
test is required since the merge logic is fully token-free testable in Core.

## Explicitly out of scope (this feature)

- Writer append-mode (unneeded — temp + SQL merge).
- Schema versioning/migration (single current schema; mismatched/foreign `.db` is rejected, not migrated).
- Partitioned `.db` resume (already refused for all formats via `IsPartitionedExportPath`).
- The conversion feature (see below).

## Deferred — Feature ② (separate spec/plan)

**JSON → everything conversion + a Conversion tab.** Decided direction (2026-06-05): convert offline
with no network; preserve all data. Enhancement: new JSON exports will embed a trailing
**conversion-data block** (the resolved member / role / channel / color lookups already held in the
`ExportContext` at export time) so converting them is fully faithful; legacy JSON without the block
degrades gracefully (raw `@id`, default color). Requires a new JSON→`Message` deserializer and an
offline `ExportContext`, then reuses the existing writers; the Conversion page follows the Library
page pattern. To be designed after ① ships.
