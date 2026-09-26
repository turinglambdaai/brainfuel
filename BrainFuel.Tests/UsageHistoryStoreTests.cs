using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

public sealed class UsageHistoryStoreTests
{
    private static string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bf-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "usage-history.json");
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SaveThenLoad_RoundTripsSamples()
    {
        var path = TempPath();
        var store = UsageHistoryStore.Load(path);
        store.Append(new UsageSample(T0, 42.5, 68.25));
        store.Append(new UsageSample(T0.AddMinutes(5), 43.5, double.NaN)); // absent weekly
        store.Save();

        var back = UsageHistoryStore.Load(path);
        Assert.Equal(2, back.Samples.Count);
        Assert.Equal(T0, back.Samples[0].At);
        Assert.Equal(42.5, back.Samples[0].HourlyPct, 2);
        Assert.Equal(68.25, back.Samples[0].WeeklyPct, 2);
        Assert.True(double.IsNaN(back.Samples[1].WeeklyPct));
    }

    [Fact]
    public void OutOfOrderAppend_KeepsSortOrder()
    {
        var store = UsageHistoryStore.Load(TempPath());
        store.Append(new UsageSample(T0.AddMinutes(5), 10, 20));
        store.Append(new UsageSample(T0, 5, 15)); // clock jumped back

        Assert.Equal(T0, store.Samples[0].At);
        Assert.Equal(T0.AddMinutes(5), store.Samples[1].At);
    }

    [Fact]
    public void Prune_DropsOldAndCapsCount()
    {
        var store = UsageHistoryStore.Load(TempPath());
        // Samples 9 to 8+ days back: all outside the 8-day retention window.
        for (int i = 0; i < 20; i++)
            store.Append(new UsageSample(T0.AddDays(-9).AddMinutes(i * 10), i, i));
        store.Prune(T0);
        Assert.Empty(store.Samples);

        // Cap: beyond 3000 samples the oldest are dropped.
        for (int i = 0; i < 3010; i++)
            store.Append(new UsageSample(T0.AddMinutes(i), i % 100, i % 100));
        store.Prune(T0.AddMinutes(3009));
        Assert.Equal(3000, store.Samples.Count);
        Assert.Equal(T0.AddMinutes(10), store.Samples[0].At);
    }

    [Fact]
    public void CorruptFile_LoadsEmptyWithoutThrowing()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not json ");
        var store = UsageHistoryStore.Load(path);
        Assert.Empty(store.Samples);
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var store = UsageHistoryStore.Load(TempPath());
        Assert.Empty(store.Samples);
    }
}
