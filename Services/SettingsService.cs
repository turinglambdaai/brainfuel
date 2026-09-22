using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BrainFuel.Services;

public enum DisplayStyle { Used, Remaining }
public enum AppTheme { System, Light, Dark }

public class AppSettings
{
    public string? ApiKey { get; set; }
    public string BaseDomain { get; set; } = "https://open.bigmodel.cn";
    public int RefreshIntervalMinutes { get; set; } = 5;

    // Legacy absolute coordinates are retained for backward-compatible migration.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // v0.5+: monitor-aware placement. Screen bounds identify the last display;
    // relative values preserve the position when DPI/resolution/work-area changes.
    public int? WindowScreenX { get; set; }
    public int? WindowScreenY { get; set; }
    public int? WindowScreenWidth { get; set; }
    public int? WindowScreenHeight { get; set; }
    public double? WindowRelativeX { get; set; }
    public double? WindowRelativeY { get; set; }

    // New installs default to a non-intrusive normal window. During migration,
    // SettingsService preserves the old always-on-top behavior for existing users.
    public bool AlwaysOnTop { get; set; }

    public DisplayStyle WeeklyDisplayStyle { get; set; } = DisplayStyle.Used;
    public DisplayStyle HourlyDisplayStyle { get; set; } = DisplayStyle.Remaining;
    public bool AutoStart { get; set; }
    public bool NotifyEnabled { get; set; } = true;
    public int NotifyThreshold { get; set; } = 80;
    public AppTheme ThemeMode { get; set; } = AppTheme.Dark;
    public double CardOpacity { get; set; } = 1.0;
    public AppLanguage Language { get; set; } = AppLanguage.Zh;

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseDomain);
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string AppDirectory { get; } = BuildAppDirectory();
    private static string SettingsPath => Path.Combine(AppDirectory, "settings.json");
    public static string DebugPath => Path.Combine(AppDirectory, "quota-debug.json");

    public static bool ApiKeyIsProtected { get; private set; }
    public static string ApiKeyStorageName => ApiKeyIsProtected
        ? CredentialStore.BackendDisplayName
        : "settings.json";

    private static string BuildAppDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(appData, "BrainFuel");
    }

    public static AppSettings Load()
    {
        AppSettings settings = new();
        string? json = null;
        bool existingInstall = false;
        bool hadAlwaysOnTopSetting = false;

        try
        {
            if (File.Exists(SettingsPath))
            {
                existingInstall = true;
                json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);
                hadAlwaysOnTopSetting = doc.RootElement.TryGetProperty(nameof(AppSettings.AlwaysOnTop), out _);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt/unreadable preferences must never prevent the widget starting.
            settings = new AppSettings();
        }

        // v0.4 and earlier were unconditionally Topmost. Preserve that behavior
        // for existing users while making new installations non-intrusive by default.
        if (existingInstall && !hadAlwaysOnTopSetting)
            settings.AlwaysOnTop = true;

        // OS secret storage takes precedence. If only a legacy plaintext key is
        // present, migrate it and immediately rewrite settings.json without it.
        if (CredentialStore.TryRead(AppDirectory, out var protectedKey) &&
            !string.IsNullOrWhiteSpace(protectedKey))
        {
            settings.ApiKey = protectedKey;
            ApiKeyIsProtected = true;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ApiKey) &&
                 CredentialStore.TryWrite(AppDirectory, settings.ApiKey))
        {
            ApiKeyIsProtected = true;
            Save(settings);
        }
        else
        {
            ApiKeyIsProtected = false;
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);

        bool protectedStored;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            protectedStored = CredentialStore.TryDelete(AppDirectory);
        }
        else
        {
            protectedStored = CredentialStore.TryWrite(AppDirectory, settings.ApiKey);
        }

        ApiKeyIsProtected = protectedStored && !string.IsNullOrWhiteSpace(settings.ApiKey);

        // Serialize normally for backward compatibility, then remove the API key
        // whenever an OS-protected copy exists. If the platform secret service is
        // unavailable, retain the old behavior rather than silently losing the key.
        var root = JsonSerializer.SerializeToNode(settings, JsonOpts)?.AsObject() ?? new JsonObject();
        if (protectedStored)
            root.Remove(nameof(AppSettings.ApiKey));

        File.WriteAllText(SettingsPath, root.ToJsonString(JsonOpts));
        HardenSettingsPermissions();
    }

    private static void HardenSettingsPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                SettingsPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Some filesystems do not expose Unix modes; settings remain usable.
        }
    }
}
