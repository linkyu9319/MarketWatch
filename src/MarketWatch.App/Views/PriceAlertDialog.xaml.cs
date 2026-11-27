using System.Globalization;
using System.Windows;
using MarketWatch.Core.Models;

namespace MarketWatch.App.Views;

public partial class PriceAlertDialog : Window
{
    private readonly Instrument _instrument;
    public PriceAlertRule? CreatedRule { get; private set; }

    public PriceAlertDialog(Instrument instrument, Quote? currentQuote)
    {
        InitializeComponent();
        _instrument = instrument;
        var priceHint = currentQuote?.LastPrice.HasValue == true ? $"（当前：{currentQuote.LastPrice:N2}）" : "";
        TargetText.Text = $"{instrument.Name} ({instrument.DisplayCode}){priceHint}";

        MetricBox.ItemsSource = new[] { AlertMetric.ChangePercent, AlertMetric.Price };
        MetricBox.SelectedIndex = 0;

        DirectionBox.ItemsSource = new[] { AlertDirection.CrossBelow, AlertDirection.CrossAbove };
        DirectionBox.SelectedIndex = 0;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(ThresholdBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            ErrorText.Text = "请输入有效的数字阈值。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (!int.TryParse(CooldownBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cooldown) || cooldown < 1)
        {
            cooldown = 10;
        }

        CreatedRule = new PriceAlertRule
        {
            InstrumentId = _instrument.Id,
            InstrumentName = _instrument.Name,
            Metric = (AlertMetric)MetricBox.SelectedItem,
            Direction = (AlertDirection)DirectionBox.SelectedItem,
            Threshold = threshold,
            CooldownMinutes = Math.Clamp(cooldown, 1, 1440),
            IsEnabled = true
        };

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
