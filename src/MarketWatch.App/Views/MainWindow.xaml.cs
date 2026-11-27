using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using MarketWatch.App.Services;
using MarketWatch.App.ViewModels;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using ScottPlot;

namespace MarketWatch.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppStateCoordinator _coordinator;
    private readonly ThemeManager _themeManager;

    public bool AllowTrueClose { get; set; }

    public MainWindow(MainViewModel viewModel, AppStateCoordinator coordinator, ThemeManager themeManager)
    {
        InitializeComponent();
        _vm = viewModel;
        _coordinator = coordinator;
        _themeManager = themeManager;
        DataContext = _vm;

        Width = _coordinator.Settings.WindowWidth;
        Height = _coordinator.Settings.WindowHeight;

        _vm.IntradayChart.SeriesChanged += OnIntradaySeriesChanged;
        _themeManager.ThemeOrColorsChanged += OnThemeChanged;
        _themeManager.ApplyToWindow(this);

        ConfigurePlotInteraction();
    }

    private void ConfigurePlotInteraction()
    {
        IntradayPlot.UserInputProcessor.IsEnabled = false;
    }

    private void OnWatchlistRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (WatchlistGrid.SelectedItem is WatchlistItemViewModel item)
        {
            _vm.InspectIntradayCommand.Execute(item);
        }
    }

    private void OnThemeChanged()
    {
        Dispatcher.BeginInvoke(() => RenderIntradayChart(_vm.IntradayChart.CurrentSeries));
    }

    private void OnIntradaySeriesChanged(IntradaySeries? series)
    {
        Dispatcher.BeginInvoke(() => RenderIntradayChart(series));
    }

    private void RenderIntradayChart(IntradaySeries? series)
    {
        var plt = IntradayPlot.Plot;
        plt.Clear();

        var isDark = _themeManager.CurrentTheme == MarketWatch.Core.Models.ThemeMode.Dark;
        var bgColor = isDark ? ScottPlot.Color.FromHex("#161B22") : ScottPlot.Color.FromHex("#FFFFFF");
        var gridColor = isDark ? ScottPlot.Color.FromHex("#263142") : ScottPlot.Color.FromHex("#E2E8F0");
        var textColor = isDark ? ScottPlot.Color.FromHex("#94A3B8") : ScottPlot.Color.FromHex("#475569");

        plt.FigureBackground.Color = bgColor;
        plt.DataBackground.Color = bgColor;
        plt.Axes.Color(textColor);
        plt.Grid.MajorLineColor = gridColor;

        if (series is null || !series.IsSupported || series.Points.Count <= 1)
        {
            IntradayPlot.Refresh();
            return;
        }

        // Preserve real timestamps AND insert NaN gaps across lunch breaks (> 20 minutes gap)
        var xs = new List<double>(series.Points.Count + 4);
        var ys = new List<double>(series.Points.Count + 4);

        for (var i = 0; i < series.Points.Count; i++)
        {
            var pt = series.Points[i];
            var chinaDt = ChinaTimeHelper.ToChinaTime(pt.Timestamp).DateTime;

            if (i > 0)
            {
                var prevDt = ChinaTimeHelper.ToChinaTime(series.Points[i - 1].Timestamp).DateTime;
                if (chinaDt - prevDt > TimeSpan.FromMinutes(20))
                {
                    xs.Add(prevDt.AddMinutes(1).ToOADate());
                    ys.Add(double.NaN);
                }
            }

            xs.Add(chinaDt.ToOADate());
            ys.Add((double)pt.Price);
        }

        var lastPrice = series.Points[^1].Price;
        var refPrice = series.PreviousClose ?? series.Points[0].Price;
        var isUp = lastPrice >= refPrice;

        var lineHex = _themeManager.CurrentColorScheme == ColorScheme.ChinaRedUpGreenDown
            ? (isUp ? (isDark ? "#FB7185" : "#DC2626") : (isDark ? "#34D399" : "#059669"))
            : (isUp ? (isDark ? "#34D399" : "#059669") : (isDark ? "#FB7185" : "#DC2626"));

        var scatter = plt.Add.ScatterLine(xs.ToArray(), ys.ToArray());
        scatter.Color = ScottPlot.Color.FromHex(lineHex);
        scatter.LineWidth = 1.8f;

        if (series.PreviousClose.HasValue)
        {
            var hline = plt.Add.HorizontalLine((double)series.PreviousClose.Value);
            hline.Color = isDark ? ScottPlot.Color.FromHex("#64748B") : ScottPlot.Color.FromHex("#94A3B8");
            hline.LinePattern = LinePattern.Dashed;
            hline.LineWidth = 1.1f;
        }

        plt.Axes.DateTimeTicksBottom();
        plt.Axes.AutoScale();
        plt.Layout.Frameless(false);

        IntradayPlot.Refresh();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _coordinator.Settings.MinimizeToTray)
        {
            Hide();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowTrueClose && _coordinator.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            var w = ActualWidth;
            var h = ActualHeight;
            _ = _coordinator.UpdateSettingsAsync(s =>
            {
                s.WindowWidth = w;
                s.WindowHeight = h;
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.IntradayChart.SeriesChanged -= OnIntradaySeriesChanged;
        _themeManager.ThemeOrColorsChanged -= OnThemeChanged;
        base.OnClosed(e);
    }

    public void RestoreAndFocus()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }
}
