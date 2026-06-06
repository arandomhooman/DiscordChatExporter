using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Platform.Storage;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Continuation;
using DiscordChatExporter.Gui.Localization;
using DiscordChatExporter.Gui.Services;
using DiscordChatExporter.Gui.ViewModels.Components;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class DashboardContinueExportPickerTests
{
    [Fact]
    public void Continue_export_picker_accepts_sqlite_exports()
    {
        var method = typeof(DashboardViewModel).GetMethod(
            "CreateContinueExportFileTypes",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        method.Should().NotBeNull();
        var fileTypes = ((IReadOnlyList<FilePickerFileType>)method!.Invoke(null, null)!)
            .Should()
            .ContainSingle()
            .Subject;

        fileTypes.Name.Should().Be("Supported exports (JSON, HTML, CSV, SQLite)");
        fileTypes.Patterns.Should().BeEquivalentTo(["*.json", "*.html", "*.htm", "*.csv", "*.db"]);
    }

    [Fact]
    public void Continue_export_localization_mentions_sqlite_exports()
    {
        using var localization = new LocalizationManager(new SettingsService());

        localization.ContinueExportTooltip.Should().Contain("SQLite");
        localization.ContinueExportFormatUnsupportedMessage.Should().Contain("SQLite");
    }

    [Fact]
    public void Continue_export_uses_selected_channels_when_resolving_server_export_files()
    {
        var guild = new Guild(new Snowflake(100), "Test Guild", "");
        var general = new Channel(
            new Snowflake(200),
            ChannelKind.GuildTextChat,
            guild.Id,
            null,
            "general",
            0,
            null,
            null,
            false,
            new Snowflake(2000)
        );
        var random = general with { Id = new Snowflake(300), Name = "random" };

        var dir = Path.Combine(
            Path.GetTempPath(),
            $"{nameof(DashboardContinueExportPickerTests)}-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(dir);

        try
        {
            var generalPath = Path.Combine(
                dir,
                ExportRequest.GetDefaultOutputFileName(guild, general, ExportFormat.Json)
            );
            var randomPath = Path.Combine(
                dir,
                ExportRequest.GetDefaultOutputFileName(guild, random, ExportFormat.Json)
            );

            File.WriteAllText(generalPath, "");
            File.WriteAllText(randomPath, "");

            var cutoff = new ContinuationCutoff(
                general.Id,
                new Snowflake(2500),
                null,
                true,
                1,
                true
            );
            var method = typeof(DashboardViewModel).GetMethod(
                "ResolveSelectedContinueExportFilePaths",
                BindingFlags.NonPublic | BindingFlags.Static
            );

            method.Should().NotBeNull();
            var paths =
                (IReadOnlyList<string>)
                    method!.Invoke(null, [generalPath, guild, new[] { general, random }, cutoff])!;

            paths.Should().Equal(generalPath, randomPath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
