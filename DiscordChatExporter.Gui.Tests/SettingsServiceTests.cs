using DiscordChatExporter.Gui.Services;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void Token_persistence_is_opt_in_by_default()
    {
        var settingsService = new SettingsService();

        settingsService.IsTokenPersisted.Should().BeFalse();
    }
}
