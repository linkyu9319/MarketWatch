using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MarketWatch.App.Services;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class WatchlistItemViewModel : ObservableObject
{
    private readonly ThemeManager _themeManager;

    public Instrument Instrument { get; }
    public InstrumentId Id => Instrument.Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _displayCode;
    [ObservableProperty] private string _marketBadge;
    [ObservableProperty] private string _priceText = "--";
    [ObservableProperty] private string _changeText = "--";
    [ObservableProperty] private string _changePercentText = "--";
    [ObservableProperty] private string _quoteTimeText = "--";
    [ObservableProperty] private string _marketStatusText = "待刷新";
    [ObservableProperty] private string _sourceAndDelayText = "--";
    [ObservableProperty] private string? _errorTooltip;
    [ObservableProperty] private bool _isStaleOrError;
    [ObservableProperty] private System.Windows.Media.Brush _directionBrush;

    public decimal? SortPrice => LatestQuote?.LastPrice;
    public decimal? SortChangePercent => LatestQuote?.ChangePercent;
    public Quote? LatestQuote { get; private set; }

    public WatchlistItemViewModel(Instrument instrument, ThemeManager themeManager)
    {
        Instrument = instrument;
        _themeManager = themeManager;
        _name = instrument.Name;
        _displayCode = instrument.DisplayCode;
        _marketBadge = instrument.Id.Market switch
        {
            Market.HK => "港",
            Market.CN => "A",
            Market.US => "美",
            _ => ""
        };
        _directionBrush = themeManager.GetDirectionBrush(null);
    }

    public void ApplyQuote(Quote quote)
    {
        LatestQuote = quote;
        if (!string.IsNullOrWhiteSpace(quote.Name))
        {
            Name = quote.Name;
        }

        if (quote.LastPrice.HasValue)
        {
            var p = quote.LastPrice.Value;
            PriceText = p < 10m ? p.ToString("0.000") : p.ToString("N2");
        }
        else
        {
            PriceText = "--";
        }

        if (quote.Change.HasValue)
        {
            var c = quote.Change.Value;
            var sign = c > 0 ? "+" : "";
            ChangeText = Math.Abs(c) < 1m && quote.LastPrice is < 10m
                ? $"{sign}{c:0.000}"
                : $"{sign}{c:0.00}";
        }
        else
        {
            ChangeText = "--";
        }

        if (quote.ChangePercent.HasValue)
        {
            var pct = quote.ChangePercent.Value;
            var sign = pct > 0 ? "+" : "";
            ChangePercentText = $"{sign}{pct:0.00}%";
        }
        else
        {
            ChangePercentText = "--";
        }

        if (quote.QuoteTime.HasValue)
        {
            var qtCst = ChinaTimeHelper.ToChinaTime(quote.QuoteTime.Value);
            var todayCst = ChinaTimeHelper.GetChinaDate(DateTimeOffset.UtcNow);
            QuoteTimeText = DateOnly.FromDateTime(qtCst.DateTime) == todayCst
                ? qtCst.ToString("HH:mm:ss")
                : qtCst.ToString("MM-dd HH:mm");
        }
        else
        {
            QuoteTimeText = "--";
        }

        MarketStatusText = quote.MarketStatus switch
        {
            MarketStatus.Trading => "交易中",
            MarketStatus.PreMarket => "盘前",
            MarketStatus.LunchBreak => "午休",
            MarketStatus.AfterHours => "盘后",
            MarketStatus.Closed => "已休市",
            MarketStatus.Suspended => "停牌",
            _ => "未知"
        };

        var sourceName = quote.Source switch
        {
            "Tencent" => "腾讯",
            "Eastmoney" => "东财",
            _ => string.IsNullOrWhiteSpace(quote.Source) ? "--" : quote.Source
        };

        var delayLabel = quote.DelayStatus switch
        {
            DelayStatus.NearRealtime => "近实时",
            DelayStatus.Delayed => "延迟",
            DelayStatus.EndOfDay => "收盘",
            DelayStatus.Stale => "已过期",
            _ => "延迟未知"
        };

        SourceAndDelayText = $"{sourceName}·{delayLabel}";
        IsStaleOrError = quote.DelayStatus == DelayStatus.Stale || !string.IsNullOrWhiteSpace(quote.Error);
        ErrorTooltip = !string.IsNullOrWhiteSpace(quote.Error)
            ? $"{quote.Error}（接收时间：{ChinaTimeHelper.ToChinaTime(quote.ReceivedAt):HH:mm:ss}）"
            : $"来源：{sourceName} | 状态：{delayLabel} | 昨收：{quote.PreviousClose?.ToString("N2") ?? "--"} | 开盘：{quote.Open?.ToString("N2") ?? "--"} | 高/低：{quote.High?.ToString("N2") ?? "--"}/{quote.Low?.ToString("N2") ?? "--"}";

        RefreshColors();
    }

    public void RefreshColors()
    {
        DirectionBrush = _themeManager.GetDirectionBrush(LatestQuote?.ChangePercent ?? LatestQuote?.Change);
    }
}
