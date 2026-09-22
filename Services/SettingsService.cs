using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BrainFuel.Services;

public enum DisplayStyle { Used, Remaining }
public enum AppTheme { System, Light, Dark }
public enum ApiKeyStorageState { None, Protected, PlaintextFallback, ProtectedUnavailable }

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

    public static ApiKeyStorageState ApiKeyStorageState { get; private set; } = ApiKeyStorageState.None;
    public static bool ApiKeyIsProtected => ApiKeyStorageState == ApiKeyStorageState.Protected;
    public static string ApiKeyStorageName =>
        ApiKeyStorageState is ApiKeyStorageState.Protected or ApiKeyStorageState.ProtectedUnavailable
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

        if (existingInstall && !hadAlwaysOnTopSetting)
            settings.AlwaysOnTop = true;

        if (settings.ApiKeyConfigured == false)
        {
            settings.ApiKey = null;
            _lastPersistedApiKey = null;
            ApiKeyStorageState = ApiKeyStorageState.None;
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
            ApiKeyStorageState = ApiKeyStorageState.Protected;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ApiKey) &&
                 CredentialStore.TryWrite(AppDirectory, settings.ApiKey))
        {
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = settings.ApiKey;
            ApiKeyStorageState = ApiKeyStorageState.Protected;
            Save(settings);
        }
        else if (settings.ApiKeyConfigured == true && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            // The settings marker says a protected credential exists, but the OS
            // store could not be read right now (locked keychain, unavailable D-Bus,
            // transient desktop-session failure, etc.). Preserve that fact instead
            // of turning a temporary read failure into a destructive clear/downgrade.
            settings.ApiKey = null;
            _lastPersistedApiKey = null;
            ApiKeyStorageState = ApiKeyStorageState.ProtectedUnavailable;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = settings.ApiKey;
            ApiKeyStorageState = ApiKeyStorageState.PlaintextFallback;
        }
        else
        {
            settings.ApiKeyConfigured = false;
            _lastPersistedApiKey = null;
            ApiKeyStorageState = ApiKeyStorageState.None;
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);
        var key = settings.ApiKey?.Trim();
        settings.ApiKey = key;

        bool omitPlaintextKey;
        if (ApiKeyStorageState == ApiKeyStorageState.ProtectedUnavailable &&
            settings.ApiKeyConfigured == true &&
            string.IsNullOrWhiteSpace(key))
        {
            // An unrelated settings save while the system credential service is
            // unavailable must preserve the protected-credential marker and never
            // delete the OS-store item or write a plaintext replacement.
            settings.ApiKeyConfigured = true;
            omitPlaintextKey = true;
        }
        else if (string.IsNullOrWhiteSpace(key))
        {
            settings.ApiKeyConfigured = false;
            CredentialStore.TryDelete(AppDirectory);
            _lastPersistedApiKey = null;
            ApiKeyStorageState = ApiKeyStorageState.None;
            omitPlaintextKey = true;
        }
        else if (ApiKeyStorageState == ApiKeyStorageState.Protected &&
                 string.Equals(key, _lastPersistedApiKey, StringComparison.Ordinal))
        {
            // Unrelated settings changes do not touch the keychain. A transient
            // secure-store outage can therefore never downgrade a protected key.
            settings.ApiKeyConfigured = true;
            omitPlaintextKey = true;
        }
        else if (CredentialStore.TryWrite(AppDirectory, key))
        {
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = key;
            ApiKeyStorageState = ApiKeyStorageState.Protected;
            omitPlaintextKey = true;
        }
        else
        {
            // First-time save / actual key change with no usable secret service:
            // preserve the credential rather than silently losing it, disclose the
            // fallback in Settings, and restrict file permissions on Unix systems.
            settings.ApiKeyConfigured = true;
            _lastPersistedApiKey = key;
            ApiKeyStorageState = ApiKeyStorageState.PlaintextFallback;
            omitPlaintextKey = false;
        }

        var root = JsonSerializer.SerializeToNode(settings, JsonOpts)?.AsObject() ?? new JsonObject();
        if (omitPlaintextKey)
            root.Remove(nameof(AppSettings.ApiKey));

        WriteSettingsAtomically(root.ToJsonString(JsonOpts));
    }

    private static void WriteSettingsAtomically(string json)
    {
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, json);
        HardenFilePermissions(temp);
        File.Move(temp, SettingsPath, overwrite: true);
        HardenFilePermissions(SettingsPath);
    }

    private static void HardenFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Some filesystems do not expose Unix modes; settings remain usable.
        }
    }
}
