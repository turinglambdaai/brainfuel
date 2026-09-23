using System;
using System.IO;

namespace BrainFuel.Services;

/// <summary>
/// Minimal append-only error log next to the settings file. Refresh failures
/// used to vanish into a bare catch, leaving "filled in the key but nothing
/// shows" undiagnosable; this gives support a file to read.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(SettingsService.AppDirectory, "brainfuel.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.AppDirectory);
            lock (Gate)
            {
                TrimIfNeeded();
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    /// <summary>Keeps the second half of the file once it grows past the cap.</summary>
    private static void TrimIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            using var reader = new StreamReader(LogPath);
            reader.BaseStream.Seek(-(MaxBytes / 2), SeekOrigin.End);
            _ = reader.ReadLine(); // drop the partial first line
            File.WriteAllText(LogPath, reader.ReadToEnd());
        }
        catch { /* best effort */ }
    }
}
