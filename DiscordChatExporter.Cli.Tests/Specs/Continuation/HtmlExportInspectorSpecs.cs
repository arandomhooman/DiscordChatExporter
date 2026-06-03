using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Cli.Tests.Infra;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class HtmlExportInspectorSpecs
{
    private static async Task<string> WriteAsync(string html)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"Guild - general [222] - {Guid.NewGuid():N}.html"
        );
        await File.WriteAllTextAsync(path, html);
        return path;
    }

    [Fact]
    public async Task I_can_read_the_exact_cutoff_count_and_channel_from_an_html_export()
    {
        var path = await WriteAsync(HtmlSample.Export([1000L, 2000L], [3000L]));
        try
        {
            var info = await HtmlExportInspector.InspectAsync(path);
            info.ChannelId.Value.Should().Be(222UL);
            info.Cutoff.Value.Should().Be(3000UL);
            info.CutoffIsExact.Should().BeTrue();
            info.ExistingCount.Should().Be(3);
            info.IsChronological.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task I_cannot_continue_an_html_export_with_no_messages()
    {
        var path = await WriteAsync(HtmlSample.Export());
        try
        {
            var act = async () => await HtmlExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
