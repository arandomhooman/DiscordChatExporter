using System;
using System.Collections.Generic;

namespace DiscordChatExporter.Core.Exporting;

// Estimates time remaining from a stream of (fraction, time) samples using a rolling window.
// Pure and clock-injectable for testability. Not thread-safe; call from one thread (the UI).
public sealed class EtaEstimator(TimeSpan? window = null, TimeSpan? minWindow = null)
{
    private readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(60);
    private readonly TimeSpan _minWindow = minWindow ?? TimeSpan.FromSeconds(3);
    private readonly List<(double Fraction, DateTimeOffset Time)> _samples = [];

    public void Report(double fraction, DateTimeOffset now)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        _samples.Add((fraction, now));
        var cutoff = now - _window;
        while (_samples.Count > 2 && _samples[0].Time < cutoff)
            _samples.RemoveAt(0);
    }

    public TimeSpan? Estimate
    {
        get
        {
            if (_samples.Count == 0)
                return null;
            var newest = _samples[^1];
            if (newest.Fraction >= 1.0)
                return TimeSpan.Zero;
            var oldest = _samples[0];
            var dt = (newest.Time - oldest.Time).TotalSeconds;
            var df = newest.Fraction - oldest.Fraction;
            if (dt < _minWindow.TotalSeconds || df <= 0)
                return null;
            var rate = df / dt;
            var remaining = (1.0 - newest.Fraction) / rate;
            return TimeSpan.FromSeconds(Math.Max(0, remaining));
        }
    }

    public void Reset() => _samples.Clear();
}
