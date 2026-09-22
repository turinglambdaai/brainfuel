using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace BrainFuel.Services;

/// <summary>
/// Keeps the widget usable across monitor unplug/replug, DPI changes, resolution
/// changes and taskbar/dock moves. Placement is stored relative to a display's
/// working area rather than relying only on fragile absolute desktop pixels.
/// </summary>
public static class WindowPlacementService
{
    private const int EdgeMargin = 20;
    private const int MinimumVisibleWidth = 72;
    private const int MinimumVisibleHeight = 48;

    public static PixelPoint Restore(Window window, AppSettings settings)
    {
        var target = FindSavedScreen(window, settings) ?? window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (target is null)
            return window.Position;

        if (settings.WindowRelativeX is double rx && settings.WindowRelativeY is double ry)
            return PositionFromRelative(window, target, rx, ry);

        // One-time migration from the old absolute-coordinate settings.
        if (settings.WindowX is int x && settings.WindowY is int y)
        {
            var old = new PixelPoint(x, y);
            var oldScreen = window.Screens.ScreenFromPoint(old);
            if (oldScreen is not null)
                return ClampFullyVisible(window, oldScreen, old);
        }

        // New install: primary display, upper-right, with breathing room from
        // system chrome and application window controls.
        return PositionFromRelative(window, target, 1.0, 0.0);
    }

    public static void Capture(Window window, AppSettings settings)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? FindBestIntersectingScreen(window);
        if (screen is null)
            return;

        var wa = screen.WorkingArea;
        var size = GetPixelSize(window, screen);
        var range = GetPlacementRange(wa, size);

        settings.WindowX = window.Position.X;
        settings.WindowY = window.Position.Y;
        settings.WindowScreenX = screen.Bounds.X;
        settings.WindowScreenY = screen.Bounds.Y;
        settings.WindowScreenWidth = screen.Bounds.Width;
        settings.WindowScreenHeight = screen.Bounds.Height;
        settings.WindowRelativeX = Normalize(window.Position.X, range.MinX, range.MaxX);
        settings.WindowRelativeY = Normalize(window.Position.Y, range.MinY, range.MaxY);
    }

    public static void RepairAfterScreenChange(Window window, AppSettings settings)
    {
        var currentScreen = FindBestIntersectingScreen(window);
        if (currentScreen is not null && IsSufficientlyVisible(window, currentScreen))
        {
            // The display still exists but its work area may have changed because
            // of DPI, resolution, orientation, taskbar or dock changes.
            window.Position = ClampFullyVisible(window, currentScreen, window.Position);
            Capture(window, settings);
            return;
        }

        // The display carrying the widget disappeared. Preserve the latest
        // relative position and move to primary. We intentionally do not jump
        // back automatically if that external display is reconnected later.
        var primary = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (primary is null)
            return;

        var rx = Math.Clamp(settings.WindowRelativeX ?? 1.0, 0, 1);
        var ry = Math.Clamp(settings.WindowRelativeY ?? 0.0, 0, 1);
        window.Position = PositionFromRelative(window, primary, rx, ry);
        Capture(window, settings);
    }

    public static void MoveToPrimary(Window window, AppSettings settings)
    {
        Capture(window, settings);
        var primary = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (primary is null)
            return;

        window.Position = PositionFromRelative(
            window,
            primary,
            Math.Clamp(settings.WindowRelativeX ?? 1.0, 0, 1),
            Math.Clamp(settings.WindowRelativeY ?? 0.0, 0, 1));
        Capture(window, settings);
    }

    public static void MoveToNextScreen(Window window, AppSettings settings)
    {
        var screens = window.Screens.All.ToArray();
        if (screens.Length < 2)
            return;

        Capture(window, settings);
        var current = window.Screens.ScreenFromWindow(window) ?? FindBestIntersectingScreen(window);
        var index = current is null ? -1 : Array.FindIndex(screens, s => SameScreen(s, current));
        var next = screens[(index + 1 + screens.Length) % screens.Length];

        window.Position = PositionFromRelative(
            window,
            next,
            Math.Clamp(settings.WindowRelativeX ?? 1.0, 0, 1),
            Math.Clamp(settings.WindowRelativeY ?? 0.0, 0, 1));
        Capture(window, settings);
    }

    private static Screen? FindSavedScreen(Window window, AppSettings settings)
    {
        if (settings.WindowScreenX is int sx &&
            settings.WindowScreenY is int sy &&
            settings.WindowScreenWidth is int sw &&
            settings.WindowScreenHeight is int sh)
        {
            foreach (var screen in window.Screens.All)
            {
                var b = screen.Bounds;
                if (b.X == sx && b.Y == sy && b.Width == sw && b.Height == sh)
                    return screen;
            }
        }

        if (settings.WindowX is int x && settings.WindowY is int y)
            return window.Screens.ScreenFromPoint(new PixelPoint(x, y));

        return null;
    }

    private static Screen? FindBestIntersectingScreen(Window window)
    {
        Screen? best = null;
        long bestArea = 0;
        foreach (var screen in window.Screens.All)
        {
            var area = IntersectionArea(WindowRect(window, screen), screen.WorkingArea);
            if (area > bestArea)
            {
                bestArea = area;
                best = screen;
            }
        }
        return best;
    }

    private static bool IsSufficientlyVisible(Window window, Screen screen)
    {
        var rect = WindowRect(window, screen);
        var wa = screen.WorkingArea;
        var left = Math.Max(rect.X, wa.X);
        var top = Math.Max(rect.Y, wa.Y);
        var right = Math.Min(rect.Right, wa.Right);
        var bottom = Math.Min(rect.Bottom, wa.Bottom);
        return right - left >= MinimumVisibleWidth && bottom - top >= MinimumVisibleHeight;
    }

    private static PixelPoint ClampFullyVisible(Window window, Screen screen, PixelPoint desired)
    {
        var range = GetPlacementRange(screen.WorkingArea, GetPixelSize(window, screen));
        return new PixelPoint(
            Math.Clamp(desired.X, range.MinX, range.MaxX),
            Math.Clamp(desired.Y, range.MinY, range.MaxY));
    }

    private static PixelPoint PositionFromRelative(Window window, Screen screen, double x, double y)
    {
        var range = GetPlacementRange(screen.WorkingArea, GetPixelSize(window, screen));
        return new PixelPoint(
            Interpolate(range.MinX, range.MaxX, x),
            Interpolate(range.MinY, range.MaxY, y));
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) GetPlacementRange(PixelRect wa, PixelSize size)
    {
        var minX = wa.X + EdgeMargin;
        var minY = wa.Y + EdgeMargin;
        var maxX = Math.Max(minX, wa.Right - EdgeMargin - size.Width);
        var maxY = Math.Max(minY, wa.Bottom - EdgeMargin - size.Height);
        return (minX, maxX, minY, maxY);
    }

    private static PixelSize GetPixelSize(Window window, Screen screen)
    {
        var widthDip = window.Bounds.Width > 0 ? window.Bounds.Width : window.Width;
        var heightDip = window.Bounds.Height > 0 ? window.Bounds.Height : window.Height;
        return new PixelSize(
            Math.Max(1, (int)Math.Ceiling(widthDip * screen.Scaling)),
            Math.Max(1, (int)Math.Ceiling(heightDip * screen.Scaling)));
    }

    private static PixelRect WindowRect(Window window, Screen screen)
    {
        var size = GetPixelSize(window, screen);
        return new PixelRect(window.Position, size);
    }

    private static long IntersectionArea(PixelRect a, PixelRect b)
    {
        var width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y));
        return (long)width * height;
    }

    private static double Normalize(int value, int min, int max)
        => max <= min ? 0 : Math.Clamp((double)(value - min) / (max - min), 0, 1);

    private static int Interpolate(int min, int max, double value)
        => max <= min ? min : (int)Math.Round(min + Math.Clamp(value, 0, 1) * (max - min));

    private static bool SameScreen(Screen a, Screen b)
        => a.Bounds.Equals(b.Bounds);
}
