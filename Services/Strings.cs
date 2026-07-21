using System.Collections.Generic;
using Avalonia;

namespace BrainFuel.Services;

public enum AppLanguage { Zh, En }

/// <summary>
/// Single source of truth for UI strings. Static labels are exposed to XAML via
/// DynamicResource (ApplyLanguage writes them into Application.Resources); dynamic
/// strings are produced by Get() in the current language.
/// </summary>
public static class Strings
{
    public static AppLanguage Current { get; private set; } = AppLanguage.Zh;

    private static readonly Dictionary<AppLanguage, Dictionary<string, string>> Tables = new()
    {
        [AppLanguage.Zh] = new()
        {
            // card
            ["LblHourly"] = "5 小时",
            ["LblWeekly"] = "周用量",
            // context menu
            ["MenuRefresh"] = "立即刷新",
            ["MenuSettings"] = "设置…",
            ["MenuQuit"] = "退出",
            // settings window
            ["WinTitle"] = "BrainFuel 设置",
            ["SettingsTitle"] = "GLM 套餐额度监控",
            ["SettingsDesc"] = "填入你的 GLM Coding Plan Key 与平台，控件会用它查询额度。",
            ["LblLanguage"] = "语言",
            ["LblTheme"] = "主题",
            ["ThemeSystem"] = "跟随系统",
            ["ThemeLight"] = "亮色",
            ["ThemeDark"] = "暗色",
            ["LblOpacity"] = "卡片透明度",
            ["LblApiKey"] = "API Key",
            ["KeyPlaceholder"] = "粘贴 GLM Coding Plan Key",
            ["LblPlatform"] = "平台",
            ["PlatformCn"] = "智谱国内 · open.bigmodel.cn",
            ["PlatformIntl"] = "Z.ai 国际 · api.z.ai",
            ["LblInterval"] = "刷新间隔（分钟）",
            ["ChkWeeklyRemaining"] = "周用量显示「剩余」而非「已用」",
            ["ChkHourlyRemaining"] = "5 小时显示「剩余」而非「已用」",
            ["ChkAutostart"] = "开机自动启动",
            ["ChkNotify"] = "额度接近耗尽时桌面通知",
            ["LblThreshold"] = "触发阈值（已用 %）",
            ["BtnCancel"] = "取消",
            ["BtnSave"] = "保存",
            // dynamic (VM)
            ["JustNow"] = "刚刚",
            ["MinutesAgo"] = "{0} 分钟前",
            ["HoursAgo"] = "{0} 小时前",
            ["DaysAgo"] = "{0} 天前",
            ["Refreshing"] = "刷新中…",
            ["RefreshFailed"] = "刷新失败",
            ["RefreshFailedAt"] = "刷新失败 · 上次 {0}",
            ["MinutesLater"] = "{0} 分钟后",
            ["HoursLater"] = "{0} 小时后",
            ["DaysLater"] = "{0} 天后",
            ["ResettingSoon"] = "即将重置",
            ["None"] = "—",
            ["NotifyHourlyTitle"] = "5 小时额度即将耗尽",
            ["NotifyWeeklyTitle"] = "周额度即将耗尽",
            ["NotifyUsed"] = "已用 {0}%",
        },
        [AppLanguage.En] = new()
        {
            ["LblHourly"] = "5h",
            ["LblWeekly"] = "Weekly",
            ["MenuRefresh"] = "Refresh now",
            ["MenuSettings"] = "Settings…",
            ["MenuQuit"] = "Quit",
            ["WinTitle"] = "BrainFuel Settings",
            ["SettingsTitle"] = "GLM Coding Plan Monitor",
            ["SettingsDesc"] = "Enter your GLM Coding Plan key and platform. The widget uses it to read quota.",
            ["LblLanguage"] = "Language",
            ["LblTheme"] = "Theme",
            ["ThemeSystem"] = "System",
            ["ThemeLight"] = "Light",
            ["ThemeDark"] = "Dark",
            ["LblOpacity"] = "Card opacity",
            ["LblApiKey"] = "API Key",
            ["KeyPlaceholder"] = "Paste your GLM Coding Plan key",
            ["LblPlatform"] = "Platform",
            ["PlatformCn"] = "Zhipu (China) · open.bigmodel.cn",
            ["PlatformIntl"] = "Z.ai (intl) · api.z.ai",
            ["LblInterval"] = "Refresh interval (min)",
            ["ChkWeeklyRemaining"] = "Show weekly as 'remaining' instead of 'used'",
            ["ChkHourlyRemaining"] = "Show 5h as 'remaining' instead of 'used'",
            ["ChkAutostart"] = "Start on login",
            ["ChkNotify"] = "Notify when quota nearly exhausted",
            ["LblThreshold"] = "Threshold (used %)",
            ["BtnCancel"] = "Cancel",
            ["BtnSave"] = "Save",
            ["JustNow"] = "just now",
            ["MinutesAgo"] = "{0} min ago",
            ["HoursAgo"] = "{0} h ago",
            ["DaysAgo"] = "{0} d ago",
            ["Refreshing"] = "Refreshing…",
            ["RefreshFailed"] = "Refresh failed",
            ["RefreshFailedAt"] = "Failed · last {0}",
            ["MinutesLater"] = "in {0} min",
            ["HoursLater"] = "in {0} h",
            ["DaysLater"] = "in {0} d",
            ["ResettingSoon"] = "resetting soon",
            ["None"] = "—",
            ["NotifyHourlyTitle"] = "5-hour quota nearly exhausted",
            ["NotifyWeeklyTitle"] = "Weekly quota nearly exhausted",
            ["NotifyUsed"] = "{0}% used",
        },
    };

    public static string Get(string key, params object[] args)
    {
        var s = Tables[Current].TryGetValue(key, out var v) ? v : key;
        return args.Length == 0 ? s : string.Format(s, args);
    }

    /// <summary>Switches the language and re-publishes the strings into app resources.</summary>
    public static void ApplyLanguage(AppLanguage lang)
    {
        Current = lang;
        if (Application.Current is null) return;
        var res = Application.Current.Resources;
        foreach (var kv in Tables[lang])
            res[kv.Key] = kv.Value;
    }
}
