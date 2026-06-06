using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class CsvExportMergerSpecs
{
    private const string Header = "AuthorID,Author,Date,Content,Attachments,Reactions\r\n";

    private static string Row(string date, string content) =>
        $"\"5\",\"A\",\"{date}\",\"{content}\",\"\",\"\"\r\n";

    private static async Task<string> WriteAsync(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-csvmerge-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Fact]
    public async Task It_appends_new_rows_skipping_the_header_and_boundary_rows()
    {
        var d1 = "2021-07-19T13:34:18.0000000+00:00";
        var d2 = "2021-07-24T13:49:13.0000000+00:00";
        var d3 = "2021-07-25T10:00:00.0000000+00:00";
        var existing = await WriteAsync(Header + Row(d1, "a") + Row(d2, "b"));
        var fresh = await WriteAsync(Header + Row(d2, "b") + Row(d3, "c"));
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            Snowflake.FromDate(DateTimeOffset.Parse(d2)),
            null,
            true,
            2,
            false
        );
        try
        {
            var added = await CsvExportMerger.MergeAsync(existing, fresh, cutoff);
            added.Should().Be(1);

            var lines = (await File.ReadAllLinesAsync(existing)).Where(l => l.Length > 0).ToArray();
            lines[0].Should().StartWith("AuthorID,");
            lines.Count(l => l.StartsWith("AuthorID,")).Should().Be(1);
            lines.Last().Should().Contain("2021-07-25");
            // The atomic-replace .bak is a transient crash-safety net, cleaned up on success.
            File.Exists(existing + ".bak").Should().BeFalse();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }
}
