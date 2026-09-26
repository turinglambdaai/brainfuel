using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

public sealed class AverageBurnRateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static List<UsageSample> Samples(params (int MinAgo, double Hourly)[] items) =>
        items.Select(i => new UsageSample(T0.AddMinutes(-i.MinAgo), i.Hourly, double.NaN)).ToList();

    [Fact]
    public void PositiveDeltas_Only_AreSummed()
    {
        // 20 -> 40 -> 35 -> 55: two rises (+40) and one drop (-5, ignored)
        var samples = Samples((240, 20), (180, 40), (120, 35), (60, 55));
        var rate = UsageHistoryStore.AverageBurnRateLast24h(samples, T0);
        Assert.NotNull(rate);
        Assert.Equal(40 / 24.0, rate!.Value, 4);
    }

    [Fact]
    public void ResetInBetween_StillCountsFullBurn()
    {
        // 45 -> (reset) -> 5 -> 40: positive deltas are 35; the reset drop is ignored.
        var samples = Samples((180, 45), (120, 5), (60, 40));
        var rate = UsageHistoryStore.AverageBurnRateLast24h(samples, T0);
        Assert.Equal(35 / 24.0, rate!.Value, 4);
    }

    [Fact]
    public void AbsentWindow_BreaksTheChain()
    {
        // NaN between samples: pairs across it must not be compared.
        var samples = Samples((240, 20), (180, double.NaN), (120, 40));
        var rate = UsageHistoryStore.AverageBurnRateLast24h(samples, T0);
        // Only 40 alone in its chain: no pair at all -> null
        Assert.Null(rate);
    }

    [Fact]
    public void SamplesOutsideWindow_AreIgnored()
    {
        var samples = Samples((30 * 60, 10), (29 * 60, 90)); // 30h/29h ago: outside 24h
        Assert.Null(UsageHistoryStore.AverageBurnRateLast24h(samples, T0));
    }

    [Fact]
    public void SingleSample_YieldsNull()
    {
        var samples = Samples((60, 42));
        Assert.Null(UsageHistoryStore.AverageBurnRateLast24h(samples, T0));
    }

    [Fact]
    public void EmptyHistory_YieldsNull()
    {
        Assert.Null(UsageHistoryStore.AverageBurnRateLast24h(new List<UsageSample>(), T0));
    }
}
