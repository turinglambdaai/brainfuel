using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace BrainFuel.Services;

/// <summary>
/// Parses hotkey combos like "Ctrl+Alt+B". Kept dependency-free so it is
/// unit-testable on every platform.
/// </summary>
public static class HotkeyParse
{
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        uint mods = 0;
        string? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= 0x0002; break;
                case "alt": mods |= 0x0001; break;
                case "shift": mods |= 0x0004; break;
                case "win" or "windows": mods |= 0x0008; break;
                default:
                    if (key is not null) return false; // two non-modifier keys
                    key = part;
                    break;
            }
        }

        if (key is null || mods == 0) return false; // a bare key would swallow typing everywhere
        modifiers = mods;

        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
        {
            var c = char.ToUpperInvariant(key[0]);
            virtualKey = (uint)(c >= '0' && c <= '9' ? c : c); // 'A'-'Z' and '0'-'9' share ASCII/VK codes
            return true;
        }

        if (key.Length is 2 or 3 && key[0] is 'F' or 'f' && int.TryParse(key[1..], out var f) && f is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + f - 1); // VK_F1 = 0x70
            return true;
        }

        return false;
    }
}

#if WINDOWS
/// <summary>
/// Registers a system-wide hotkey on a dedicated message-only thread (Win32
/// RegisterHotKey with a NULL window posts WM_HOTKEY to the calling thread's
/// queue, so no wndproc subclassing of the Avalonia window is needed). Other
/// platforms: registration reports failure and the feature stays off.
/// </summary>
public static class GlobalHotkey
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_QUIT = 0x0012;

    private static Thread? _thread;
    private static uint _threadId;
    private static readonly object Gate = new();
    private static Action? _onTrigger;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>(Re)registers the combo. Returns false when unsupported or rejected.</summary>
    public static bool TryRegister(string combo, Action onTrigger)
    {
        Stop();
        if (!IsSupported || !HotkeyParse.TryParse(combo, out var mods, out var vk))
            return false;

        lock (Gate)
        {
            _onTrigger = onTrigger;
            var started = new ManualResetEventSlim(false);
            var comboCapture = combo;
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                // MOD_NOREPEAT keeps holding the combo from flooding the app.
                bool ok = RegisterHotKey(IntPtr.Zero, 1, mods | 0x4000, vk);
                started.Set();
                if (!ok)
                {
                    _threadId = 0;
                    return;
                }

                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_HOTKEY && (msg.wParam & 0xFFFF) == 1)
                    {
                        var trigger = _onTrigger;
                        if (trigger is not null)
                            Dispatcher.UIThread.Post(trigger);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "BrainFuel hotkey",
            };
            _thread.Start();
            started.Wait(TimeSpan.FromSeconds(2));
        }
        return true;
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                _threadId = 0;
            }
            _onTrigger = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
#else
public static class GlobalHotkey
{
    public static bool IsSupported => false;

    public static bool TryRegister(string combo, Action onTrigger) => false;

    public static void Stop() { }
}
#endif
