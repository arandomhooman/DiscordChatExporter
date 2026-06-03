using DiscordChatExporter.Core.Exporting.Continuation;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs.Continuation;

public class ContinuationFormatSpecs
{
    [Theory]
    [InlineData("x.json", true)]
    [InlineData("x.html", true)]
    [InlineData("x.htm", true)]
    [InlineData("x.csv", true)]
    [InlineData("x.txt", false)]
    [InlineData("x.xml", false)]
    public void It_recognizes_supported_extensions(string path, bool supported)
    {
        ContinuationFormat.IsSupportedExtension(path).Should().Be(supported);
    }
}
