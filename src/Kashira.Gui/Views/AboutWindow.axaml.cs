using System;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kashira.Gui.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url) OpenUrl(url);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
        }
        catch { /* ignore */ }
    }
}
