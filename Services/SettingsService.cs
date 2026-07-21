using System;
using System.IO;
using System.Text.Json;

namespace BrainFuel.Services;

public enum DisplayStyle { Used, Remaining }

public enum AppTheme { System, Light, Dark }

public class AppSettings
{
    public string? ApiKey { get; set; }
    public string BaseDomain { get; set; } = "https://open.bigmodel.cn";
    public int RefreshIntervalMinutes { get; set; } = 5;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public DisplayStyle WeeklyDisplayStyle { get; set; } = DisplayStyle.Used;
    public DisplayStyle HourlyDisplayStyle { get; set; } = DisplayStyle.Remaining;
    public bool AutoStart { get; set; }
    public bool NotifyEnabled { get; set; } = true;
    public int NotifyThreshold { get; set; } = 80;
    public AppTheme ThemeMode { get; set; } = AppTheme.Dark;
    public double CardOpacity { get; set; } = 1.0;
    public AppLanguage Language { get; set; } = AppLanguage.Zh;

    public bool IsValid => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseDomain);
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string AppDirectory { get; } = BuildAppDirectory();
    private static string SettingsPath => Path.Combine(AppDirectory, "settings.json");
    public static string DebugPath => Path.Combine(AppDirectory, "quota-debug.json");

    private static string BuildAppDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, "BrainFuel");
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch { /* corrupt or unreadable — fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
