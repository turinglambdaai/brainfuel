using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BrainFuel.Services;

/// <summary>
/// Calls the GLM Coding Plan monitor endpoint to read current quota usage.
/// Mirrors what the official `glm-plan-usage` plugin does:
///   GET {baseDomain}/api/monitor/usage/quota/limit
///   Authorization: &lt;GLM Coding Plan key&gt;   (raw value, no "Bearer " prefix)
/// </summary>
public sealed class GlmUsageClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _debugPath;

    public GlmUsageClient(string baseDomain, string apiKey, string debugPath)
    {
        var root = baseDomain.TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(root), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", apiKey ?? string.Empty);
        _debugPath = debugPath;
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/monitor/usage/quota/limit", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // Always dump the raw payload so field mappings stay auditable.
        try { File.WriteAllText(_debugPath, body); } catch { /* non-fatal */ }

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"quota/limit HTTP {(int)resp.StatusCode}");

        var parsed = JsonSerializer.Deserialize<QuotaLimitResponse>(body, JsonOpts);
        var limits = parsed?.Data?.Limits ?? new List<RawLimit>();
        return MapSnapshot(limits, parsed?.Data?.Level, body);
    }

    private static UsageSnapshot MapSnapshot(List<RawLimit> limits, string? level, string raw)
    {
        var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.Now, RawJson = raw, PlanLevel = level };

        // Heuristic: tolerate servers that report 0..1 instead of 0..100.
        double maxPct = 0;
        foreach (var l in limits)
            if (l.Percentage is double p && p > maxPct) maxPct = p;
        double scale = maxPct > 0 && maxPct <= 1.5 ? 100.0 : 1.0;

        foreach (var lim in limits)
        {
            var type = (lim.Type ?? "").Trim().ToUpperInvariant();
            if (type != "TOKENS_LIMIT")
                continue; // TIME_LIMIT is the MCP monthly quota — not shown in v1.

            var pct = (lim.Percentage ?? 0) * scale;
            var reset = TryFindReset(lim);

            // `number == 5` is the 5-hour token window; the other TOKENS_LIMIT is the weekly quota.
            if (lim.Number == 5)
            {
                snap.HasHourly = true;
                snap.HourlyUsedPct = pct;
                snap.HourlyResetAt = reset;
            }
            else if (!snap.HasWeekly)
            {
                snap.HasWeekly = true;
                snap.WeeklyUsedPct = pct;
                snap.WeeklyResetAt = reset;
            }
        }

        return snap;
    }

    private static DateTimeOffset? TryFindReset(RawLimit lim)
    {
        if (lim.NextResetTime is long ms && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);

        if (lim.Extra is null) return null;
        foreach (var (key, val) in lim.Extra)
        {
            var k = key.ToUpperInvariant();
            if ((k.Contains("RESET") || k.Contains("EXPIRE") || k.Contains("END") || k.Contains("NEXT"))
                && val.ValueKind == JsonValueKind.Number && val.TryGetInt64(out var v) && v > 0)
                return DateTimeOffset.FromUnixTimeMilliseconds(v);
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
