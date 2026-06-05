using System.IO;
using System.Text.RegularExpressions;

namespace DiscordChatExporter.Core.Exporting.Continuation;

internal static partial class ContinuationFileName
{
    [GeneratedRegex(@"\([^)]*(?:\bbefore\b|\bto\b)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex BeforeBoundHintRegex();

    public static bool HasBeforeBoundHint(string filePath) =>
        BeforeBoundHintRegex().IsMatch(Path.GetFileNameWithoutExtension(filePath));
}
