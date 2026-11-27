using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MarketWatch.Core.Models;
using AppThemeMode = MarketWatch.Core.Models.ThemeMode;

namespace MarketWatch.App.Services;

public sealed class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public AppThemeMode CurrentTheme { get; private set; } = AppThemeMode.Dark;
    public ColorScheme CurrentColorScheme { get; private set; } = ColorScheme.ChinaRedUpGreenDown;

    public event Action? ThemeOrColorsChanged;

    public void Apply(AppThemeMode themeMode, ColorScheme colorScheme)
    {
        CurrentTheme = themeMode;
        CurrentColorScheme = colorScheme;

        var app = System.Windows.Application.Current;
        if (app is null)
            return;

        var uri = themeMode == AppThemeMode.Light
            ? new Uri("pack://application:,,,/MarketWatch;component/Themes/LightTheme.xaml", UriKind.Absolute)
            : new Uri("pack://application:,,,/MarketWatch;component/Themes/DarkTheme.xaml", UriKind.Absolute);

        var nextDict = new ResourceDictionary { Source = uri };
        var merged = app.Resources.MergedDictionaries;

        // Remove ALL existing DarkTheme / LightTheme dictionaries (including the initial one declared in App.xaml)
        // so later entries never shadow the newly selected theme.
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var dict = merged[i];
            var src = dict.Source?.OriginalString;
            var isThemeDict = (!string.IsNullOrEmpty(src) &&
                               (src.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                                src.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)))
                              || dict.Contains("AppBackgroundBrush");
            if (isThemeDict)
            {
                merged.RemoveAt(i);
            }
        }

        merged.Insert(0, nextDict);

        // Update native Windows 10/11 title bars on all open WPF windows
        foreach (Window win in app.Windows)
        {
            ApplyWindowTitleBarTheme(win, themeMode);
        }

        ThemeOrColorsChanged?.Invoke();
    }

    public void ApplyToWindow(Window window)
    {
        if (window.IsLoaded)
        {
            ApplyWindowTitleBarTheme(window, CurrentTheme);
        }
        else
        {
            window.SourceInitialized += (_, _) => ApplyWindowTitleBarTheme(window, CurrentTheme);
        }
    }

    private static void ApplyWindowTitleBarTheme(Window window, AppThemeMode themeMode)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var useDark = themeMode == AppThemeMode.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
            }
        }
        catch
        {
            // Older Windows builds without DWM dark title bar attribute ignore safely
        }
    }

    public System.Windows.Media.Brush GetDirectionBrush(decimal? value)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return System.Windows.Media.Brushes.Gray;

        if (!value.HasValue || value.Value == 0m)
            return app.Resources["MarketFlatBrush"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

        var isUp = value.Value > 0m;
        var key = CurrentColorScheme == ColorScheme.ChinaRedUpGreenDown
            ? (isUp ? "MarketRedBrush" : "MarketGreenBrush")
            : (isUp ? "MarketGreenBrush" : "MarketRedBrush");

        return app.Resources[key] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;
    }

    public System.Windows.Media.Color GetThemeColor(string resourceKey, System.Windows.Media.Color fallback)
    {
        var app = System.Windows.Application.Current;
        if (app?.Resources[resourceKey] is System.Windows.Media.Color c)
            return c;
        return fallback;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
