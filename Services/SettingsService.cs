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

    // Nullable for migration: v0.4 and earlier do not have this marker. False is
    // an explicit tombstone so a failed keychain deletion cannot resurrect a
    // previously-cleared credential on the next launch.
    public bool? ApiKeyConfigured { get; set; }

    public string BaseDomain { get; set; } = "https://open.bigmodel.cn";
    public int RefreshIntervalMinutes { get; set; } = 5;

    // Legacy absolute coordinates are retained for backward-compatible migration.
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    // v0.5+: monitor-aware placement. Display name plus bounds identify the last
    // display; relative values preserve position when layout/DPI/resolution changes.
    public string? WindowScreenName { get; set; }
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
    private static string? _lastPersistedApiKey;

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
        bool existingInstall = false;
        bool hadAlwaysOnTopSetting = false;

        try
        {
            if (File.Exists(SettingsPath))
            {
                existingInstall = true;
                var json = File.ReadAllText(SettingsPath);
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

        // An explicit false is a tombstone. Ignore (and opportunistically remove)
        // any stale OS-store entry rather than resurrecting a key the user cleared.
        if (settings.ApiKeyConfigured == false)
        {
            settings.ApiKey = null;
            _lastPersistedApiKey = null;
            ApiKeyIsProtected = false;
            CredentialStore.TryDelete(AppDirectory);
            return settings;
        }

        // OS secret storage takes precedence. If only a legacy plaintext key is
        // present, migrate it and immediately rewrite settings.json without it.
        if (CredentialStore.TryRead(AppDirectory, out var protectedKey) &&
            !string.IsNullOrWhiteSpace(protectedKey))
        {
            settings.ApiKey = protectedKey;
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = protectedKey;
            ApiKeyIsProtected = true;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ApiKey) &&
                 CredentialStore.TryWrite(AppDirectory, settings.ApiKey))
        {
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = settings.ApiKey;
            ApiKeyIsProtected = true;
            Save(settings);
        }
        else
        {
            settings.ApiKeyConfigured = !string.IsNullOrWhiteSpace(settings.ApiKey);
            _lastPersistedApiKey = settings.ApiKey;
            ApiKeyIsProtected = false;
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        var key = settings.ApiKey?.Trim();
        settings.ApiKey = key;

        bool protectedStored;
        if (string.IsNullOrWhiteSpace(key))
        {
            settings.ApiKeyConfigured = false;
            // The tombstone above is authoritative even if OS-store deletion is
            // temporarily unavailable; Load() will never resurrect the stale key.
            CredentialStore.TryDelete(AppDirectory);
            _lastPersistedApiKey = null;
            ApiKeyIsProtected = false;
            protectedStored = true; // no plaintext secret needs persistence
        }
        else if (ApiKeyIsProtected && string.Equals(key, _lastPersistedApiKey, StringComparison.Ordinal))
        {
            // Critical: an unrelated settings change must not touch the keychain.
            // This prevents a transient keyring outage from downgrading a protected
            // credential back into plaintext settings.json.
            settings.ApiKeyConfigured = true;
            protectedStored = true;
        }
        else if (CredentialStore.TryWrite(AppDirectory, key))
        {
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = key;
            ApiKeyIsProtected = true;
            protectedStored = true;
        }
        else
        {
            // First-time save / actual key change with no usable secret service:
            // preserve the user's credential rather than silently losing it, but
            // disclose this fallback in Settings and restrict file permissions.
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = key;
            ApiKeyIsProtected = false;
            protectedStored = false;
        }

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
