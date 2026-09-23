using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
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
        // Tolerate pasted keys carrying a "Bearer " prefix; both gateways expect
        // the raw key and would otherwise reject the request.
        var key = (apiKey ?? string.Empty).Trim();
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = key["Bearer ".Length..].Trim();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", key);
        _debugPath = debugPath;
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync("/api/monitor/usage/quota/limit", ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UsageRequestException(UsageFailureKind.Timeout, "GLM quota request timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw ClassifyTransportFailure(ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);

#if DEBUG
            // Keep raw server payloads only in developer builds. Release builds should
            // not continuously persist account-usage responses to disk.
            try
            {
                var directory = Path.GetDirectoryName(_debugPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_debugPath, body);
            }
            catch
            {
                // Debug logging is non-fatal.
            }
#endif

            if (!resp.IsSuccessStatusCode)
                throw ClassifyHttpFailure(resp.StatusCode, body);

            QuotaLimitResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<QuotaLimitResponse>(body, JsonOpts);
            }
            catch (JsonException ex)
            {
                throw new UsageRequestException(
                    UsageFailureKind.InvalidResponse,
                    "GLM quota response was not valid JSON",
                    ex,
                    resp.StatusCode);
            }

            // HTTP 200 does not mean success here: both gateways report rejected
            // keys as 200 + {"code":…,"msg":…,"success":false}. Without this the
            // envelope falls through to "no data object" and gets classified as
            // an unrecognizable response instead of an authentication failure.
            if (parsed?.IsErrorEnvelope == true)
                throw ClassifyErrorEnvelope(parsed, body, resp.StatusCode);

            if (parsed?.Data is null)
            {
                throw new UsageRequestException(
                    UsageFailureKind.InvalidResponse,
                    "GLM quota response did not contain a data object",
                    statusCode: resp.StatusCode);
            }

            var limits = parsed.Data.Limits ?? new List<RawLimit>();
            var snapshot = MapSnapshot(limits, parsed.Data.Level, body);

            // A successful Coding Plan response is expected to expose at least one
            // token quota. A successful HTTP response containing no token quota is
            // much more useful to users as "this account has no Coding Plan" than
            // as a mysterious empty card.
            if (!snapshot.HasHourly && !snapshot.HasWeekly)
            {
                throw new UsageRequestException(
                    UsageFailureKind.NoCodingPlan,
                    "No Coding Plan token quota was present in the GLM response",
                    statusCode: resp.StatusCode);
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Classifies an HTTP 200 error envelope. Reuses the body heuristics used
    /// for non-200 responses, then falls back to the envelope's own code.
    /// </summary>
    private static UsageRequestException ClassifyErrorEnvelope(QuotaLimitResponse parsed, string body, HttpStatusCode statusCode)
    {
        var code = parsed.ErrorCode;
        var described = $"GLM error envelope (code {code ?? "?"}): {parsed.ErrorMsg ?? "<no message>"}";

        if (LooksLikeAuthenticationFailure(parsed.ErrorMsg ?? string.Empty) ||
            LooksLikeAuthenticationFailure(body) ||
            code is "401" or "403" or "1001")
            return new UsageRequestException(UsageFailureKind.Authentication, described, statusCode: statusCode);

        if (LooksLikeMissingPlan(body))
            return new UsageRequestException(UsageFailureKind.NoCodingPlan, described, statusCode: statusCode);

        if (code == "429")
            return new UsageRequestException(UsageFailureKind.RateLimited, described, statusCode: statusCode);

        return new UsageRequestException(UsageFailureKind.InvalidResponse, described, statusCode: statusCode);
    }

    private static UsageRequestException ClassifyHttpFailure(HttpStatusCode statusCode, string body)
    {
        var code = (int)statusCode;

        if (code == 407)
            return new UsageRequestException(UsageFailureKind.Proxy, $"GLM proxy authentication failed (HTTP {code})", statusCode: statusCode);

        if (code == 429)
            return new UsageRequestException(UsageFailureKind.RateLimited, $"GLM rate limit (HTTP {code})", statusCode: statusCode);

        if (code == 402 || LooksLikeMissingPlan(body))
            return new UsageRequestException(UsageFailureKind.NoCodingPlan, $"No Coding Plan quota (HTTP {code})", statusCode: statusCode);

        if (code is 401 or 403 || LooksLikeAuthenticationFailure(body))
            return new UsageRequestException(UsageFailureKind.Authentication, $"GLM authentication failed (HTTP {code})", statusCode: statusCode);

        if (code >= 500)
            return new UsageRequestException(UsageFailureKind.ServiceUnavailable, $"GLM service error (HTTP {code})", statusCode: statusCode);

        return new UsageRequestException(UsageFailureKind.ServiceUnavailable, $"Unexpected GLM response (HTTP {code})", statusCode: statusCode);
    }

    private static UsageRequestException ClassifyTransportFailure(HttpRequestException ex)
    {
        if (HasInner<AuthenticationException>(ex) || ContainsAny(ex.Message, "ssl", "tls", "certificate", "证书"))
            return new UsageRequestException(UsageFailureKind.Tls, "TLS/certificate connection failure", ex, ex.StatusCode);

        if (ContainsAny(ex.Message, "proxy", "tunnel"))
            return new UsageRequestException(UsageFailureKind.Proxy, "Proxy connection failure", ex, ex.StatusCode);

        return new UsageRequestException(UsageFailureKind.Network, "Network connection failure", ex, ex.StatusCode);
    }

    private static bool LooksLikeMissingPlan(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var lower = body.ToLowerInvariant();
        bool mentionsPlan = lower.Contains("coding plan") || lower.Contains("subscription") || lower.Contains("套餐") || lower.Contains("订阅");
        bool saysMissing = lower.Contains("not found") || lower.Contains("not subscribed") || lower.Contains("no plan") ||
                           lower.Contains("not activated") || lower.Contains("未开通") || lower.Contains("未订阅") || lower.Contains("不存在");
        return mentionsPlan && saysMissing;
    }

    private static bool LooksLikeAuthenticationFailure(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return ContainsAny(body, "unauthorized", "invalid api key", "invalid key", "authorization", "authentication", "鉴权", "密钥无效", "key无效");
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static bool HasInner<T>(Exception ex) where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is T) return true;
        return false;
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
