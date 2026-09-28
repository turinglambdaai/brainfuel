using System;
using System.IO;
using System.Text.Json;

namespace BrainFuel.Services;

/// <summary>
/// Reads OAuth access tokens from the local Claude Code / Codex CLI installs.
/// Tokens are read fresh on every refresh so CLI-side renewals are picked up
/// automatically; nothing is copied into BrainFuel's own storage.
/// </summary>
public static class LocalCliCredentials
{
    public static string CodexAuthPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    public static string ClaudeCredentialsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static bool Exists(string? providerId) => providerId switch
    {
        "codex" => File.Exists(CodexAuthPath),
        "claude" => File.Exists(ClaudeCredentialsPath),
        _ => true, // glm-style providers rely on pasted keys, not a CLI login
    };

    public static string? ReadToken(string? providerId) => providerId switch
    {
        "codex" => ReadCodexToken(),
        "claude" => ReadClaudeToken(),
        _ => null,
    };

    private static string? ReadCodexToken()
    {
        try
        {
            if (!File.Exists(CodexAuthPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CodexAuthPath));
            return doc.RootElement.TryGetProperty("tokens", out var t) &&
                   t.TryGetProperty("access_token", out var a) &&
                   a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"codex credentials unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>ChatGPT account id the Codex CLI sends as chatgpt-account-id.</summary>
    public static bool TryReadAccountId(out string? accountId)
    {
        accountId = null;
        try
        {
            if (!File.Exists(CodexAuthPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(CodexAuthPath));
            if (doc.RootElement.TryGetProperty("tokens", out var t) &&
                t.TryGetProperty("account_id", out var a) &&
                a.ValueKind == JsonValueKind.String)
            {
                accountId = a.GetString();
                return !string.IsNullOrWhiteSpace(accountId);
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadClaudeToken()
    {
        try
        {
            if (!File.Exists(ClaudeCredentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ClaudeCredentialsPath));
            return doc.RootElement.TryGetProperty("claudeAiOauth", out var o) &&
                   o.TryGetProperty("accessToken", out var a) &&
                   a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
        }
        catch (Exception ex)
        {
            AppLog.Error($"claude credentials unreadable: {ex.Message}");
            return null;
        }
    }
}
