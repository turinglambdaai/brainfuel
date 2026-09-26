using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

public sealed class QuotaBurnTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TwoSamplesSpanningMinutes_ProduceRate()
    {
        var t = new QuotaBurnTracker();
        t.AddSample(T0, 40);
        t.AddSample(T0.AddMinutes(30), 50); // +10 pct in 0.5 h -> 20 %/h

        Assert.True(t.HasCurrentRate(T0.AddMinutes(30)));
        Assert.Equal(20.0, t.RatePctPerHour!.Value, 3);
        // 50% used at 20%/h -> 2.5 h to empty
        Assert.Equal(2.5, t.ProjectHoursToExhaustion(50, T0.AddMinutes(30))!.Value, 3);
    }

    [Fact]
    public void SamplesTooCloseTogether_DoNotProduceRate()
    {
        var t = new QuotaBurnTracker();
        t.AddSample(T0, 40);
        t.AddSample(T0.AddMinutes(2), 45); // under the 5-minute noise floor

        Assert.False(t.HasCurrentRate(T0.AddMinutes(2)));
        Assert.Null(t.ProjectHoursToExhaustion(45, T0.AddMinutes(2)));
    }

    [Fact]
    public void DecreasingPercentage_ResetsHistory()
    {
        var t = new QuotaBurnTracker();
        t.AddSample(T0, 60);
        t.AddSample(T0.AddMinutes(30), 70);
        Assert.True(t.HasCurrentRate(T0.AddMinutes(30)));

        t.AddSample(T0.AddMinutes(31), 5); // window reset
        Assert.False(t.HasCurrentRate(T0.AddMinutes(31)));

        // New window needs its own two spaced samples again: 5->10 pct over 14 min.
        t.AddSample(T0.AddMinutes(45), 10);
        Assert.True(t.HasCurrentRate(T0.AddMinutes(45)));
        Assert.Equal(21.43, t.RatePctPerHour!.Value, 2);
    }

    [Fact]
    public void FlatUsage_ProducesNoRate()
    {
        var t = new QuotaBurnTracker();
        t.AddSample(T0, 40);
        t.AddSample(T0.AddMinutes(30), 40);

        Assert.False(t.HasCurrentRate(T0.AddMinutes(30)));
    }

    [Fact]
    public void StaleRate_ExpiresAfterTwoHours()
    {
        var t = new QuotaBurnTracker();
        t.AddSample(T0, 40);
        t.AddSample(T0.AddMinutes(30), 50);

        Assert.False(t.HasCurrentRate(T0.AddHours(3)));
        Assert.Null(t.ProjectHoursToExhaustion(50, T0.AddHours(3)));
    }

    [Fact]
    public void ExhaustedWindow_ProjectsZeroHours()
    {
        var t = new QuotaBurnTracker();
        t.AddSample(T0, 40);
        t.AddSample(T0.AddMinutes(30), 50);

        Assert.Equal(0, t.ProjectHoursToExhaustion(100, T0.AddMinutes(30))!.Value, 3);
    }
}

public sealed class SeverityTests
{
    [Theory]
    [InlineData(0, SeverityLevel.Calm)]
    [InlineData(74.9, SeverityLevel.Calm)]
    [InlineData(75, SeverityLevel.Amber)]
    [InlineData(89.9, SeverityLevel.Amber)]
    [InlineData(90, SeverityLevel.Red)]
    [InlineData(100, SeverityLevel.Red)]
    public void Thresholds(double usedPct, SeverityLevel expected)
    {
        Assert.Equal(expected, Severity.FromUsedPct(usedPct));
    }
}
