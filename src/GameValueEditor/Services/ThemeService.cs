using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GameValueEditor.Services;

public enum ApplicationTheme
{
    Light,
    Dark
}

public sealed class ThemeService
{
    public static ApplicationTheme CurrentTheme { get; private set; } = ApplicationTheme.Light;

    public void Apply(ApplicationTheme theme)
    {
        CurrentTheme = theme;
        var palette = theme == ApplicationTheme.Light
            ? new Dictionary<string, string>
            {
                ["WindowColor"] = "#F5F7FA",
                ["PanelColor"] = "#FFFFFF",
                ["PanelRaisedColor"] = "#EEF2F7",
                ["BorderColor"] = "#CBD5E1",
                ["TextColor"] = "#172033",
                ["MutedTextColor"] = "#64748B",
                ["AccentColor"] = "#2563EB",
                ["AccentSoftColor"] = "#DBEAFE",
                ["SelectionTextColor"] = "#0F172A",
                ["DangerColor"] = "#C62828"
            }
            : new Dictionary<string, string>
            {
                ["WindowColor"] = "#0D1117",
                ["PanelColor"] = "#151B23",
                ["PanelRaisedColor"] = "#1B2430",
                ["BorderColor"] = "#344054",
                ["TextColor"] = "#F2F4F7",
                ["MutedTextColor"] = "#AAB4C3",
                ["AccentColor"] = "#2563EB",
                ["AccentSoftColor"] = "#243B5A",
                ["SelectionTextColor"] = "#FFFFFF",
                ["DangerColor"] = "#FF7B72"
            };

        foreach (var (key, value) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            Application.Current.Resources[key] = color;
            Application.Current.Resources[key.Replace("Color", "Brush", StringComparison.Ordinal)] = new SolidColorBrush(color);
        }

        foreach (Window window in Application.Current.Windows)
            ApplyWindowChrome(window);
    }

    public static void ApplyWindowChrome(Window window)
    {
        if (!OperatingSystem.IsWindows() || !window.IsLoaded) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = CurrentTheme == ApplicationTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

public sealed record ThemeChoice(ApplicationTheme Value, string Display);
