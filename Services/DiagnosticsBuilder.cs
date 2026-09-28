using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BrainFuel.Services;

/// <summary>
/// Assembles a sanitized support bundle for the in-app "copy diagnostics"
/// action: versions, storage state, account metadata (names/domains only) and
/// the tail of the failure log. By construction it never contains the API key
/// or raw quota payloads — the log is errors-only and keys never enter
/// settings output.
/// </summary>
public static class DiagnosticsBuilder
{
    public static string Build(AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BrainFuel {typeof(DiagnosticsBuilder).Assembly.GetName().Version?.ToString(3)}");
        sb.AppendLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription.TrimEnd()}");
        sb.AppendLine($"Install: {(UpdateService.IsInstalledBuild() ? "installed" : "portable")}");
        sb.AppendLine($"Credential storage: {SettingsService.ApiKeyStorageState} via {SettingsService.ApiKeyStorageName}");
        sb.AppendLine($"Data dir: {SettingsService.AppDirectory}");
        sb.AppendLine($"Hotkey: {(settings.HotkeyEnabled ? settings.HotkeyCombo : "off")}");
        sb.AppendLine($"Accounts ({settings.Accounts.Count}):");
        foreach (var a in settings.Accounts)
            sb.AppendLine($"  - id={a.Id} name={(string.IsNullOrWhiteSpace(a.Name) ? "-" : a.Name)} " +
                          $"provider={a.Provider} domain={a.BaseDomain} configured={a.Configured}");
        sb.AppendLine($"Active account: {settings.ActiveAccountId ?? "-"}");

        sb.AppendLine();
        var log = Path.Combine(SettingsService.AppDirectory, "brainfuel.log");
        sb.AppendLine($"--- brainfuel.log (tail) ---");
        try
        {
            if (File.Exists(log))
            {
                var lines = File.ReadAllLines(log);
                foreach (var line in lines.Skip(Math.Max(0, lines.Length - 40)))
                    sb.AppendLine(line);
            }
            else sb.AppendLine("<no log file>");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"<log unreadable: {ex.Message}>");
        }
        return sb.ToString();
    }
}
