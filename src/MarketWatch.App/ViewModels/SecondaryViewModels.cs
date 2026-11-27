using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;

    public ObservableCollection<ReminderTriggerRecord> ReminderRecords { get; } = new();
    public ObservableCollection<PriceAlertRecord> PriceAlertRecords { get; } = new();

    [ObservableProperty] private string _summaryText = "加载中...";

    public HistoryViewModel(AppStateCoordinator coordinator)
    {
        _coordinator = coordinator;
        _ = LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        AlertHistoryData data;
        try
        {
            data = await _coordinator.LoadHistoryAsync();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            SummaryText = $"读取历史失败：{ex.Message}";
            return;
        }
        ReminderRecords.Clear();
        foreach (var r in data.ReminderRecords)
            ReminderRecords.Add(r);

        PriceAlertRecords.Clear();
        foreach (var p in data.PriceAlertRecords)
            PriceAlertRecords.Add(p);

        SummaryText = $"定时提醒记录 {ReminderRecords.Count} 条 · 价格预警记录 {PriceAlertRecords.Count} 条";
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            if (!await _coordinator.ClearHistoryAsync())
            {
                SummaryText = "暂时无法读取或保存提醒触发记录，已保留历史，请稍后重试。";
                return;
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            SummaryText = $"清空历史失败：{ex.Message}";
            return;
        }
        await LoadAsync();
    }
}

public sealed partial class MiniWindowViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly ThemeManager _themeManager;

    public ObservableCollection<WatchlistItemViewModel> CompactItems { get; } = new();

    [ObservableProperty] private string _beijingTimeText = "--:--:--";
    [ObservableProperty] private string _cutoffShortText = "距15:00 --:--";
    [ObservableProperty] private bool _isUrgent;
    [ObservableProperty] private double _windowOpacity = 0.94;
    [ObservableProperty] private bool _hasCompactItems;

    public MiniWindowViewModel(AppStateCoordinator coordinator, IClock clock, ThemeManager themeManager)
    {
        _coordinator = coordinator;
        _clock = clock;
        _themeManager = themeManager;

        _coordinator.QuotesUpdated += OnQuotesUpdated;
        _coordinator.SettingsUpdated += OnSettingsUpdated;
        _coordinator.ClockTicked += OnClockTicked;
        _themeManager.ThemeOrColorsChanged += OnThemeChanged;

        Sync(_coordinator.Settings);
        UpdateClock(_clock.UtcNow);
    }

    private void Sync(AppSettings settings)
    {
        RunOnUi(() =>
        {
            WindowOpacity = settings.MiniWindowOpacity;
            var latest = _coordinator.GetAllLatestQuotes();
            CompactItems.Clear();
            foreach (var inst in settings.Watchlist.Where(w => w.ShowInMiniWindow))
            {
                var vm = new WatchlistItemViewModel(inst, _themeManager);
                if (latest.TryGetValue(inst.Id, out var q))
                    vm.ApplyQuote(q);
                CompactItems.Add(vm);
            }
            HasCompactItems = CompactItems.Count > 0;
        });
    }

    private void OnQuotesUpdated(BatchQuoteResponse resp)
    {
        RunOnUi(() =>
        {
            foreach (var item in CompactItems)
            {
                if (resp.Quotes.TryGetValue(item.Id, out var q))
                    item.ApplyQuote(q);
            }
        });
    }

    private void OnSettingsUpdated(AppSettings s) => Sync(s);
    private void OnClockTicked(DateTimeOffset utcNow) => RunOnUi(() => UpdateClock(utcNow));
    private void OnThemeChanged() => RunOnUi(() => { foreach (var i in CompactItems) i.RefreshColors(); });

    private void UpdateClock(DateTimeOffset utcNow)
    {
        var cst = ChinaTimeHelper.ToChinaTime(utcNow);
        BeijingTimeText = cst.ToString("HH:mm:ss");
        var today = DateOnly.FromDateTime(cst.DateTime);
        var cutoff = _coordinator.Settings.ReminderRules
            .Where(r => r.IsEnabled)
            .Select(r => r.CutoffTime)
            .DefaultIfEmpty(new TimeOnly(15, 0))
            .Min();

        var cal = _coordinator.EvaluateTodayCalendar();
        if (!cal.IsTradingDay)
        {
            IsUrgent = false;
            CutoffShortText = "今日休市";
            return;
        }

        var rem = ChinaTimeHelper.CreateChinaDateTimeOffset(today, cutoff) - cst;
        if (rem <= TimeSpan.Zero)
        {
            IsUrgent = false;
            CutoffShortText = $"{cutoff:HH:mm} 已截止";
        }
        else
        {
            IsUrgent = rem <= TimeSpan.FromMinutes(30);
            CutoffShortText = $"距{cutoff:HH:mm} {(int)rem.TotalHours:00}:{rem.Minutes:00}:{rem.Seconds:00}";
        }
    }

    private static void RunOnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess() || d.HasShutdownStarted || d.HasShutdownFinished || !d.Thread.IsAlive) action();
        else d.BeginInvoke(action);
    }
}
