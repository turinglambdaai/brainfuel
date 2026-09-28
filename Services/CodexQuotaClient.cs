using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BrainFuel.Services;

/// <summary>
/// Reads Codex (ChatGPT subscription) rate-limit windows from the backend the
/// Codex CLI itself uses. Verified against a live Plus account 2026-09:
/// GET https://chatgpt.com/backend-api/wham/usage returns
/// rate_limit.primary_window (5 h) and secondary_window (week) with
/// used_percent and reset_at (unix seconds). The OAuth access token is read
/// fresh from ~/.codex/auth.json on every refresh.
/// </summary>
public sealed class CodexQuotaClient : IQuotaClient
{
    private const string Endpoint = "https://chatgpt.com/backend-api/wham/usage";

    private readonly HttpClient _http;
    private readonly Func<string?> _tokenProvider;

    public CodexQuotaClient()
        : this(new HttpClientHandler())
    {
    }

    internal CodexQuotaClient(HttpMessageHandler handler)
        : this(handler, () => LocalCliCredentials.ReadToken("codex"))
    {
    }

    internal CodexQuotaClient(HttpMessageHandler handler, Func<string?> tokenProvider)
    {
        _tokenProvider = tokenProvider;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", "codex_cli_rs");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default)
    {
        var token = _tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            throw new UsageRequestException(
                UsageFailureKind.Authentication,
                "Codex CLI login not found (~/.codex/auth.json)");

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (LocalCliCredentials.TryReadAccountId(out var accountId) && !string.IsNullOrWhiteSpace(accountId))
            req.Headers.Add("chatgpt-account-id", accountId);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UsageRequestException(UsageFailureKind.Timeout, "Codex usage request timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UsageRequestException(UsageFailureKind.Network, "Codex connection failure", ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UsageRequestException(
                    UsageFailureKind.Authentication,
                    $"Codex rejected the local login (HTTP {(int)resp.StatusCode}): {body[..Math.Min(200, body.Length)]}",
                    statusCode: resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new UsageRequestException(UsageFailureKind.RateLimited, "Codex usage endpoint rate-limited us", statusCode: resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                throw new UsageRequestException(
                    UsageFailureKind.ServiceUnavailable,
                    $"Unexpected Codex response (HTTP {(int)resp.StatusCode})",
                    statusCode: resp.StatusCode);

            CodexUsageResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<CodexUsageResponse>(body);
            }
            catch (JsonException ex)
            {
                throw new UsageRequestException(UsageFailureKind.InvalidResponse, "Codex usage response was not valid JSON", ex);
            }

            var rate = parsed?.rate_limit;
            if (rate?.primary_window is null && rate?.secondary_window is null)
                throw new UsageRequestException(UsageFailureKind.NoCodingPlan, "Codex usage response contained no rate-limit windows");

            var snap = new UsageSnapshot
            {
                FetchedAt = DateTimeOffset.Now,
                PlanLevel = parsed?.plan_type,
            };

            if (rate.primary_window is { } p)
            {
                snap.HasHourly = true; // the 5-hour window
                snap.HourlyUsedPct = Clamp(p.used_percent);
                snap.HourlyResetAt = ToTime(p.reset_at);
            }
            if (rate.secondary_window is { } s)
            {
                snap.HasWeekly = true;
                snap.WeeklyUsedPct = Clamp(s.used_percent);
                snap.WeeklyResetAt = ToTime(s.reset_at);
            }
            return snap;
        }
    }

    private static double Clamp(double? v) => Math.Clamp(v ?? 0, 0, 100);

    private static DateTimeOffset? ToTime(long? unixSeconds) =>
        unixSeconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) : null;

    public void Dispose() => _http.Dispose();
}

// ---- response shapes (subset of the real payload) ----

public sealed class CodexUsageResponse
{
    public string? plan_type { get; set; }
    public CodexRateLimit? rate_limit { get; set; }
}

public sealed class CodexRateLimit
{
    public CodexRateLimitWindow? primary_window { get; set; }
    public CodexRateLimitWindow? secondary_window { get; set; }
}

public sealed class CodexRateLimitWindow
{
    public double used_percent { get; set; }
    public long? reset_at { get; set; }
}
