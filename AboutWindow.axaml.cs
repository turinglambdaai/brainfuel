using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BrainFuel.Services;

namespace BrainFuel;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var zh = Strings.Current == AppLanguage.Zh;
        var buildKind = Strings.Get(UpdateService.IsInstalledBuild()
            ? "UpdateInstalledBuild"
            : "UpdatePortableBuild");

        Title = zh ? "关于 BrainFuel" : "About BrainFuel";
        VersionText.Text = $"v{version}";
        DescriptionText.Text = zh
            ? "一个用于监控 GLM Coding Plan 额度的轻量桌面小组件。"
            : "A lightweight desktop widget for monitoring GLM Coding Plan quota.";
        BuildText.Text = zh ? $"版本类型：{buildKind}" : $"Build: {buildKind}";
        LicenseText.Text = zh ? "免费开源软件 · MIT License" : "Free and open-source software · MIT License";
        SourceButton.Content = zh ? "GitHub 项目主页" : "GitHub repository";
        ReleasesButton.Content = zh ? "版本与下载" : "Releases & downloads";
        CloseButton.Content = zh ? "关闭" : "Close";
    }

    private void OpenSource_Click(object? sender, RoutedEventArgs e) => OpenUrl(UpdateService.RepositoryUrl);

    private void OpenReleases_Click(object? sender, RoutedEventArgs e) => OpenUrl(UpdateService.ReleasesUrl);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // About links are convenience actions; failure should not affect the app.
        }
    }
}
