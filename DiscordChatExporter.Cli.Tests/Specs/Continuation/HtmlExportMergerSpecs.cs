using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlExportMergerSpecs
{
    private static async Task<string> WriteAsync(string html, string suffix = "")
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"dce-htmlmerge-{Guid.NewGuid():N}{suffix}.html"
        );
        await File.WriteAllTextAsync(path, html);
        return path;
    }

    // Local copy of HtmlExportInspector.MessageIdRegex (which is internal to Core and not
    // visible to this test assembly — there is no InternalsVisibleTo). Same quote-tolerant
    // pattern, so it reads the same ids the merger does.
    private static long[] Ids(string html) =>
        Regex
            .Matches(html, "data-message-id=\"?(\\d+)\"?")
            .Select(m => long.Parse(m.Groups[1].Value))
            .ToArray();

    [Fact]
    public async Task It_splices_new_groups_recomputes_the_count_and_dedupes_overlap()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L, 2000L]));
        var fresh = await WriteAsync(HtmlSample.Export([2000L, 3000L]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(2000),
            null,
            true,
            2,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(3);

            var merged = await File.ReadAllTextAsync(existing);
            Ids(merged).Should().Equal(1000L, 2000L, 3000L); // ascending, deduped (2000 not doubled)
            Regex.Matches(merged, "<div class=\"chatlog\">").Count.Should().Be(1);
            Regex.Matches(merged, "<div class=\"?postamble\"?>").Count.Should().Be(1);
            merged.Should().Contain("Exported 3 message(s)");
            File.Exists(existing + ".bak").Should().BeTrue();
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    [Fact]
    public async Task It_appends_into_an_html_export_that_had_no_messages()
    {
        var existing = await WriteAsync(HtmlSample.Export());
        var fresh = await WriteAsync(HtmlSample.Export([1000L]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1),
            null,
            true,
            0,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(1);
            Ids(await File.ReadAllTextAsync(existing)).Should().Equal(1000L);
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
