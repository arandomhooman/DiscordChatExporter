using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Discord.Data.Common;
using DiscordChatExporter.Core.Discord.Data.Embeds;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class JsonExportReaderSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceJsonReader_" + Guid.NewGuid().ToString("N")
    );

    public JsonExportReaderSpecs() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static ExportContext CreateContext(string outputPath)
    {
        var guild = new Guild(new Snowflake(1), "Test Guild", "");
        var channel = new Channel(
            new Snowflake(2),
            ChannelKind.GuildTextChat,
            new Snowflake(1),
            null,
            "test-channel",
            0,
            null,
            "topic",
            false,
            null
        );
        var request = new ExportRequest(
            guild,
            channel,
            outputPath,
            null,
            ExportFormat.Json,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            isReverseMessageOrder: false,
            shouldFormatMarkdown: false,
            shouldDownloadAssets: false,
            shouldReuseAssets: false,
            locale: "en-US",
            isUtcNormalizationEnabled: true
        );
        return new ExportContext(new DiscordClient("fake-token"), request);
    }

    private static User CreateUser(ulong id, string name) =>
        new(new Snowflake(id), false, null, name, name, "");

    private static Message CreateMessage(ulong id, User author, User mention) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
            null,
            null,
            false,
            "the quick brown fox",
            [
                new Attachment(
                    new Snowflake(50),
                    "https://cdn.example/file.png",
                    "file.png",
                    null,
                    null,
                    null,
                    FileSize.FromBytes(123)
                ),
            ],
            [
                new Embed(
                    "embed title",
                    EmbedKind.Rich,
                    null,
                    null,
                    null,
                    null,
                    "embed body",
                    [],
                    null,
                    [],
                    null,
                    null
                ),
            ],
            [],
            [],
            [mention],
            null,
            null,
            null,
            null
        );

    private async Task<string> WriteJsonAsync(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        var author = CreateUser(10, "alice");
        var mention = CreateUser(11, "bob");
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        await writer.WriteMessageAsync(CreateMessage(1001, author, mention));
        await writer.WriteMessageAsync(
            CreateMessage(1002, author, mention) with
            {
                Content = "second message",
            }
        );
        await writer.WritePostambleAsync();
        return path;
    }

    [Fact]
    public async Task Reader_reconstructs_messages_from_json_export()
    {
        var path = await WriteJsonAsync("chat.json");

        var parsed = await JsonExportReader.ParseAsync(path);

        parsed.Guild.Id.Should().Be(new Snowflake(1));
        parsed.Channel.Id.Should().Be(new Snowflake(2));
        parsed.Messages.Should().HaveCount(2);
        parsed.Messages[0].Content.Should().Be("the quick brown fox");
        parsed.Messages[0].Author.Id.Should().Be(new Snowflake(10));
        parsed.Messages[0].Attachments.Should().ContainSingle();
        parsed.Messages[0].Embeds.Should().ContainSingle().Which.Title.Should().Be("embed title");
        parsed
            .Messages[0]
            .MentionedUsers.Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(new Snowflake(11));
    }

    [Fact]
    public async Task Reader_rejects_malformed_or_non_dce_json()
    {
        var malformed = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(malformed, "{");
        var nonDce = Path.Combine(_dir, "not-dce.json");
        await File.WriteAllTextAsync(nonDce, "{\"hello\":1}");

        await FluentActions
            .Awaiting(() => JsonExportReader.ParseAsync(malformed).AsTask())
            .Should()
            .ThrowAsync<InvalidExportException>();
        await FluentActions
            .Awaiting(() => JsonExportReader.ParseAsync(nonDce).AsTask())
            .Should()
            .ThrowAsync<InvalidExportException>();
    }

    [Fact]
    public async Task Reader_parses_conversion_data_and_tolerates_legacy_json_without_it()
    {
        var path = await WriteJsonAsync("chat.json");

        (await JsonExportReader.ParseAsync(path)).ConversionData.Should().NotBeNull();

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("conversionData"))
                    continue;

                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        var legacyPath = Path.Combine(_dir, "legacy.json");
        await File.WriteAllBytesAsync(legacyPath, stream.ToArray());

        (await JsonExportReader.ParseAsync(legacyPath)).ConversionData.Should().BeNull();
    }
}
