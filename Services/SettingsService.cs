using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BrainFuel.Services;

public enum DisplayStyle { Used, Remaining }
public enum AppTheme { System, Light, Dark }
public enum ApiKeyStorageState { None, Protected, PlaintextFallback, ProtectedUnavailable }
public enum CardSizeMode { Standard, Compact }

/// <summary>One monitored GLM account: display metadata only — the key never
/// lives here (it is resolved from the keyring by <see cref="Id"/>).</summary>
public class AccountConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseDomain { get; set; } = "https://open.bigmodel.cn";

    // Which product's quota this account monitors ("glm" today; the provider
    // abstraction exists so other products can be added without migrations).
    public string Provider { get; set; } = QuotaProviders.DefaultProvider;

    // Tombstone semantics like the legacy ApiKeyConfigured: false is explicit,
    // so a failed keychain deletion cannot resurrect a cleared credential.
    public bool Configured { get; set; }

    // ComboBox display text.
    public override string ToString() =>
        Name.Length > 0 ? Name
            : BaseDomain.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ? "Z.ai"
            : "bigmodel.cn";
}

public class AppSettings
{
    public List<AccountConfig> Accounts { get; set; } = new();
    public string? ActiveAccountId { get; set; }

    // ---- legacy single-account fields (v0.5.x) -----------------------------
    // Read during migration, no longer written by Save (Save strips them).
    public string? ApiKey { get; set; }
    public bool? ApiKeyConfigured { get; set; }
    public string BaseDomain { get; set; } = "https://open.bigmodel.cn";
    // -------------------------------------------------------------------------

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
    public CardSizeMode SizeMode { get; set; } = CardSizeMode.Standard;
    public bool HotkeyEnabled { get; set; }
    public string HotkeyCombo { get; set; } = "Ctrl+Alt+B";

    [JsonIgnore]
    public AccountConfig? ActiveAccount =>
        Accounts.FirstOrDefault(a => a.Id == ActiveAccountId) ?? Accounts.FirstOrDefault();

    [JsonIgnore]
    public bool HasConfiguredCredential => Accounts.Any(a => a.Configured);

    [JsonIgnore]
    public bool IsValid => ActiveAccount is { Configured: true };
}

public static class SettingsService
{
    // Enum values are written as strings ("Dark", "Compact") and *both* strings
    // and legacy numeric values are accepted on read. Without the converter a
    // hand-edited settings.json containing "ThemeMode": "Dark" fails to parse
    // and the whole file silently resets to defaults.
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public const string LegacyAccountId = "default";

    // Resolved keys by account id. Populated by Load, updated by Save; the
    // settings file itself never contains them (except the explicit plaintext
    // fallback below).
    private static readonly Dictionary<string, string> Keyring = new();

    /// <summary>True when at least one account's key is only in settings.json
    /// because no system secret store was available.</summary>
    private static bool _plaintextFallback;

    public static string AppDirectory { get; } = BuildAppDirectory();
    private static string SettingsPath => Path.Combine(AppDirectory, "settings.json");
    public static string DebugPath => Path.Combine(AppDirectory, "quota-debug.json");

    public static ApiKeyStorageState ApiKeyStorageState { get; private set; } = ApiKeyStorageState.None;
    public static bool ApiKeyIsProtected => ApiKeyStorageState == ApiKeyStorageState.Protected;
    public static string ApiKeyStorageName =>
        ApiKeyStorageState is ApiKeyStorageState.Protected or ApiKeyStorageState.ProtectedUnavailable
            ? CredentialStore.BackendDisplayName
            : "settings.json";

    /// <summary>Returns the stored key for an account, or null.</summary>
    public static string? GetKey(string accountId) =>
        Keyring.TryGetValue(accountId, out var key) && !string.IsNullOrWhiteSpace(key) ? key : null;

    public static void SetKey(string accountId, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) Keyring.Remove(accountId);
        else Keyring[accountId] = key.Trim();
    }

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
        catch (Exception ex)
        {
            // Corrupt or unmappable settings: fall back to defaults, but leave a
            // trace — otherwise every preference (position, theme, threshold)
            // silently disappears and looks like a bug elsewhere.
            AppLog.Error($"settings.json load failed, using defaults: {ex.Message}");
            settings = new AppSettings();
        }

        if (existingInstall && !hadAlwaysOnTopSetting)
            settings.AlwaysOnTop = true;

        MigrateLegacyAccount(settings);
        NormalizeAccounts(settings);
        LoadKeys(settings);
        return settings;
    }

    /// <summary>v0.5.x files carry one implicit account in ApiKey/BaseDomain.
    /// Any existing file becomes the "default" account; a fresh install gets
    /// its empty account from NormalizeAccounts instead.</summary>
    private static void MigrateLegacyAccount(AppSettings settings)
    {
        if (settings.Accounts.Count > 0 || !File.Exists(SettingsPath)) return;

        bool hadCredential = settings.ApiKeyConfigured != false &&
                             (!string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKeyConfigured == true);

        settings.Accounts.Add(new AccountConfig
        {
            Id = LegacyAccountId,
            Name = "",
            BaseDomain = string.IsNullOrWhiteSpace(settings.BaseDomain)
                ? "https://open.bigmodel.cn"
                : settings.BaseDomain,
            Configured = hadCredential,
        });
    }

    private static void NormalizeAccounts(AppSettings settings)
    {
        if (settings.Accounts.Count == 0)
        {
            // Fresh install: one unnamed account gives the UI a landing spot,
            // mirroring the old single-account first-run flow.
            settings.Accounts.Add(new AccountConfig { Id = LegacyAccountId, Name = "" });
        }

        foreach (var account in settings.Accounts.Where(a => string.IsNullOrWhiteSpace(a.Id)).ToList())
            account.Id = LegacyAccountId; // defensive: never persist an empty id

        if (settings.Accounts.All(a => a.Id != settings.ActiveAccountId))
            settings.ActiveAccountId = settings.Accounts[0].Id;
    }

    private static void LoadKeys(AppSettings settings)
    {
        Keyring.Clear();
        _plaintextFallback = false;

        // Plaintext fallback from a previous run (no system secret store).
        // Handled by the caller of Save writing an "AccountKeys" node; read
        // through the raw document because AppSettings has no such property.
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("AccountKeys", out var node) &&
                    node.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in node.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()))
                            Keyring[p.Name] = p.Value.GetString()!;
                    if (Keyring.Count > 0) _plaintextFallback = true;
                }
            }
        }
        catch { /* handled by the outer Load try/catch shape */ }

        if (CredentialStore.TryReadAll(AppDirectory, out var protectedKeys) && protectedKeys.Count > 0)
        {
            foreach (var (id, key) in protectedKeys)
                Keyring[id] = key;

            // One-time upgrade from the legacy single-key blob.
            if (!protectedKeys.ContainsKey(LegacyAccountId) &&
                CredentialStore.TryRead(AppDirectory, out var legacyKey) &&
                !string.IsNullOrWhiteSpace(legacyKey))
            {
                Keyring[LegacyAccountId] = legacyKey;
            }

            _plaintextFallback = false;
            ApiKeyStorageState = ApiKeyStorageState.Protected;
            PersistKeys(settings); // rewrite the upgraded/complete dict
        }
        else if (Keyring.Count > 0)
        {
            ApiKeyStorageState = ApiKeyStorageState.PlaintextFallback;
        }
        else if (settings.Accounts.Any(a => a.Configured))
        {
            // A protected credential is known to exist, but the OS store cannot
            // be read right now. Preserve markers and retry during refreshes.
            ApiKeyStorageState = ApiKeyStorageState.ProtectedUnavailable;
        }
        else
        {
            ApiKeyStorageState = ApiKeyStorageState.None;
        }

        // Mark accounts configured when a key for them actually resolved.
        foreach (var account in settings.Accounts)
            if (Keyring.ContainsKey(account.Id) && Keyring[account.Id].Length > 0)
                account.Configured = true;
    }

    /// <summary>
    /// Re-reads credentials that were temporarily unavailable (locked Keychain,
    /// unavailable Secret Service session, etc.). Lets periodic refresh recover
    /// without a restart.
    /// </summary>
    public static bool TryRefreshProtectedKeys(AppSettings settings)
    {
        if (ApiKeyStorageState == ApiKeyStorageState.Protected) return true;
        if (ApiKeyStorageState != ApiKeyStorageState.ProtectedUnavailable) return false;

        if (CredentialStore.TryReadAll(AppDirectory, out var keys) && keys.Count > 0)
        {
            foreach (var (id, key) in keys)
                Keyring[id] = key;
            ApiKeyStorageState = ApiKeyStorageState.Protected;
            foreach (var account in settings.Accounts)
                if (Keyring.ContainsKey(account.Id) && Keyring[account.Id].Length > 0)
                    account.Configured = true;
            return true;
        }

        return false;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppDirectory);

        // Mirror the active account's domain into the legacy field so older
        // builds (or external readers) still see something sane.
        settings.BaseDomain = settings.ActiveAccount?.BaseDomain ?? settings.BaseDomain;

        PersistKeys(settings);

        var root = JsonSerializer.SerializeToNode(settings, JsonOpts)?.AsObject() ?? new JsonObject();
        root.Remove(nameof(AppSettings.ApiKey)); // never write legacy key fields
        root.Remove(nameof(AppSettings.ApiKeyConfigured));

        if (_plaintextFallback)
        {
            // No system secret store: keys ride in settings.json explicitly.
            var node = new JsonObject();
            foreach (var (id, key) in Keyring)
                node[id] = key;
            root["AccountKeys"] = node;
        }
        else
        {
            root.Remove("AccountKeys");
        }

        WriteSettingsAtomically(root.ToJsonString(JsonOpts));
    }

    private static void PersistKeys(AppSettings settings)
    {
        var configured = settings.Accounts.Where(a => a.Configured).Select(a => a.Id).ToHashSet();
        foreach (var id in Keyring.Keys.Where(id => !configured.Contains(id)).ToList())
            Keyring.Remove(id); // account deleted or de-configured

        if (Keyring.Count == 0 && ApiKeyStorageState != ApiKeyStorageState.ProtectedUnavailable)
        {
            CredentialStore.TryDelete(AppDirectory); // the single blob holds all keys
            ApiKeyStorageState = ApiKeyStorageState.None;
            _plaintextFallback = false;
            return;
        }

        if (ApiKeyStorageState == ApiKeyStorageState.ProtectedUnavailable)
            return; // keep markers; keys stay untouched until the store returns

        if (CredentialStore.TryWriteAll(AppDirectory, Keyring))
        {
            ApiKeyStorageState = ApiKeyStorageState.Protected;
            _plaintextFallback = false;
        }
        else
        {
            ApiKeyStorageState = ApiKeyStorageState.PlaintextFallback;
            _plaintextFallback = true;
        }
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
