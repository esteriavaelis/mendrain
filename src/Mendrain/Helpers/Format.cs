using System.Globalization;
using Mendrain.Core.Models;
using Microsoft.UI.Xaml;

namespace Mendrain.Helpers;

/// <summary>Display formatting used from x:Bind function bindings.</summary>
public static class Format
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        if (bytes < 0)
            return "—";
        if (bytes < 1024)
            return bytes == 1 ? "1 byte" : $"{bytes:N0} bytes";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Date(DateTime? date) =>
        date is { } d && d.Year > 1980 ? d.ToString("g", CultureInfo.CurrentCulture) : "";

    public static string Count(int n, string singular, string plural) =>
        $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? singular : plural)}";

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowIf(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;

    public static string StatusGlyph(JobStatus status) => status switch
    {
        JobStatus.Ready => "\uEA3A",       // ring
        JobStatus.Extracting => "\uE896",  // download
        JobStatus.Completed => "\uE930",   // completed
        JobStatus.Skipped => "\uE72A",     // forward
        JobStatus.Failed => "\uEA39",      // error badge
        JobStatus.Cancelled => "\uE711",   // x
        _ => "\uEA3A",
    };

    public static string StatusText(JobStatus status) => status switch
    {
        JobStatus.Ready => "Ready",
        JobStatus.Extracting => "Extracting",
        JobStatus.Completed => "Completed",
        JobStatus.Skipped => "Skipped",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        _ => "",
    };

    public static Microsoft.UI.Xaml.Media.Brush StatusBrush(JobStatus status)
    {
        var key = status switch
        {
            JobStatus.Completed => "SystemFillColorSuccessBrush",
            JobStatus.Failed => "SystemFillColorCriticalBrush",
            JobStatus.Skipped => "SystemFillColorCautionBrush",
            JobStatus.Extracting => "AccentTextFillColorPrimaryBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
    }

    public static bool IsRunning(JobStatus status) => status == JobStatus.Extracting;
    public static Visibility ShowWhenRunning(JobStatus status) => Show(status == JobStatus.Extracting);
    public static Visibility HideWhenRunning(JobStatus status) => Hide(status == JobStatus.Extracting);
    public static bool IsIndeterminate(double? progress) => progress is null;
    public static double Percent(double? progress) => (progress ?? 0) * 100;
}
