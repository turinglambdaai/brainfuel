using System;
using System.Collections.Generic;

namespace BrainFuel.Services;

/// <summary>
/// Estimates how fast a quota window is being consumed (% per hour) from
/// consecutive usage samples, and projects time-to-exhaustion. A decreasing
/// percentage means the window reset — history is dropped so a reset never
/// reads as "negative burn".
/// </summary>
public sealed class QuotaBurnTracker
{
    private const double MinSampleSpanHours = 5.0 / 60.0;   // 5 min: below this, rounding noise dominates
    private const double RateValidForHours = 2.0;           // older estimates no longer describe reality

    private readonly List<(DateTimeOffset At, double Pct)> _samples = new();

    public DateTimeOffset? RateComputedAt { get; private set; }

    /// <summary>Consumption speed in used-% per hour; null until two samples
    /// at least <see cref="MinSampleSpanHours"/> apart show an increase.</summary>
    public double? RatePctPerHour { get; private set; }

    public void AddSample(DateTimeOffset at, double usedPct)
    {
        usedPct = Math.Clamp(usedPct, 0, 100);

        if (_samples.Count > 0 && usedPct < _samples[^1].Pct)
        {
            // Window reset (or plan upgrade): burn history from the old window
            // would be misleading — drop the samples *and* the stale rate.
            _samples.Clear();
            RatePctPerHour = null;
            RateComputedAt = null;
        }

        _samples.Add((at, usedPct));

        // Keep the monotonic tail bounded; two points are enough for a rate.
        while (_samples.Count > 4)
            _samples.RemoveAt(0);

        var first = _samples[0];
        var last = _samples[^1];
        double span = (last.At - first.At).TotalHours;
        if (span >= MinSampleSpanHours && last.Pct > first.Pct)
        {
            RatePctPerHour = (last.Pct - first.Pct) / span;
            RateComputedAt = last.At;
        }
    }

    /// <summary>True while the estimate is recent enough to be worth showing.</summary>
    public bool HasCurrentRate(DateTimeOffset now)
        => RatePctPerHour is > 0 && RateComputedAt is { } at && (now - at).TotalHours <= RateValidForHours;

    /// <summary>Hours until the window is fully used at the current burn rate.</summary>
    public double? ProjectHoursToExhaustion(double usedPct, DateTimeOffset now)
    {
        if (!HasCurrentRate(now) || RatePctPerHour is not { } rate)
            return null;
        double hours = (100 - Math.Clamp(usedPct, 0, 100)) / rate;
        return hours >= 0 ? hours : null;
    }
}

/// <summary>Visual urgency derived from the used percentage.</summary>
public enum SeverityLevel { Calm, Amber, Red }

public static class Severity
{
    public const double AmberThreshold = 75;
    public const double RedThreshold = 90;

    public static SeverityLevel FromUsedPct(double usedPct) => usedPct switch
    {
        >= RedThreshold => SeverityLevel.Red,
        >= AmberThreshold => SeverityLevel.Amber,
        _ => SeverityLevel.Calm,
    };
}
