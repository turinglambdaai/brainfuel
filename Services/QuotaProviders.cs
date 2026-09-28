using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BrainFuel.Services;

/// <summary>
/// A per-account connection to one quota source. Implemented per product
/// (GLM Coding Plan today, other providers as they are added); accounts
/// reference a provider by id and the factory wires them up.
/// </summary>
public interface IQuotaClient : IDisposable
{
    Task<UsageSnapshot> GetUsageAsync(CancellationToken ct = default);
}

public static class QuotaProviders
{
    public const string DefaultProvider = "glm";

    /// <summary>Provider ids the UI may offer; unknown ids fall back to GLM.</summary>
    public static readonly IReadOnlyList<string> Known = new[] { "glm", "codex", "claude" };

    public static IQuotaClient Create(string? providerId, string baseDomain, string apiKey, string debugPath) =>
        providerId switch
        {
            "codex" => new CodexQuotaClient(),
            "claude" => new ClaudeQuotaClient(),
            _ => new GlmUsageClient(baseDomain, apiKey, debugPath),
        };
}
