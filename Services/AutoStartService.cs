using System;
using System.Diagnostics;
using System.IO;

namespace BrainFuel.Services;

/// <summary>
/// Cross-platform "start on login" toggle.
///   Windows : HKCU\...\Run (via the built-in reg.exe — no NuGet dependency)
///   macOS   : ~/Library/LaunchAgents/&lt;id&gt;.plist
///   Linux   : ~/.config/autostart/&lt;id&gt;.desktop
/// The entry points at the currently-running exe (Environment.ProcessPath),
/// so toggle it from the published single-file exe for a stable autostart.
/// </summary>
public static class AutoStartService
{
    private const string AppName = "BrainFuel";
    private const string AppId = "com.turinglambdaai.brainfuel";
    private const string WinRunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows()) return WindowsQuery();
        if (OperatingSystem.IsMacOS()) return File.Exists(MacPlistPath);
        if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktopPath);
        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        if (OperatingSystem.IsWindows()) WindowsSet(enabled, exe);
        else if (OperatingSystem.IsMacOS()) WriteFile(MacPlistPath, enabled, () => MacPlist(exe));
        else if (OperatingSystem.IsLinux()) WriteFile(LinuxDesktopPath, enabled, () => LinuxDesktop(exe));
    }

    // ---- Windows: shell out to reg.exe (no NuGet dependency) ----
    private static bool WindowsQuery()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var p = Process.Start(NewReg("query", WinRunKey, "/v", AppName));
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void WindowsSet(bool enabled, string exe)
    {
        if (!OperatingSystem.IsWindows()) return;
        var psi = enabled
            ? NewReg("add", WinRunKey, "/v", AppName, "/t", "REG_SZ", "/d", $"\"{exe}\"", "/f")
            : NewReg("delete", WinRunKey, "/v", AppName, "/f");
        try { using var p = Process.Start(psi); p?.WaitForExit(3000); } catch { }
    }

    private static ProcessStartInfo NewReg(params string[] args)
    {
        var psi = new ProcessStartInfo("reg.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    // ---- macOS / Linux: drop a file ----
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string MacPlistPath => Path.Combine(Home, "Library", "LaunchAgents", AppId + ".plist");
    private static string LinuxDesktopPath => Path.Combine(Home, ".config", "autostart", AppId + ".desktop");

    private static void WriteFile(string path, bool enabled, Func<string> content)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content());
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    private static string MacPlist(string exe) => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0""><dict>
  <key>Label</key><string>{AppId}</string>
  <key>ProgramArguments</key><array><string>{exe}</string></array>
  <key>RunAtLoad</key><true/>
</dict></plist>";

    private static string LinuxDesktop(string exe) => $@"[Desktop Entry]
Type=Application
Name={AppName}
Exec={exe}
X-GNOME-Autostart-enabled=true
";
}
