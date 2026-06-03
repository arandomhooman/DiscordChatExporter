using System;
using DiscordChatExporter.Core.Exporting;
using FluentAssertions;
using Xunit;

namespace DiscordChatExporter.Cli.Tests.Specs;

public class EtaEstimatorSpecs
{
    [Fact]
    public void It_returns_null_until_it_has_a_confident_window()
    {
        var t = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator();
        eta.Report(0.0, t);
        eta.Estimate.Should().BeNull();
    }

    [Fact]
    public void It_estimates_time_remaining_from_a_steady_rate()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator(minWindow: TimeSpan.FromSeconds(2));
        eta.Report(0.0, t0);
        eta.Report(0.10, t0 + TimeSpan.FromSeconds(5));
        eta.Estimate.Should().NotBeNull();
        eta.Estimate!.Value.TotalSeconds.Should().BeApproximately(45, 2);
    }

    [Fact]
    public void It_returns_zero_when_complete()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator(minWindow: TimeSpan.FromSeconds(2));
        eta.Report(0.0, t0);
        eta.Report(1.0, t0 + TimeSpan.FromSeconds(5));
        eta.Estimate.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void It_returns_null_when_progress_stalls()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var eta = new EtaEstimator(minWindow: TimeSpan.FromSeconds(2));
        eta.Report(0.3, t0);
        eta.Report(0.3, t0 + TimeSpan.FromSeconds(5));
        eta.Estimate.Should().BeNull();
    }
}
