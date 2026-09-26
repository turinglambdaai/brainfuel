using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace BrainFuel.Services;

/// <summary>
/// One quota observation: percentages of both windows at a point in time.
/// A value of double.NaN means that window was absent from the response.
/// Percentages only — never the API key and never raw payloads.
/// </summary>
public readonly record struct UsageSample(DateTimeOffset At, double HourlyPct, double WeeklyPct);

/// <summary>
/// Rolling local usage history backing the detail-panel graphs. Persisted as
/// a compact JSON array next to settings.json; pruned to the retention window
/// and a hard sample cap on every save.
/// </summary>
public sealed class UsageHistoryStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(8);
    private const int MaxSamples = 3000;

    private readonly string _path;
    private readonly List<UsageSample> _samples = new();

    private UsageHistoryStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(SettingsService.AppDirectory, "usage-history.json");

    public IReadOnlyList<UsageSample> Samples => _samples;

    public static UsageHistoryStore Load(string path)
    {
        var store = new UsageHistoryStore(path);
        try
        {
            if (!File.Exists(path)) return store;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return store;

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object ||
                    !e.TryGetProperty("at", out var at) ||
                    at.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var t))
                    continue;

                double Read(string name) =>
                    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                        ? v.GetDouble()
                        : double.NaN;

                store._samples.Add(new UsageSample(t, Read("h"), Read("w")));
            }
            store._samples.Sort((a, b) => a.At.CompareTo(b.At));
        }
        catch (Exception ex)
        {
            // A corrupt history must never break startup; start fresh and note it.
            AppLog.Error($"usage-history load failed, starting fresh: {ex.Message}");
            store._samples.Clear();
        }
        return store;
    }

    public void Append(UsageSample sample)
    {
        // Keep the list time-ordered even if the clock jumps backwards.
        int i = _samples.Count;
        while (i > 0 && _samples[i - 1].At > sample.At) i--;
        _samples.Insert(i, sample);
    }

    /// <summary>Drops samples outside the retention window, beyond the cap.</summary>
    public void Prune(DateTimeOffset now)
    {
        _samples.RemoveAll(s => now - s.At > Retention);
        if (_samples.Count > MaxSamples)
            _samples.RemoveRange(0, _samples.Count - MaxSamples);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDirectory);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartArray();
                foreach (var s in _samples)
                {
                    writer.WriteStartObject();
                    writer.WriteString("at", s.At);
                    WritePct(writer, "h", s.HourlyPct);
                    WritePct(writer, "w", s.WeeklyPct);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            File.WriteAllText(_path, System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch (Exception ex)
        {
            AppLog.Error($"usage-history save failed: {ex.Message}");
        }
    }

    private static void WritePct(Utf8JsonWriter writer, string name, double pct)
    {
        if (double.IsNaN(pct)) return; // absent window: omit, reads back as NaN
        writer.WriteNumber(name, Math.Round(pct, 2));
    }
}
