using System;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting.Filtering;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class MessageFilterFactorySpecs
{
    private static Message CreateMessage(User author) =>
        new(
            Snowflake.Zero,
            MessageKind.Default,
            MessageFlags.None,
            author,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            false,
            "hello",
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

    [Fact]
    public void I_can_create_an_author_filter_from_a_display_name_with_spaces()
    {
        // Arrange
        var matchingMessage = CreateMessage(
            new User(new Snowflake(123), false, null, "josh", "Josh Smith", "")
        );

        var nonMatchingMessage = CreateMessage(
            new User(new Snowflake(456), false, null, "alex", "Alex Smith", "")
        );

        // Act
        var filter = MessageFilter.FromAuthor("Josh Smith");

        // Assert
        filter.IsMatch(matchingMessage).Should().BeTrue();
        filter.IsMatch(nonMatchingMessage).Should().BeFalse();
    }
}
