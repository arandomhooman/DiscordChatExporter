using System;
using Gress;

namespace DiscordChatExporter.Core.Exporting;

// Richer per-channel progress: fallback fraction, pre-filter messages walked, and timestamp position.
public readonly record struct ExportProgress(
    Percentage Fraction,
    long MessagesRead,
    DateTimeOffset? CurrentTimestamp
);
