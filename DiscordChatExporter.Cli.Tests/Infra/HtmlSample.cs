using System.Collections.Generic;
using System.Linq;
using WebMarkupMin.Core;

namespace DiscordChatExporter.Cli.Tests.Infra;

// Produces HTML byte-shaped like a real DiscordChatExporter HTML export: each block minified
// separately with the SAME minifier HtmlMessageWriter uses, joined by newlines. The chatlog
// open/close are wrapped in wmm:ignore exactly like PreambleTemplate/PostambleTemplate, so the
// minifier preserves them verbatim (matching production).
public static class HtmlSample
{
    private static readonly HtmlMinifier Minifier = new();

    private static string Minify(string html) => Minifier.Minify(html, false).MinifiedContent;

    private const string PreambleHtml =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body>"
        + "<div class=\"preamble\"><div class=\"preamble__entry\">Guild</div></div>"
        + "<!--wmm:ignore--><div class=\"chatlog\"><!--/wmm:ignore-->";

    private static string MessageGroupHtml(IEnumerable<long> ids) =>
        "<div class=\"chatlog__message-group\">"
        + string.Concat(
            ids.Select(id =>
                $"<div id=\"chatlog__message-container-{id}\" class=\"chatlog__message-container\" data-message-id=\"{id}\">"
                + "<div class=\"chatlog__content chatlog__markdown\"><span class=\"chatlog__markdown-preserve\">msg "
                + id
                + "</span></div></div>"
            )
        )
        + "</div>";

    private static string PostambleHtml(long count) =>
        "<!--wmm:ignore--></div><!--/wmm:ignore-->"
        + "<div class=\"postamble\"><div class=\"postamble__entry\">Exported "
        + count.ToString("n0")
        + " message(s)</div></div></body></html>";

    public static string Export(params long[][] groups)
    {
        var blocks = new List<string> { Minify(PreambleHtml) };
        blocks.AddRange(groups.Select(g => Minify(MessageGroupHtml(g))));
        var total = groups.Sum(g => g.LongLength);
        blocks.Add(Minify(PostambleHtml(total)));
        return string.Join("\n", blocks);
    }
}
