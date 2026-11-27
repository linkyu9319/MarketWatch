using System.Windows;
using System.Windows.Media;
using MarketWatch.Core.Models;
using AppThemeMode = MarketWatch.Core.Models.ThemeMode;

namespace MarketWatch.App.Services;

public sealed class ThemeManager
{
    private ResourceDictionary? _currentThemeDict;

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
            ? new Uri("Themes/LightTheme.xaml", UriKind.Relative)
            : new Uri("Themes/DarkTheme.xaml", UriKind.Relative);

        var nextDict = new ResourceDictionary { Source = uri };
        var merged = app.Resources.MergedDictionaries;

        if (_currentThemeDict is not null)
        {
            var idx = merged.IndexOf(_currentThemeDict);
            if (idx >= 0)
            {
                merged[idx] = nextDict;
            }
            else
            {
                merged.Insert(0, nextDict);
            }
        }
        else
        {
            merged.Insert(0, nextDict);
        }

        _currentThemeDict = nextDict;
        ThemeOrColorsChanged?.Invoke();
    }

    public System.Windows.Media.Brush GetDirectionBrush(decimal? value)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return System.Windows.Media.Brushes.Gray;

        if (!value.HasValue || value.Value == 0m)
            return (System.Windows.Media.Brush)app.Resources["MarketFlatBrush"];

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
}
