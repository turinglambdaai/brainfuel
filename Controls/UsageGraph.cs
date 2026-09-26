using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace BrainFuel.Controls;

/// <summary>A point on the usage graph: X normalized 0..1 (time), Y used-% 0..100.</summary>
public readonly record struct GraphPoint(double X, double Pct);

/// <summary>
/// Minimal usage-over-time area chart for the detail panel: a soft area fill
/// under the line, faint guides at the 75/90 urgency thresholds. Values are
/// used-% so the chart reads the same way the rings do.
/// </summary>
public class UsageGraph : Control
{
    public static readonly StyledProperty<IReadOnlyList<GraphPoint>?> PointsProperty =
        AvaloniaProperty.Register<UsageGraph, IReadOnlyList<GraphPoint>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<UsageGraph, IBrush?>(nameof(Accent));

    static UsageGraph()
    {
        AffectsRender<UsageGraph>(PointsProperty, AccentProperty);
    }

    public IReadOnlyList<GraphPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static readonly ImmutableSolidColorBrush AmberGuide =
        new(Color.Parse("#40F5A623"));
    private static readonly ImmutableSolidColorBrush RedGuide =
        new(Color.Parse("#40E5484D"));
    private static readonly ImmutablePen GuidePen = new(AmberGuide, 1);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var points = Points;
        if (points is null || points.Count == 0) return;

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 8 || h < 8) return;

        double top = 2;
        double bottom = h - 2;
        double Y(double pct) => bottom - (bottom - top) * Math.Clamp(pct, 0, 100) / 100.0;

        // Urgency threshold guides, barely visible.
        DrawGuide(context, 75, w, Y);
        DrawGuide(context, 90, w, Y);

        var accent = Accent as SolidColorBrush ?? new SolidColorBrush(Color.FromRgb(150, 150, 150));
        var fill = new ImmutableSolidColorBrush(accent.Color, 0x30);
        var stroke = new ImmutablePen(new ImmutableSolidColorBrush(accent.Color), 1.5);

        // One continuous figure; resets show as steep drops, which is honest.
        var figure = new PathFigure { IsClosed = false };
        bool first = true;
        foreach (var p in points)
        {
            var pt = new Point(p.X * w, Y(p.Pct));
            if (first)
            {
                figure.StartPoint = pt;
                first = false;
            }
            else
            {
                (figure.Segments ??= new PathSegments()).Add(new LineSegment { Point = pt });
            }
        }
        if (first) return;

        var geometry = new PathGeometry { Figures = new PathFigures { figure } };

        var areaFigure = new PathFigure
        {
            StartPoint = figure.StartPoint,
            IsClosed = true,
        };
        foreach (var seg in figure.Segments!)
            areaFigure.Segments!.Add(seg);
        areaFigure.Segments!.Add(new LineSegment { Point = new Point(points[^1].X * w, bottom) });
        areaFigure.Segments!.Add(new LineSegment { Point = new Point(figure.StartPoint.X, bottom) });

        context.DrawGeometry(fill, null, new PathGeometry { Figures = new PathFigures { areaFigure } });
        context.DrawGeometry(null, stroke, geometry);
    }

    private static void DrawGuide(DrawingContext ctx, double pct, double w, Func<double, double> y)
    {
        double yy = y(pct);
        ctx.DrawLine(new ImmutablePen(pct >= 90 ? RedGuide : AmberGuide, 1),
            new Point(0, yy), new Point(w, yy));
    }
}
