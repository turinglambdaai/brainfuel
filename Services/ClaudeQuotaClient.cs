using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BrainFuel.Services;

/// <summary>
/// Reads Claude subscription usage windows from the OAuth endpoint that
/// powers Claude Code's /usage command (community-documented, verified shape):
/// GET https://api.anthropic.com/api/oauth/usage with the Claude Code OAuth
/// access token from ~/.claude/.credentials.json. Requires the
/// oauth-2025-04-20 beta flag and a claude-code/* User-Agent (without it the
/// endpoint sits in an aggressively rate-limited bucket).
/// </summary>
public sealed class ClaudeQuotaClient : IQuotaClient
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";

    private readonly HttpClient _http;
    private readonly Func<string?> _tokenProvider;

    public ClaudeQuotaClient()
        : this(new HttpClientHandler())
    {
    }

    internal ClaudeQuotaClient(HttpMessageHandler handler)
        : this(handler, () => LocalCliCredentials.ReadToken("claude"))
    {
    }

    internal ClaudeQuotaClient(HttpMessageHandler handler, Func<string?> tokenProvider)
    {
        _tokenProvider = tokenProvider;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", "claude-code/2.0");
        _http.DefaultRequestHeaders.Add("anthropic-beta", "oauth-2025-04-20");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default)
    {
        var token = _tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            throw new UsageRequestException(
                UsageFailureKind.Authentication,
                "Claude Code login not found (~/.claude/.credentials.json)");

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UsageRequestException(UsageFailureKind.Timeout, "Claude usage request timed out", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UsageRequestException(UsageFailureKind.Network, "Claude connection failure", ex);
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new UsageRequestException(
                    UsageFailureKind.Authentication,
                    $"Claude rejected the local login (HTTP {(int)resp.StatusCode}) — re-login via Claude Code",
                    statusCode: resp.StatusCode);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new UsageRequestException(UsageFailureKind.RateLimited, "Claude usage endpoint rate-limited us", statusCode: resp.StatusCode);
            if (!resp.IsSuccessStatusCode)
                throw new UsageRequestException(
                    UsageFailureKind.ServiceUnavailable,
                    $"Unexpected Claude response (HTTP {(int)resp.StatusCode})",
                    statusCode: resp.StatusCode);

            ClaudeUsageResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ClaudeUsageResponse>(body);
            }
            catch (JsonException ex)
            {
                throw new UsageRequestException(UsageFailureKind.InvalidResponse, "Claude usage response was not valid JSON", ex);
            }

            if (parsed?.five_hour is null && parsed?.seven_day is null)
                throw new UsageRequestException(UsageFailureKind.NoCodingPlan, "Claude usage response contained no usage windows");

            var snap = new UsageSnapshot { FetchedAt = DateTimeOffset.Now };
            if (parsed.five_hour is { } h)
            {
                snap.HasHourly = true;
                snap.HourlyUsedPct = Math.Clamp(h.utilization ?? 0, 0, 100);
                snap.HourlyResetAt = ParseTime(h.resets_at);
            }
            if (parsed.seven_day is { } w)
            {
                snap.HasWeekly = true;
                snap.WeeklyUsedPct = Math.Clamp(w.utilization ?? 0, 0, 100);
                snap.WeeklyResetAt = ParseTime(w.resets_at);
            }
            return snap;
        }
    }

    private static DateTimeOffset? ParseTime(string? iso) =>
        DateTimeOffset.TryParse(iso, out var t) ? t : null;

    public void Dispose() => _http.Dispose();
}

public sealed class ClaudeUsageResponse
{
    public ClaudeUsageWindow? five_hour { get; set; }
    public ClaudeUsageWindow? seven_day { get; set; }
}

public sealed class ClaudeUsageWindow
{
    public double? utilization { get; set; }
    public string? resets_at { get; set; }
}
