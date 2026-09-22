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
        var target = FindSavedScreenIdentity(window, settings)
                     ?? window.Screens.Primary
                     ?? window.Screens.All.FirstOrDefault();
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
        settings.WindowScreenName = string.IsNullOrWhiteSpace(screen.DisplayName) ? null : screen.DisplayName;
        settings.WindowScreenX = screen.Bounds.X;
        settings.WindowScreenY = screen.Bounds.Y;
        settings.WindowScreenWidth = screen.Bounds.Width;
        settings.WindowScreenHeight = screen.Bounds.Height;
        settings.WindowRelativeX = Normalize(window.Position.X, range.MinX, range.MaxX);
        settings.WindowRelativeY = Normalize(window.Position.Y, range.MinY, range.MaxY);
    }

    public static void RepairAfterScreenChange(Window window, AppSettings settings)
    {
        if (settings.WindowRelativeX is double rx && settings.WindowRelativeY is double ry)
        {
            var intendedScreen = FindSavedScreenIdentity(window, settings);
            if (intendedScreen is not null)
            {
                var currentScreen = FindBestIntersectingScreen(window);
                if (currentScreen is null || !SameScreen(currentScreen, intendedScreen))
                {
                    // The OS may have temporarily relocated the window while a
                    // display was being rearranged. Honor the user's saved display
                    // identity rather than accepting that transient relocation.
                    window.Position = PositionFromRelative(window, intendedScreen, rx, ry);
                }
                else
                {
                    window.Position = ClampFullyVisible(window, intendedScreen, window.Position);
                }

                Capture(window, settings);
                return;
            }

            // The intended physical display is genuinely gone. Move to primary at
            // the same relative position, then Capture so reconnecting the old
            // display later does not unexpectedly pull the card back.
            var primary = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
            if (primary is null)
                return;

            window.Position = PositionFromRelative(window, primary, rx, ry);
            Capture(window, settings);
            return;
        }

        // Legacy placement that has not yet been migrated to a relative position.
        var visibleScreen = FindBestIntersectingScreen(window);
        if (visibleScreen is not null && IsSufficientlyVisible(window, visibleScreen))
        {
            window.Position = ClampFullyVisible(window, visibleScreen, window.Position);
            Capture(window, settings);
            return;
        }

        var fallback = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (fallback is not null)
        {
            window.Position = PositionFromRelative(window, fallback, 1.0, 0.0);
            Capture(window, settings);
        }
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

    private static Screen? FindSavedScreenIdentity(Window window, AppSettings settings)
    {
        // Exact geometry is strongest and avoids ambiguity when two monitors expose
        // the same model/display name.
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

        // A display name usually survives display rearrangement and resolution/DPI
        // changes. If multiple identical monitors share a name, prefer the one
        // whose dimensions are closest to the previously saved display.
        if (!string.IsNullOrWhiteSpace(settings.WindowScreenName))
        {
            var named = window.Screens.All
                .Where(screen => string.Equals(screen.DisplayName, settings.WindowScreenName, StringComparison.Ordinal))
                .OrderBy(screen => GeometryDistance(screen.Bounds, settings))
                .FirstOrDefault();
            if (named is not null)
                return named;
        }

        return null;
    }

    private static long GeometryDistance(PixelRect bounds, AppSettings settings)
    {
        long distance = 0;
        if (settings.WindowScreenWidth is int width)
            distance += Math.Abs((long)bounds.Width - width);
        if (settings.WindowScreenHeight is int height)
            distance += Math.Abs((long)bounds.Height - height);
        return distance;
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
