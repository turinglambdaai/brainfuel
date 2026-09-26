using System.Net;
using System.Security.Authentication;
using System.Text;
using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

/// <summary>
/// Regression tests for quota-fetch failure classification. The core lesson
/// from v0.5.5: both GLM gateways reject bad keys with HTTP 200 + an error
/// envelope, so the body — not the status code — decides the failure kind.
/// </summary>
public sealed class GlmUsageClientTests
{
    private static string DebugPath => Path.Combine(Path.GetTempPath(), "bf-tests-quota-debug.json");

    private static GlmUsageClient Client(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        out StubHandler? handler,
        string key = "test-key")
    {
        handler = new StubHandler(respond);
        return new GlmUsageClient("https://open.bigmodel.cn", key, DebugPath, handler);
    }

    private static HttpResponseMessage Json(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static UsageFailureKind KindOf(Action? act, out UsageRequestException thrown)
    {
        thrown = Assert.Throws<UsageRequestException>(() => act?.Invoke());
        return thrown.Kind;
    }

    // ---- HTTP 200 error envelopes (the shape both gateways really use) ----

    [Fact]
    public void Envelope_ZaiRejectedToken_IsAuthentication()
    {
        using var client = Client(_ => Json(200,
            "{\"code\":401,\"msg\":\"token expired or incorrect\",\"success\":false}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out var ex);
        Assert.Equal(UsageFailureKind.Authentication, kind);
        Assert.Contains("token expired or incorrect", ex.Message);
    }

    [Fact]
    public void Envelope_BigmodelMissingAuthHeader_IsAuthentication()
    {
        using var client = Client(_ => Json(200,
            "{\"code\":1001,\"msg\":\"Header中未收到Authorization参数，无法进行身份验证。\",\"success\":false}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.Authentication, kind);
    }

    [Fact]
    public void Envelope_MissingPlan_IsNoCodingPlan()
    {
        using var client = Client(_ => Json(200,
            "{\"code\":404,\"msg\":\"coding plan not subscribed\",\"success\":false}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.NoCodingPlan, kind);
    }

    [Fact]
    public void Envelope_RateLimitedCode_IsRateLimited()
    {
        using var client = Client(_ => Json(200,
            "{\"code\":429,\"msg\":\"too many requests\",\"success\":false}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.RateLimited, kind);
    }

    [Fact]
    public void Envelope_UnknownCode_IsInvalidResponse()
    {
        using var client = Client(_ => Json(200,
            "{\"code\":4217,\"msg\":\"something novel\",\"success\":false}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out var ex);
        Assert.Equal(UsageFailureKind.InvalidResponse, kind);
        Assert.Contains("something novel", ex.Message);
    }

    [Fact]
    public void Envelope_StringCodeField_IsRecognized()
    {
        using var client = Client(_ => Json(200,
            "{\"code\":\"401\",\"msg\":\"token expired or incorrect\",\"success\":false}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.Authentication, kind);
    }

    [Theory]
    [InlineData("{\"code\":200,\"msg\":\"ok\",\"success\":true,\"data\":{\"limits\":[]}}")]   // explicit success
    [InlineData("{\"code\":0,\"data\":{\"limits\":[]}}")]                                     // code 0 tolerated
    public void Envelope_SuccessBodies_DoNotThrow(string body)
    {
        // No TOKENS_LIMIT in either payload → still throws, but as NoCodingPlan,
        // proving the envelope logic did not misfire on healthy responses.
        using var client = Client(_ => Json(200, body), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.NoCodingPlan, kind);
    }

    // ---- HTTP status failures ----

    [Theory]
    [InlineData(401, UsageFailureKind.Authentication)]
    [InlineData(403, UsageFailureKind.Authentication)]
    [InlineData(402, UsageFailureKind.NoCodingPlan)]
    [InlineData(429, UsageFailureKind.RateLimited)]
    [InlineData(500, UsageFailureKind.ServiceUnavailable)]
    [InlineData(503, UsageFailureKind.ServiceUnavailable)]
    [InlineData(407, UsageFailureKind.Proxy)]
    public void HttpStatus_IsClassified(int status, UsageFailureKind expected)
    {
        using var client = Client(_ => Json(status, "{}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(expected, kind);
    }

    // ---- transport failures ----

    [Fact]
    public void Transport_Timeout_IsTimeout()
    {
        using var client = Client(_ => throw new TaskCanceledException("timed out"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.Timeout, kind);
    }

    [Fact]
    public void Transport_ConnectionRefused_IsNetwork()
    {
        using var client = Client(_ => throw new HttpRequestException("Connection refused"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.Network, kind);
    }

    [Fact]
    public void Transport_CertificateError_IsTls()
    {
        using var client = Client(_ => throw new HttpRequestException(
            "The SSL connection could not be established",
            new AuthenticationException("cert")), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.Tls, kind);
    }

    [Fact]
    public void Transport_ProxyTunnel_IsProxy()
    {
        using var client = Client(_ => throw new HttpRequestException("proxy tunnel failed"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.Proxy, kind);
    }

    // ---- success mapping ----

    private const string Success01Body = """
        {"data":{"level":"Max","limits":[
            {"type":"TOKENS_LIMIT","number":5,"percentage":0.35,"nextResetTime":1780000000000},
            {"type":"TOKENS_LIMIT","number":1,"percentage":0.80,"nextResetTime":1790000000000},
            {"type":"TIME_LIMIT","number":1,"percentage":0.50}
        ]}}
        """;

    private const string Success100Body = """
        {"data":{"level":"Max","limits":[
            {"type":"TOKENS_LIMIT","number":5,"percentage":35,"nextResetTime":1780000000000},
            {"type":"TOKENS_LIMIT","number":1,"percentage":80,"nextResetTime":1790000000000}
        ]}}
        """;

    [Theory]
    [InlineData(Success01Body)]
    [InlineData(Success100Body)]
    public void Success_MapsBothWindows_AndScalesUnitRange(string body)
    {
        using var client = Client(_ => Json(200, body), out _);
        var snap = client.GetUsageAsync().GetAwaiter().GetResult();
        Assert.True(snap.HasHourly);
        Assert.True(snap.HasWeekly);
        Assert.Equal(35, snap.HourlyUsedPct, 2);
        Assert.Equal(80, snap.WeeklyUsedPct, 2);
        Assert.Equal("Max", snap.PlanLevel);
        Assert.NotNull(snap.HourlyResetAt);
        Assert.NotNull(snap.WeeklyResetAt);
    }

    [Fact]
    public void Success_NoTokenLimits_IsNoCodingPlan()
    {
        using var client = Client(_ => Json(200,
            "{\"data\":{\"limits\":[{\"type\":\"TIME_LIMIT\",\"number\":1,\"percentage\":0.5}]}}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.NoCodingPlan, kind);
    }

    [Fact]
    public void Success_MissingDataObject_IsInvalidResponse()
    {
        using var client = Client(_ => Json(200, "{\"unrelated\":true}"), out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.InvalidResponse, kind);
    }

    [Fact]
    public void MalformedJson_IsInvalidResponse()
    {
        using var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>gateway login page</html>", Encoding.UTF8, "text/html"),
        }, out _);
        var kind = KindOf(() => client.GetUsageAsync().GetAwaiter().GetResult(), out _);
        Assert.Equal(UsageFailureKind.InvalidResponse, kind);
    }

    // ---- key normalization ----

    [Fact]
    public async Task Key_BearerPrefix_IsStripped()
    {
        using var client = Client(_ => Json(200, Success100Body), out var handler, "  Bearer abc-def-123  ");
        await client.GetUsageAsync();
        Assert.Equal("abc-def-123", handler.LastRequest?.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Key_PlainKey_IsSentRaw()
    {
        using var client = Client(_ => Json(200, Success100Body), out var handler, "abc-def-123");
        await client.GetUsageAsync();
        Assert.Equal("abc-def-123", handler.LastRequest?.Headers.GetValues("Authorization").Single());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
