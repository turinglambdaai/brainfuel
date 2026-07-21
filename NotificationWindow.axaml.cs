using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace BrainFuel;

/// <summary>A small transient popup shown at the bottom-right of the screen.</summary>
public partial class NotificationWindow : Window
{
    public NotificationWindow()
    {
        InitializeComponent();
    }

    public void ShowNotification(string title, string message)
    {
        TitleText.Text = title;
        MessageText.Text = message;

        PositionBottomRight();
        Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    private void PositionBottomRight()
    {
        var screen = Screens.Primary;
        if (screen == null) return;
        double scale = RenderScaling > 0 ? RenderScaling : 1;
        int devW = (int)(Width * scale);
        int devH = (int)(Height * scale);
        var wa = screen.WorkingArea;
        Position = new PixelPoint(wa.Right - devW - 16, wa.Bottom - devH - 16);
    }
}
