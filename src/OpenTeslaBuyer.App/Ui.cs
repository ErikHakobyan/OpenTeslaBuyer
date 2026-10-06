using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace OpenTeslaBuyer.App;

/// <summary>Small Windows-specific helpers for the page view models.</summary>
internal static class Ui
{
    /// <summary>Puts text on the clipboard, retrying briefly if another program holds it. Returns false if it stayed busy.</summary>
    public static bool Copy(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }

    public static bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Open(folder);
    }

    public static void ShowInFolder(string path)
    {
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            OpenFolder(Path.GetDirectoryName(path)!);
    }

    /// <summary>Switches every window to the theme straight away.</summary>
    public static void ApplyTheme(AppTheme theme) => Application.Current.ThemeMode = theme switch
    {
        AppTheme.Light => ThemeMode.Light,
        AppTheme.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    public static string When(DateTimeOffset time) => time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s" : $"{span.Seconds} s";
}
