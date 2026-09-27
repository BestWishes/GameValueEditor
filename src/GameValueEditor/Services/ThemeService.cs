using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GameValueEditor.Services;

public enum ApplicationTheme
{
    Light,
    Dark,
    EyeCareGreen,
    WarmSand,
    MistBlue
}

public sealed class ThemeService
{
    public static ApplicationTheme CurrentTheme { get; private set; } = ApplicationTheme.Light;

    public void Apply(ApplicationTheme theme)
    {
        CurrentTheme = theme;
        var palette = theme switch
        {
            ApplicationTheme.Light => new Dictionary<string, string>
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
            },
            ApplicationTheme.Dark => new Dictionary<string, string>
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
            },
            ApplicationTheme.EyeCareGreen => new Dictionary<string, string>
            {
                ["WindowColor"] = "#18211D",
                ["PanelColor"] = "#202B25",
                ["PanelRaisedColor"] = "#2A372F",
                ["BorderColor"] = "#46594D",
                ["TextColor"] = "#DCE7DE",
                ["MutedTextColor"] = "#A9B9AC",
                ["AccentColor"] = "#7FB58A",
                ["AccentSoftColor"] = "#304C38",
                ["SelectionTextColor"] = "#F2F7F3",
                ["DangerColor"] = "#FF8A80"
            },
            ApplicationTheme.WarmSand => new Dictionary<string, string>
            {
                ["WindowColor"] = "#F3EBDD",
                ["PanelColor"] = "#FFF9EE",
                ["PanelRaisedColor"] = "#E8DCC8",
                ["BorderColor"] = "#CDBEA7",
                ["TextColor"] = "#332D25",
                ["MutedTextColor"] = "#776B5B",
                ["AccentColor"] = "#9A5D2E",
                ["AccentSoftColor"] = "#EED7BA",
                ["SelectionTextColor"] = "#2D241C",
                ["DangerColor"] = "#B43A33"
            },
            ApplicationTheme.MistBlue => new Dictionary<string, string>
            {
                ["WindowColor"] = "#E9EFF3",
                ["PanelColor"] = "#F7FAFC",
                ["PanelRaisedColor"] = "#DCE6EC",
                ["BorderColor"] = "#B8C8D2",
                ["TextColor"] = "#24323C",
                ["MutedTextColor"] = "#637985",
                ["AccentColor"] = "#3F718C",
                ["AccentSoftColor"] = "#C9DFEA",
                ["SelectionTextColor"] = "#162832",
                ["DangerColor"] = "#B33D46"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, null)
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
        var enabled = CurrentTheme is ApplicationTheme.Dark or ApplicationTheme.EyeCareGreen ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

public sealed record ThemeChoice(ApplicationTheme Value, string Display);
