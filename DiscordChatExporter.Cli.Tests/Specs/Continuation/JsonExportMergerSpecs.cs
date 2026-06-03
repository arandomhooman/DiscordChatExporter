using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class JsonExportMergerSpecs
{
    private static string Existing(string messages, int count) =>
        $$"""
            {
              "guild": { "id": "111", "name": "G" },
              "channel": { "id": "222", "name": "C" },
              "dateRange": { "after": null, "before": null },
              "exportedAt": "2021-01-01T00:00:00+00:00",
              "messages": [{{messages}}],
              "messageCount": {{count}}
            }
            """;

    private const string MsgA =
        """{ "id": "1000", "type": "Default", "timestamp": "2021-07-19T13:34:18+00:00", "content": "a" }""";
    private const string MsgB =
        """{ "id": "2000", "type": "Default", "timestamp": "2021-07-24T13:49:13+00:00", "content": "b" }""";
    private const string MsgC =
        """{ "id": "3000", "type": "Default", "timestamp": "2021-07-25T10:00:00+00:00", "content": "c" }""";

    // A message with a nested object (author) and nested arrays (reactions, mentions) to
    // exercise the depth-tracking token-pump on non-flat structures.
    private const string MsgNested = """
        {
          "id": "4000",
          "type": "Default",
          "timestamp": "2021-07-26T10:00:00+00:00",
          "content": "nested",
          "author": { "id": "5", "name": "Author", "isBot": false },
          "reactions": [ { "emoji": { "name": "+1" }, "count": 3 } ],
          "mentions": [],
          "attachments": [ { "id": "9", "url": "http://x/y", "fileSizeBytes": 1234 } ]
        }
        """;

    private static async Task<string> WriteAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dce-merge-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    [Fact]
    public async Task It_appends_new_messages_and_fixes_the_count()
    {
        var existing = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        var fresh = await WriteAsync(Existing(MsgC, 1));
        try
        {
            var total = await JsonExportMerger.MergeAsync(
                existing,
                fresh,
                new DateTimeOffset(2026, 06, 03, 0, 0, 0, TimeSpan.Zero)
            );

            total.Should().Be(3);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var root = doc.RootElement;
            var ids = root.GetProperty("messages")
                .EnumerateArray()
                .Select(m => m.GetProperty("id").GetString())
                .ToArray();
            ids.Should().Equal("1000", "2000", "3000");
            root.GetProperty("messageCount").GetInt64().Should().Be(3);
            root.GetProperty("guild").GetProperty("id").GetString().Should().Be("111");
            root.GetProperty("exportedAt").GetString().Should().Contain("2026-06-03");

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
    public async Task It_appends_into_an_export_that_had_no_messages()
    {
        var existing = await WriteAsync(Existing("", 0));
        var fresh = await WriteAsync(Existing($"{MsgA},{MsgB}", 2));
        try
        {
            var total = await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            total.Should().Be(2);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            doc.RootElement.GetProperty("messages").GetArrayLength().Should().Be(2);
            doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(2);
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
    public async Task It_produces_valid_json_whose_count_matches_actual_elements()
    {
        var existing = await WriteAsync(Existing(MsgA, 1));
        var fresh = await WriteAsync(Existing($"{MsgB},{MsgC}", 2));
        try
        {
            await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var actual = doc.RootElement.GetProperty("messages").GetArrayLength();
            doc.RootElement.GetProperty("messageCount").GetInt64().Should().Be(actual);
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
    public async Task It_preserves_nested_object_and_array_structure_in_copied_messages()
    {
        // Existing has a nested message; fresh adds another nested message. This guards the
        // depth-tracking CopyValue token-pump against nested objects and arrays.
        var existing = await WriteAsync(Existing(MsgNested, 1));
        var fresh = await WriteAsync(Existing(MsgNested.Replace("\"4000\"", "\"5000\""), 1));
        try
        {
            var total = await JsonExportMerger.MergeAsync(existing, fresh, DateTimeOffset.UtcNow);
            total.Should().Be(2);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(existing));
            var root = doc.RootElement;
            var messages = root.GetProperty("messages");
            messages.GetArrayLength().Should().Be(2);
            root.GetProperty("messageCount").GetInt64().Should().Be(2);

            var first = messages[0];
            first.GetProperty("id").GetString().Should().Be("4000");
            first.GetProperty("author").GetProperty("id").GetString().Should().Be("5");
            first.GetProperty("author").GetProperty("isBot").GetBoolean().Should().BeFalse();

            var reactions = first.GetProperty("reactions");
            reactions.GetArrayLength().Should().Be(1);
            reactions[0].GetProperty("emoji").GetProperty("name").GetString().Should().Be("+1");
            reactions[0].GetProperty("count").GetInt32().Should().Be(3);

            first.GetProperty("mentions").GetArrayLength().Should().Be(0);

            var attachments = first.GetProperty("attachments");
            attachments[0].GetProperty("fileSizeBytes").GetInt64().Should().Be(1234);

            // The second (appended) message keeps its rewritten id and nested structure too.
            var second = messages[1];
            second.GetProperty("id").GetString().Should().Be("5000");
            second.GetProperty("author").GetProperty("name").GetString().Should().Be("Author");
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
