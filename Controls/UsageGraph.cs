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

    public static readonly StyledProperty<IReadOnlyList<GraphPoint>?> PreviousPointsProperty =
        AvaloniaProperty.Register<UsageGraph, IReadOnlyList<GraphPoint>?>(nameof(PreviousPoints));

    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<UsageGraph, IBrush?>(nameof(Accent));

    static UsageGraph()
    {
        AffectsRender<UsageGraph>(PointsProperty, PreviousPointsProperty, AccentProperty);
    }

    public IReadOnlyList<GraphPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>The same window one period earlier (24h / 7d), drawn as a
    /// dimmer dashed line without fill for comparison.</summary>
    public IReadOnlyList<GraphPoint>? PreviousPoints
    {
        get => GetValue(PreviousPointsProperty);
        set => SetValue(PreviousPointsProperty, value);
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

        // Previous-period overlay first (below the main series), if present.
        if (PreviousPoints is { Count: > 1 } prev && BuildLine(prev, w, Y) is { } prevFigure)
        {
            var prevPen = new Pen(new ImmutableSolidColorBrush(accent.Color, 0x55), 1.2)
            {
                DashStyle = new DashStyle(new double[] { 3, 2 }, 0),
            };
            context.DrawGeometry(null, prevPen, new PathGeometry { Figures = new PathFigures { prevFigure } });
        }

        var fill = new ImmutableSolidColorBrush(accent.Color, 0x30);
        var stroke = new ImmutablePen(new ImmutableSolidColorBrush(accent.Color), 1.5);

        // One continuous figure; resets show as steep drops, which is honest.
        if (BuildLine(points, w, Y) is not { } figure) return;

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
        context.DrawGeometry(null, stroke, new PathGeometry { Figures = new PathFigures { figure } });
    }

    private static PathFigure? BuildLine(IReadOnlyList<GraphPoint> points, double w, Func<double, double> y)
    {
        PathFigure? figure = null;
        foreach (var p in points)
        {
            var pt = new Point(p.X * w, y(p.Pct));
            if (figure is null)
            {
                figure = new PathFigure { StartPoint = pt, IsClosed = false };
            }
            else
            {
                (figure.Segments ??= new PathSegments()).Add(new LineSegment { Point = pt });
            }
        }
        return figure;
    }

    private static void DrawGuide(DrawingContext ctx, double pct, double w, Func<double, double> y)
    {
        double yy = y(pct);
        ctx.DrawLine(new ImmutablePen(pct >= 90 ? RedGuide : AmberGuide, 1),
            new Point(0, yy), new Point(w, yy));
    }
}
