using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Models;

namespace MarketWatch.App.ViewModels;

public sealed partial class IntradayChartViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty] private Instrument? _currentInstrument;
    [ObservableProperty] private string _title = "点击自选标的查看分时走势";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasChartData;
    [ObservableProperty] private string _emptyOrNoticeText = "在自选列表中单击任意标的以加载当日真实分时曲线。";

    public IntradaySeries? CurrentSeries { get; private set; }

    public event Action<IntradaySeries?>? SeriesChanged;

    public IntradayChartViewModel(AppStateCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public void Clear()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        CurrentInstrument = null;
        CurrentSeries = null;
        IsLoading = false;
        HasChartData = false;
        Title = "点击自选标的查看分时走势";
        Subtitle = "";
        EmptyOrNoticeText = "在自选列表中单击任意标的以加载当日真实分时曲线。";
        SeriesChanged?.Invoke(null);
    }

    public async Task LoadForInstrumentAsync(Instrument? instrument)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        CurrentInstrument = instrument;
        if (instrument is null)
        {
            Title = "分时走势";
            Subtitle = "";
            HasChartData = false;
            EmptyOrNoticeText = "请选择一只自选标的。";
            CurrentSeries = null;
            SeriesChanged?.Invoke(null);
            return;
        }

        Title = $"{instrument.Name} ({instrument.DisplayCode}) 分时图";
        Subtitle = "正在获取真实分钟数据...";
        IsLoading = true;

        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        try
        {
            var series = await _coordinator.GetIntradayAsync(instrument, ct);
            if (ct.IsCancellationRequested)
                return;

            CurrentSeries = series;
            var sourceLabel = series.Source switch
            {
                "Tencent" => "腾讯财经",
                "Eastmoney" => "东方财富",
                _ => series.Source
            };

            if (!series.IsSupported)
            {
                HasChartData = false;
                Subtitle = $"数据源：{sourceLabel}";
                EmptyOrNoticeText = series.StatusMessage ?? "当前数据源不支持该标的的分时走势（不会虚构模拟曲线）。";
            }
            else if (series.Points.Count <= 1)
            {
                HasChartData = false;
                var dateText = series.TradingDate.HasValue ? $"{series.TradingDate.Value:yyyy-MM-dd}" : "当日";
                Subtitle = $"{dateText} · 来源：{sourceLabel} · 昨收：{series.PreviousClose?.ToString("N2") ?? "--"}";
                EmptyOrNoticeText = series.StatusMessage ?? "当前尚未开盘或暂无足够分时点位。";
            }
            else
            {
                HasChartData = true;
                var firstPt = series.Points[0];
                var lastPt = series.Points[^1];
                var minP = series.Points.Min(p => p.Price);
                var maxP = series.Points.Max(p => p.Price);
                Subtitle = $"{series.TradingDate:yyyy-MM-dd} · {firstPt.Timestamp:HH:mm}~{lastPt.Timestamp:HH:mm} ({series.Points.Count}点) · 昨收 {series.PreviousClose?.ToString("N2") ?? "--"} · 高 {maxP:N2} / 低 {minP:N2} · 来源：{sourceLabel}";
                EmptyOrNoticeText = "";
            }

            SeriesChanged?.Invoke(series);
        }
        catch (OperationCanceledException)
        {
            // Ignored when switching instruments rapidly
        }
        catch (Exception ex)
        {
            HasChartData = false;
            Subtitle = "加载失败";
            EmptyOrNoticeText = $"分时数据请求失败：{ex.Message}";
            CurrentSeries = null;
            SeriesChanged?.Invoke(null);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshIntradayAsync()
    {
        if (CurrentInstrument is not null)
        {
            await LoadForInstrumentAsync(CurrentInstrument);
        }
    }
}
