using System;
using System.IO;
using System.Threading.Tasks;
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
