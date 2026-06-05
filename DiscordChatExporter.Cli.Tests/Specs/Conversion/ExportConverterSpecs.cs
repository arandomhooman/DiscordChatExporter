using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AngleSharp.Dom;
using DiscordChatExporter.Cli.Tests.Utils;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Core.Exporting.Conversion;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Library;
using DiscordChatExporter.Core.Exporting.Partitioning;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Conversion;

public sealed class ExportConverterSpecs : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "DceConverter_" + Guid.NewGuid().ToString("N")
    );

    public ExportConverterSpecs() => Directory.CreateDirectory(_dir);

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
            null,
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

    private static Message CreateMessage(ulong id, User author, string content) =>
        new(
            new Snowflake(id),
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch.AddSeconds(id),
            null,
            null,
            false,
            content,
            [],
            [],
            [],
            [],
            [],
            null,
            null,
            null,
            null
        );

    private async Task<string> WriteJsonAsync()
    {
        var path = Path.Combine(_dir, "chat.json");
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        await writer.WriteMessageAsync(CreateMessage(1001, author, "the quick brown fox"));
        await writer.WritePostambleAsync();
        return path;
    }

    private async Task<string> WriteJsonAsync(string fileName, params Message[] messages)
    {
        var path = Path.Combine(_dir, fileName);
        await using var writer = new JsonMessageWriter(File.Create(path), CreateContext(path));
        await writer.WritePreambleAsync();
        foreach (var message in messages)
            await writer.WriteMessageAsync(message);

        await writer.WritePostambleAsync();
        return path;
    }

    private async Task<string> WriteRawJsonAsync(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private async Task<string> WriteLegacyMentionJsonAsync()
    {
        var path = Path.Combine(_dir, "legacy-mention.json");
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        var mention = new User(
            new Snowflake(11),
            false,
            null,
            "bob",
            "Bob Display",
            "avatar-local.png"
        );

        await using (var writer = new JsonMessageWriter(File.Create(path), CreateContext(path)))
        {
            await writer.WritePreambleAsync();
            await writer.WriteMessageAsync(
                CreateMessage(1001, author, "hello <@11> <#30>") with
                {
                    MentionedUsers = [mention],
                }
            );
            await writer.WritePostambleAsync();
        }

        var json = await File.ReadAllTextAsync(path);
        var conversionDataStart = json.IndexOf(
            ",\r\n  \"conversionData\"",
            StringComparison.Ordinal
        );
        if (conversionDataStart < 0)
            conversionDataStart = json.IndexOf(",\n  \"conversionData\"", StringComparison.Ordinal);

        await File.WriteAllTextAsync(path, json[..conversionDataStart] + "\n}");
        return path;
    }

    private async Task<string> WriteJsonWithLocalAvatarAndRemoteConversionDataAsync()
    {
        var path = await WriteLegacyMentionJsonAsync();
        var json = await File.ReadAllTextAsync(path);
        json = json.Replace(
            """
                  "avatarUrl": ""
            """,
            """
                  "avatarUrl": "avatar-local.png"
            """,
            StringComparison.Ordinal
        );
        await File.WriteAllTextAsync(
            path,
            json.TrimEnd('}', '\r', '\n')
                + """
                ,
                  "conversionData": {
                    "schemaVersion": 1,
                    "members": [
                      {
                        "id": "10",
                        "displayName": "alice",
                        "avatarUrl": "https://cdn.example/remote-avatar.png",
                        "colorHex": null,
                        "roleIds": []
                      }
                    ],
                    "roles": [],
                    "channels": []
                  }
                }
                """
        );
        return path;
    }

    [Fact]
    public async Task Converter_writes_sqlite_and_csv_outputs_from_json()
    {
        var jsonPath = await WriteJsonAsync();
        var dbOut = Path.Combine(_dir, "out.db");
        var csvOut = Path.Combine(_dir, "out.csv");

        await ExportConverter.ConvertAsync(jsonPath, dbOut, ExportFormat.Db);
        await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);

        (await SqliteExportReader.SearchAsync(dbOut, "quick", 50, default))
            .Should()
            .ContainSingle();
        (await File.ReadAllTextAsync(csvOut)).Should().Contain("quick brown fox");
    }

    [Fact]
    public async Task Converter_formats_legacy_mentions_offline_from_embedded_message_data()
    {
        var jsonPath = await WriteLegacyMentionJsonAsync();
        var csvOut = Path.Combine(_dir, "legacy.csv");

        await ExportConverter.ConvertAsync(jsonPath, csvOut, ExportFormat.Csv);

        var csv = await File.ReadAllTextAsync(csvOut);
        csv.Should().Contain("@Bob Display");
        csv.Should().Contain("#deleted-channel");
    }

    [Fact]
    public async Task Converter_preserves_message_avatar_url_over_conversion_data_avatar_url()
    {
        var jsonPath = await WriteJsonWithLocalAvatarAndRemoteConversionDataAsync();
        var htmlOut = Path.Combine(_dir, "avatar.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var html = await File.ReadAllTextAsync(htmlOut);
        html.Should().Contain("avatar-local.png");
        html.Should().NotContain("remote-avatar.png");
    }

    [Fact]
    public async Task Converter_converts_html_with_invite_links_offline_without_fetching_invites()
    {
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        var jsonPath = await WriteJsonAsync(
            "offline-invite.json",
            CreateMessage(1001, author, "join https://discord.gg/offline")
        );
        var htmlOut = Path.Combine(_dir, "offline-invite.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        document.Body?.TextContent.Should().Contain("https://discord.gg/offline");
        document.QuerySelector(".chatlog__embed-invite-container").Should().BeNull();
    }

    [Fact]
    public async Task Converter_relinks_replies_to_messages_in_the_same_json_export()
    {
        var author = new User(new Snowflake(10), false, null, "alice", "alice", "");
        var original = CreateMessage(1001, author, "original body");
        var reply = CreateMessage(1002, author, "reply body") with
        {
            Kind = MessageKind.Reply,
            Reference = new MessageReference(
                MessageReferenceKind.Default,
                original.Id,
                new Snowflake(2),
                new Snowflake(1)
            ),
        };
        var jsonPath = await WriteJsonAsync("reply-relink.json", original, reply);
        var htmlOut = Path.Combine(_dir, "reply-relink.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        var replyElement = document.QuerySelector("""[data-message-id="1002"]""");
        replyElement.Should().NotBeNull();
        replyElement!.QuerySelector(".chatlog__reply-unknown").Should().BeNull();
        replyElement
            .QuerySelector(".chatlog__reply-link")
            ?.Text()
            .Should()
            .Contain("original body");
    }

    [Fact]
    public async Task Converter_preserves_inline_emoji_image_url_in_html()
    {
        var jsonPath = await WriteRawJsonAsync(
            "inline-emoji-url.json",
            """
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": ""
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": "topic"
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "local emoji <:local:12345>",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": [
                    {
                      "id": "12345",
                      "name": "local",
                      "code": "local",
                      "isAnimated": false,
                      "imageUrl": "emoji-local.png"
                    }
                  ]
                }
              ],
              "messageCount": 1
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "inline-emoji-url.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var document = Html.Parse(await File.ReadAllTextAsync(htmlOut));
        document
            .QuerySelectorAll(".chatlog__emoji")
            .Select(e => e.GetAttribute("src"))
            .Should()
            .Contain("emoji-local.png");
    }

    [Fact]
    public async Task Converter_uses_conversion_data_channel_kind_for_channel_mentions()
    {
        var jsonPath = await WriteRawJsonAsync(
            "voice-channel-mention.json",
            """
            {
              "guild": {
                "id": "1",
                "name": "Test Guild",
                "iconUrl": ""
              },
              "channel": {
                "id": "2",
                "type": "GuildTextChat",
                "categoryId": null,
                "category": null,
                "name": "test-channel",
                "topic": "topic"
              },
              "messages": [
                {
                  "id": "1001",
                  "type": "Default",
                  "timestamp": "1970-01-01T00:16:41.0000000+00:00",
                  "timestampEdited": null,
                  "callEndedTimestamp": null,
                  "isPinned": false,
                  "content": "Voice channel mention: <#30>",
                  "author": {
                    "id": "10",
                    "name": "alice",
                    "discriminator": "0000",
                    "nickname": "alice",
                    "color": null,
                    "isBot": false,
                    "roles": [],
                    "avatarUrl": ""
                  },
                  "attachments": [],
                  "embeds": [],
                  "stickers": [],
                  "reactions": [],
                  "mentions": [],
                  "inlineEmojis": []
                }
              ],
              "messageCount": 1,
              "conversionData": {
                "schemaVersion": 1,
                "members": [],
                "roles": [],
                "channels": [
                  {
                    "id": "30",
                    "name": "voice-room",
                    "type": "GuildVoiceChat",
                    "isVoice": true
                  }
                ]
              }
            }
            """
        );
        var htmlOut = Path.Combine(_dir, "voice-channel-mention.html");

        await ExportConverter.ConvertAsync(jsonPath, htmlOut, ExportFormat.HtmlDark);

        var text = Html.Parse(await File.ReadAllTextAsync(htmlOut)).Body?.TextContent;
        text.Should().Contain("Voice channel mention: 🔊voice-room");
        text.Should().NotContain("#voice-room");
    }

    [Fact]
    public async Task Converter_rejects_json_as_a_target_format()
    {
        var jsonPath = await WriteJsonAsync();
        var jsonOut = Path.Combine(_dir, "out.json");

        await FluentActions
            .Awaiting(() =>
                ExportConverter.ConvertAsync(jsonPath, jsonOut, ExportFormat.Json).AsTask()
            )
            .Should()
            .ThrowAsync<InvalidExportException>();
    }
}
