using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BrainFuel;

/// <summary>A small transient popup shown at the bottom-right of the screen.</summary>
public partial class NotificationWindow : Window
{
    public NotificationWindow()
    {
        InitializeComponent();
    }

    public void ShowNotification(string title, string message, Screen? screen = null)
    {
        TitleText.Text = title;
        MessageText.Text = message;

        PositionBottomRight(screen);
        Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    /// <summary>Parks the toast on the given screen (the card's screen by
    /// default, so the alert appears where the user is actually looking);
    /// falls back to the primary display.</summary>
    private void PositionBottomRight(Screen? screen)
    {
        var target = screen ?? Screens.Primary;
        if (target == null) return;
        double scale = RenderScaling > 0 ? RenderScaling : 1;
        int devW = (int)(Width * scale);
        int devH = (int)(Height * scale);
        var wa = target.WorkingArea;
        Position = new PixelPoint(wa.Right - devW - 16, wa.Bottom - devH - 16);
    }
}
