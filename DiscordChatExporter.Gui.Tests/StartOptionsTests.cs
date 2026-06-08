using System.IO;
using DiscordChatExporter.Gui;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class StartOptionsTests
{
    [Fact]
    public void Default_settings_path_uses_per_user_appdata()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            null,
            null,
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Users\Alice\AppData\Local\DiscordChatExporter\Settings.dat");
    }

    [Fact]
    public void Settings_path_override_uses_file_path()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            @"C:\Settings\Custom.dat",
            null,
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Settings\Custom.dat");
    }

    [Fact]
    public void Settings_path_override_appends_file_name_for_directory_path()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            @"C:\Settings\",
            null,
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Settings\Settings.dat");
    }

    [Fact]
    public void Portable_mode_uses_executable_directory()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            null,
            "true",
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Portable\App\Settings.dat");
    }

    [Fact]
    public void Settings_path_override_wins_over_portable_mode()
    {
        var settingsPath = StartOptions.ResolveSettingsPath(
            @"C:\Settings\Custom.dat",
            "true",
            @"C:\Portable\App",
            @"C:\Users\Alice\AppData\Local"
        );

        settingsPath.Should().Be(@"C:\Settings\Custom.dat");
    }
}
