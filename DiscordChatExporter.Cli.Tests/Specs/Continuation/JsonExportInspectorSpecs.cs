using System;
using System.IO;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class JsonExportInspectorSpecs
{
    private static async Task<string> WriteTempAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private const string TwoMessages = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [
            { "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" },
            { "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }
          ],
          "messageCount": 2
        }
        """;

    private const string NoMessages = """
        {
          "guild": { "id": "111", "name": "G" },
          "channel": { "id": "222", "name": "C" },
          "dateRange": { "after": null, "before": null },
          "exportedAt": "2021-01-01T00:00:00+00:00",
          "messages": [],
          "messageCount": 0
        }
        """;

    [Fact]
    public async Task It_reads_guild_channel_cutoff_and_count_from_a_valid_export()
    {
        var path = await WriteTempAsync(TwoMessages);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);

            info.GuildId.Value.Should().Be(111UL);
            info.ChannelId.Value.Should().Be(222UL);
            info.LastMessageId.Value.Should().Be(2000UL);
            info.MessageCount.Should().Be(2);
            info.IsChronological.Should().BeTrue();
            info.Before.Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task It_flags_a_reverse_ordered_export_as_not_chronological()
    {
        var reversed = TwoMessages
            .Replace("\"2021-07-19T13:34:18+00:00\"", "\"2021-07-24T13:49:13+00:00\"X")
            .Replace("\"2021-07-24T13:49:13+00:00\"", "\"2021-07-19T13:34:18+00:00\"")
            .Replace("\"2021-07-19T13:34:18+00:00\"X", "\"2021-07-24T13:49:13+00:00\"");
        var path = await WriteTempAsync(reversed);
        try
        {
            var info = await JsonExportInspector.InspectAsync(path);
            info.IsChronological.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task It_rejects_an_export_with_no_messages()
    {
        var path = await WriteTempAsync(NoMessages);
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task It_rejects_a_non_dce_json_file()
    {
        var path = await WriteTempAsync("""{ "hello": "world" }""");
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task It_rejects_malformed_json()
    {
        var path = await WriteTempAsync("{ not json");
        try
        {
            var act = async () => await JsonExportInspector.InspectAsync(path);
            await act.Should().ThrowAsync<InvalidJsonExportException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
