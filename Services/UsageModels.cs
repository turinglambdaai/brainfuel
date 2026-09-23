using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrainFuel.Services;

/// <summary>
/// Normalized view of the GLM Coding Plan quota. Percentages are 0..100 (used portion).
/// </summary>
public class UsageSnapshot
{
    public bool HasHourly { get; set; }
    public bool HasWeekly { get; set; }
    public double HourlyUsedPct { get; set; }
    public double WeeklyUsedPct { get; set; }
    public DateTimeOffset? HourlyResetAt { get; set; }
    public DateTimeOffset? WeeklyResetAt { get; set; }
    public string? PlanLevel { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public string? RawJson { get; set; }
}

// ---- Raw response models -------------------------------------------------
// Confirmed against the live payload (quota-debug.json). The two TOKENS_LIMIT
// entries are distinguished by `number`: 5 = the 5-hour window, the other = weekly.

public class QuotaLimitResponse
{
    [JsonPropertyName("data")]
    public QuotaLimitData? Data { get; set; }

    // Error envelope: both gateways answer bad keys with HTTP 200 +
    // {"code":1001,"msg":"…","success":false} (observed 2026-09 against
    // open.bigmodel.cn and api.z.ai). Kept as raw JsonElement because `code`
    // appears as both number and string in the wild.
    [JsonPropertyName("code")]
    public JsonElement? Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("success")]
    public JsonElement? Success { get; set; }

    public string? ErrorMsg => Msg ?? Message;

    public string? ErrorCode => Code switch
    {
        { ValueKind: JsonValueKind.Number } n => n.ToString(),
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        _ => null,
    };

    public bool IsErrorEnvelope =>
        (Success is { ValueKind: JsonValueKind.True or JsonValueKind.False } s && !s.GetBoolean())
        || (ErrorCode is { } c && c != "0" && c != "200");
}

public class QuotaLimitData
{
    [JsonPropertyName("limits")]
    public List<RawLimit>? Limits { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }
}

public class RawLimit
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("unit")]
    public int? Unit { get; set; }

    [JsonPropertyName("number")]
    public int? Number { get; set; }

    [JsonPropertyName("percentage")]
    public double? Percentage { get; set; }

    [JsonPropertyName("currentValue")]
    public double? CurrentValue { get; set; }

    [JsonPropertyName("usage")]
    public double? Usage { get; set; }

    [JsonPropertyName("remaining")]
    public double? Remaining { get; set; }

    [JsonPropertyName("nextResetTime")]
    public long? NextResetTime { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
