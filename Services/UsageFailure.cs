using System;
using System.Net;

namespace BrainFuel.Services;

public enum UsageFailureKind
{
    Authentication,
    Network,
    Tls,
    Proxy,
    Timeout,
    RateLimited,
    NoCodingPlan,
    ServiceUnavailable,
    InvalidResponse,
    Unknown,
}

public sealed class UsageRequestException : Exception
{
    public UsageFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }

    public UsageRequestException(
        UsageFailureKind kind,
        string message,
        Exception? innerException = null,
        HttpStatusCode? statusCode = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }
}

public static class UsageFailureText
{
    public static bool AllowsSaveAnyway(UsageFailureKind kind) => kind is not
        (UsageFailureKind.Authentication or UsageFailureKind.NoCodingPlan);

    public static string Card(UsageFailureKind kind)
    {
        var zh = Strings.Current == AppLanguage.Zh;
        return kind switch
        {
            UsageFailureKind.Authentication => zh ? "API Key 无效或平台不匹配" : "API key or platform doesn't match",
            UsageFailureKind.Network => zh ? "网络连接失败" : "Network connection failed",
            UsageFailureKind.Tls => zh ? "TLS / 证书连接失败" : "TLS / certificate connection failed",
            UsageFailureKind.Proxy => zh ? "代理连接失败" : "Proxy connection failed",
            UsageFailureKind.Timeout => zh ? "连接超时" : "Connection timed out",
            UsageFailureKind.RateLimited => zh ? "请求过于频繁，稍后重试" : "Rate limited — try again later",
            UsageFailureKind.NoCodingPlan => zh ? "账户未开通 Coding Plan" : "No Coding Plan on this account",
            UsageFailureKind.ServiceUnavailable => zh ? "GLM 服务暂时异常" : "GLM service temporarily unavailable",
            UsageFailureKind.InvalidResponse => zh ? "GLM 返回数据无法识别" : "GLM returned unrecognized data",
            _ => zh ? "额度刷新失败" : "Quota refresh failed",
        };
    }

    public static string Validation(UsageFailureKind kind)
    {
        var zh = Strings.Current == AppLanguage.Zh;
        return kind switch
        {
            UsageFailureKind.Authentication => zh
                ? "API Key 无效，或所选平台与 Key 不匹配。请检查 Key，并确认“智谱国内 / Z.ai 国际”选择正确。"
                : "The API key is invalid or does not match the selected platform. Check the key and the Zhipu China / Z.ai international selection.",
            UsageFailureKind.Network => zh
                ? "无法连接 GLM 服务。请检查网络后重试；也可以先保存，联网后 BrainFuel 会自动刷新。"
                : "BrainFuel cannot reach the GLM service. Check the network and retry, or save now and let BrainFuel refresh when connectivity returns.",
            UsageFailureKind.Tls => zh
                ? "TLS / 证书握手失败。请检查系统时间、系统证书，或公司网络是否拦截 HTTPS。"
                : "TLS / certificate negotiation failed. Check the system clock, certificate store, or whether a corporate network is intercepting HTTPS.",
            UsageFailureKind.Proxy => zh
                ? "代理连接失败。请检查 Windows / 系统代理、公司代理或 VPN 设置。"
                : "Proxy connection failed. Check the system, corporate proxy, or VPN configuration.",
            UsageFailureKind.Timeout => zh
                ? "连接 GLM 服务超时。请检查网络、代理或稍后重试。"
                : "The GLM request timed out. Check the network or proxy and try again later.",
            UsageFailureKind.RateLimited => zh
                ? "GLM 暂时限流了这个请求。请稍后重试；当前设置可以先保存。"
                : "GLM rate-limited the request. Try again later; you can save the current settings now.",
            UsageFailureKind.NoCodingPlan => zh
                ? "这个账户没有检测到可用的 Coding Plan 额度。请确认该 Key 属于已开通 GLM Coding Plan 的账户。"
                : "No usable Coding Plan quota was found for this account. Confirm that the key belongs to an account with GLM Coding Plan enabled.",
            UsageFailureKind.ServiceUnavailable => zh
                ? "GLM 服务当前暂时异常。请稍后重试；当前设置可以先保存。"
                : "The GLM service is temporarily unavailable. Try again later; you can save the current settings now.",
            UsageFailureKind.InvalidResponse => zh
                ? "GLM 返回了 BrainFuel 无法识别的数据，可能是接口临时变化。可以先保存设置并稍后重试。"
                : "GLM returned data BrainFuel does not recognize, possibly due to a temporary API change. You can save now and retry later.",
            _ => zh
                ? "额度验证失败。可以先保存设置并稍后重试。"
                : "Quota validation failed. You can save the settings and try again later.",
        };
    }
}
