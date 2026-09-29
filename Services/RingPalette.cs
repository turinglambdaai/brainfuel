using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;

namespace BrainFuel.Services;

/// <summary>
/// Curated color palettes for the quota rings. Free-form color picking was
/// deliberately skipped: a curated set keeps contrast sane and the UI to one
/// row of swatches. Only the calm-state colors change; the amber/red urgency
/// accents stay fixed for consistency.
/// </summary>
public static class RingPalette
{
    public sealed record Palette(string Id, string NameZh, string NameEn, string Weekly, string Hourly);

    public static readonly IReadOnlyList<Palette> All = new[]
    {
        new Palette("classic", "经典", "Classic", "#D97757", "#E8D4B8"),
        new Palette("teal", "青蓝", "Teal", "#2AA198", "#8FD3CA"),
        new Palette("forest", "森林", "Forest", "#5B8C5A", "#A7C7A1"),
        new Palette("violet", "紫罗兰", "Violet", "#8B7BD8", "#C9BFF2"),
        new Palette("mono", "墨色", "Mono", "#8A8680", "#C9C4BA"),
    };

    public static string DefaultId => "classic";

    public static Palette? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>Publishes the palette's ring colors into the app resources;
    /// views reading the dynamic resources update on their next render pass.</summary>
    public static void Apply(string? id)
    {
        var palette = Find(id) ?? All[0];
        if (Application.Current is null) return;
        var res = Application.Current.Resources;
        res["RingWeekly"] = new SolidColorBrush(Color.Parse(palette.Weekly));
        res["RingHourly"] = new SolidColorBrush(Color.Parse(palette.Hourly));
    }
}
