namespace DiscordChatExporter.Core.Exporting.Library;

// One full-text-search hit. DatabaseFilePath identifies the source .db so the GUI can label
// the hit by joining it back to the catalog entry for that file.
public sealed record SqliteSearchHit(
    string DatabaseFilePath,
    string MessageId,
    string Timestamp,
    string AuthorName,
    string Snippet
);
