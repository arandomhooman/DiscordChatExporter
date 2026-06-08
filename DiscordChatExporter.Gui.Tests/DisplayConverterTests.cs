using System;
using System.Globalization;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Gui.Converters;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Gui.Tests;

public sealed class DisplayConverterTests
{
    [Fact]
    public void Export_format_converter_uses_display_names_for_enum_values()
    {
        ExportFormatToStringConverter
            .Instance.Convert(
                ExportFormat.HtmlDark,
                typeof(string),
                null,
                CultureInfo.InvariantCulture
            )
            .Should()
            .Be("HTML (Dark)");
    }

    [Fact]
    public void Export_format_converter_uses_display_names_for_manifest_strings()
    {
        ExportFormatToStringConverter
            .Instance.Convert("Db", typeof(string), null, CultureInfo.InvariantCulture)
            .Should()
            .Be("SQLite");
    }

    [Fact]
    public void Timestamp_converter_formats_date_time_offsets_with_requested_culture()
    {
        var timestamp = new DateTimeOffset(2026, 06, 08, 14, 30, 0, TimeSpan.Zero);

        TimestampToStringConverter
            .Instance.Convert(timestamp, typeof(string), null, CultureInfo.GetCultureInfo("en-US"))
            .Should()
            .Be("6/8/2026 2:30 PM");
    }

    [Fact]
    public void Timestamp_converter_formats_parseable_timestamp_strings()
    {
        TimestampToStringConverter
            .Instance.Convert(
                "2026-06-08T14:30:00+00:00",
                typeof(string),
                null,
                CultureInfo.GetCultureInfo("en-US")
            )
            .Should()
            .Be("6/8/2026 2:30 PM");
    }
}
