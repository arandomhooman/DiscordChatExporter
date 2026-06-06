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

    // Regression: the count rewrite must only touch the postamble. A user message whose body
    // literally reads "Exported 7 message(s)" precedes the postamble; before the fix the rewrite
    // grabbed the FIRST match (the user's text) and left the real postamble count stale.
    [Fact]
    public async Task It_does_not_rewrite_a_count_that_appears_in_message_content()
    {
        // Existing: one message (id 1000) whose CONTENT is the misleading "Exported 7 message(s)".
        var existing = await WriteAsync(
            HtmlSample.ExportDetailed([(1000L, "Exported 7 message(s)", false)])
        );
        // Fresh: adds id 2000. New total is 2 — distinct from both the in-content 7 and the old 1.
        var fresh = await WriteAsync(HtmlSample.ExportDetailed([(2000L, "hi", false)]), "-new");
        var cutoff = new ContinuationCutoff(
            new Snowflake(222),
            new Snowflake(1000),
            null,
            true,
            1,
            true
        );
        try
        {
            var total = await HtmlExportMerger.MergeAsync(existing, fresh, cutoff);
            total.Should().Be(2);

            var merged = await File.ReadAllTextAsync(existing);
            // The user's text is untouched...
            merged.Should().Contain("Exported 7 message(s)");
            // ...and the postamble shows the real recomputed total.
            merged.Should().Contain("Exported 2 message(s)");
            Ids(merged).Should().Equal(1000L, 2000L);
        }
        finally
        {
            File.Delete(existing);
            File.Delete(fresh);
            if (File.Exists(existing + ".bak"))
                File.Delete(existing + ".bak");
        }
    }

    // Regression: a pinned message renders multi-token `class="chatlog__message-container
    // chatlog__message-container--pinned"`, which the minifier keeps QUOTED. The container-split
    // marker must be quote-tolerant or the pinned container in the overlap window isn't deduped,
    // producing a duplicate id that aborts the merge. The pinned 2000 must live in the FRESH export
    // (the slice being split/deduped) for this to bite.
    [Fact]
    public async Task It_dedupes_a_pinned_message_in_the_overlap_window()
    {
        var existing = await WriteAsync(HtmlSample.Export([1000L, 2000L]));
        // Fresh overlaps 2000 (pinned this time) and adds 3000.
        var fresh = await WriteAsync(
            HtmlSample.ExportDetailed([(2000L, "pinned msg", true), (3000L, "new msg", false)]),
            "-new"
        );
        // Sanity: the fresh export really does keep the pinned class quoted (else the test is moot).
        (await File.ReadAllTextAsync(fresh))
            .Should()
            .Contain("class=\"chatlog__message-container chatlog__message-container--pinned\"");

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
            Ids(merged).Should().Equal(1000L, 2000L, 3000L); // 2000 deduped, no duplicate
            merged.Should().Contain("Exported 3 message(s)");
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
