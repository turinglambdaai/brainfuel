using System.Text.Json;
using BrainFuel.Services;
using Xunit;

namespace BrainFuel.Tests;

/// <summary>
/// settings.json is user-editable. The deserializer must accept both string
/// enum values ("Dark", "Compact" — what the app writes since the converter
/// was added) and legacy numeric values (what older versions wrote), and must
/// never throw on a hand-edited file.
/// </summary>
public sealed class SettingsJsonTests
{
    [Fact]
    public void StringEnumValues_AreAccepted()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            """{"ThemeMode": "Dark", "Language": "En", "SizeMode": "Compact"}""", SettingsService.JsonOpts);
        Assert.NotNull(s);
        Assert.Equal(AppTheme.Dark, s.ThemeMode);
        Assert.Equal(AppLanguage.En, s.Language);
        Assert.Equal(CardSizeMode.Compact, s.SizeMode);
    }

    [Fact]
    public void LegacyNumericEnumValues_AreAccepted()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            """{"ThemeMode": 1, "Language": 0, "SizeMode": 1}""", SettingsService.JsonOpts);
        Assert.NotNull(s);
        Assert.Equal(AppTheme.Light, s.ThemeMode);
        Assert.Equal(CardSizeMode.Compact, s.SizeMode);
    }

    [Fact]
    public void UnknownEnumString_ThrowsAndIsCaughtByLoadFallback()
    {
        // A misspelled enum value is a parse error; SettingsService.Load catches
        // it, resets to defaults and logs the reason (no silent data loss).
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AppSettings>(
            """{"ThemeMode": "Midnight"}""", SettingsService.JsonOpts));
    }

    [Fact]
    public void RoundTrip_PreservesEnumValues()
    {
        var original = new AppSettings
        {
            ThemeMode = AppTheme.Light,
            SizeMode = CardSizeMode.Compact,
            Language = AppLanguage.En,
        };
        var json = JsonSerializer.Serialize(original, SettingsService.JsonOpts);
        Assert.Contains("\"Compact\"", json);
        var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOpts);
        Assert.Equal(original.ThemeMode, back?.ThemeMode);
        Assert.Equal(original.SizeMode, back?.SizeMode);
        Assert.Equal(original.Language, back?.Language);
    }
}
