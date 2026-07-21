using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BrainFuel.Controls;

/// <summary>
/// Two concentric progress rings: outer = weekly usage, inner = 5-hour usage.
/// Progress values are 0..1 (used portion); value changes animate via Transitions.
/// </summary>
public class UsageRing : Control
{
    public static readonly StyledProperty<double> WeeklyProgressProperty =
        AvaloniaProperty.Register<UsageRing, double>(nameof(WeeklyProgress));

    public static readonly StyledProperty<double> HourlyProgressProperty =
        AvaloniaProperty.Register<UsageRing, double>(nameof(HourlyProgress));

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.FromRgb(58, 57, 55));     // warm dark
    private static readonly IBrush WeeklyBrush = new SolidColorBrush(Color.FromRgb(217, 119, 87));  // Anthropic clay
    private static readonly IBrush HourlyBrush = new SolidColorBrush(Color.FromRgb(232, 212, 184)); // warm sand

    static UsageRing()
    {
        AffectsRender<UsageRing>(WeeklyProgressProperty, HourlyProgressProperty);
    }

    public UsageRing()
    {
        Transitions = new Avalonia.Animation.Transitions
        {
            new Avalonia.Animation.DoubleTransition
            {
                Property = WeeklyProgressProperty,
                Duration = TimeSpan.FromMilliseconds(500),
            },
            new Avalonia.Animation.DoubleTransition
            {
                Property = HourlyProgressProperty,
                Duration = TimeSpan.FromMilliseconds(500),
            },
        };
    }

    public double WeeklyProgress
    {
        get => GetValue(WeeklyProgressProperty);
        set => SetValue(WeeklyProgressProperty, value);
    }

    public double HourlyProgress
    {
        get => GetValue(HourlyProgressProperty);
        set => SetValue(HourlyProgressProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width < 6 || size.Height < 6) return;

        double cx = size.Width / 2;
        double cy = size.Height / 2;
        double min = Math.Min(size.Width, size.Height);

        double outerStroke = Math.Max(6, min * 0.10);
        double innerStroke = Math.Max(5, min * 0.082);
        double outerR = (min - outerStroke) / 2;
        double innerR = outerR - outerStroke / 2 - innerStroke / 2 - 3;

        DrawRing(context, cx, cy, outerR, outerStroke, WeeklyProgress, TrackBrush, WeeklyBrush);
        DrawRing(context, cx, cy, innerR, innerStroke, HourlyProgress, TrackBrush, HourlyBrush);
    }

    private static void DrawRing(DrawingContext ctx, double cx, double cy, double radius,
                                 double stroke, double progress, IBrush track, IBrush value)
    {
        if (radius <= 0) return;
        progress = progress < 0 ? 0 : progress > 1 ? 1 : progress;

        var valuePen = new Pen(value, stroke) { LineCap = PenLineCap.Round };
        var rect = new Rect(cx - radius, cy - radius, radius * 2, radius * 2);

        // dim background track (full circle)
        ctx.DrawEllipse(null, new Pen(track, stroke), rect);

        // value arc
        double sweep = progress * 360;
        if (sweep <= 0) return;
        if (sweep >= 359.999)
        {
            ctx.DrawEllipse(null, valuePen, rect);
            return;
        }
        ctx.DrawGeometry(null, valuePen, BuildArc(cx, cy, radius, sweep));
    }

    private static Geometry BuildArc(double cx, double cy, double radius, double sweepDegrees)
    {
        // Start at top (-90°), sweep clockwise.
        double startRad = -Math.PI / 2;
        double endRad = startRad + sweepDegrees * Math.PI / 180.0;
        double sx = cx + radius * Math.Cos(startRad);
        double sy = cy + radius * Math.Sin(startRad);
        double ex = cx + radius * Math.Cos(endRad);
        double ey = cy + radius * Math.Sin(endRad);

        var figure = new PathFigure
        {
            StartPoint = new Point(sx, sy),
            IsClosed = false,
            Segments = new PathSegments
            {
                new ArcSegment
                {
                    Size = new Size(radius, radius),
                    Point = new Point(ex, ey),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = sweepDegrees > 180,
                },
            },
        };

        return new PathGeometry { Figures = new PathFigures { figure } };
    }
}
