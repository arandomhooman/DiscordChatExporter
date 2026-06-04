using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace DiscordChatExporter.Core.Exporting.Manifest;

public static class ManifestBuilder
{
    // Builds one self-describing manifest entry per output file in the result.
    // `assetCount` is recorded as best-effort and only when assets were actually resolved.
    public static IReadOnlyList<ManifestEntry> Build(
        ManifestChannelInfo info,
        ExportResult result,
        DateTimeOffset now
    )
    {
        var partitioned = result.Files.Count > 1;
        var entries = new List<ManifestEntry>(result.Files.Count);

        foreach (var file in result.Files)
        {
            long sizeBytes;
            string sha256;

            // Reading an output file's size/hash is best-effort: if a single file is missing,
            // locked, or unreadable, skip just that entry and keep cataloguing the rest.
            try
            {
                sizeBytes = new FileInfo(file.FilePath).Length;
                sha256 = ComputeSha256(file.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var fileName = Path.GetFileName(file.FilePath);

            entries.Add(
                new ManifestEntry(
                    GuildId: info.GuildId,
                    GuildName: info.GuildName,
                    ChannelId: info.ChannelId,
                    ChannelName: info.ChannelName,
                    CategoryName: info.CategoryName,
                    File: fileName,
                    Format: info.Format,
                    MessageCount: file.MessageCount,
                    FirstMessageId: file.FirstMessageId?.ToString(),
                    FirstMessageTimestamp: file.FirstMessageTimestamp,
                    LastMessageId: file.LastMessageId?.ToString(),
                    LastMessageTimestamp: file.LastMessageTimestamp,
                    AssetCount: result.AssetCount > 0 ? result.AssetCount : null,
                    FileSizeBytes: sizeBytes,
                    Sha256: sha256,
                    Partitioned: partitioned,
                    ExportedAt: now
                )
            );
        }

        return entries;
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
