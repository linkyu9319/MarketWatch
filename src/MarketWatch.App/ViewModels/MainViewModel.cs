using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly ThemeManager _themeManager;

    public ObservableCollection<WatchlistItemViewModel> WatchlistItems { get; } = new();
    public IntradayChartViewModel IntradayChart { get; }

    [ObservableProperty] private WatchlistItemViewModel? _selectedItem;
    [ObservableProperty] private bool _isIntradayDrawerOpen;
    [ObservableProperty] private bool _isTopMost;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _connectionStatusText = "正在连接行情源...";
    [ObservableProperty] private bool _isConnectionHealthy = true;
    [ObservableProperty] private string? _bannerMessage;
    [ObservableProperty] private string _beijingTimeText = "--:--:--";
    [ObservableProperty] private string _cutoffCountdownText = "计算中...";
    [ObservableProperty] private bool _isCutoffUrgent;
    [ObservableProperty] private string _nextReminderText = "下一次提醒：--";
    [ObservableProperty] private string _versionBadgeText = "v1.0.0";

    public event Action? OpenSearchRequested;
    public event Action? OpenSettingsRequested;
    public event Action? OpenHistoryRequested;
    public event Action? OpenUpdatesRequested;
    public event Action? ToggleMiniWindowRequested;
    public event Action<ReminderStrength>? TestReminderRequested;
    public event Action<Instrument>? AddPriceAlertForInstrumentRequested;

    public MainViewModel(
        AppStateCoordinator coordinator,
        IClock clock,
        ThemeManager themeManager,
        IAppUpdateService updateService)
    {
        _coordinator = coordinator;
        _clock = clock;
        _themeManager = themeManager;
        _versionBadgeText = $"v{updateService.CurrentVersion}";
        IntradayChart = new IntradayChartViewModel(coordinator);

        _coordinator.QuotesUpdated += OnQuotesUpdated;
        _coordinator.SettingsUpdated += OnSettingsUpdated;
        _coordinator.ClockTicked += OnClockTicked;
        _themeManager.ThemeOrColorsChanged += OnThemeOrColorsChanged;

        SyncFromSettings(_coordinator.Settings);
        if (_coordinator.InitialLoadResult?.WasCorrupted == true)
        {
            BannerMessage = _coordinator.InitialLoadResult.Notice;
        }

        UpdateClockDisplays(_clock.UtcNow);
    }

    partial void OnSelectedItemChanged(WatchlistItemViewModel? value)
    {
        if (value is not null && IsIntradayDrawerOpen)
        {
            _ = IntradayChart.LoadForInstrumentAsync(value.Instrument);
        }
    }

    private void SyncFromSettings(AppSettings settings)
    {
        RunOnUi(() =>
        {
            IsTopMost = settings.TopMostMainWindow;
            IsDarkTheme = settings.ThemeMode == ThemeMode.Dark;

            var existingQuotes = _coordinator.GetAllLatestQuotes();
            var prevSelectedId = SelectedItem?.Id;

            WatchlistItems.Clear();
            foreach (var inst in settings.Watchlist)
            {
                var vm = new WatchlistItemViewModel(inst, _themeManager);
                if (existingQuotes.TryGetValue(inst.Id, out var q))
                {
                    vm.ApplyQuote(q);
                }
                WatchlistItems.Add(vm);
            }

            if (prevSelectedId.HasValue)
            {
                SelectedItem = WatchlistItems.FirstOrDefault(x => x.Id == prevSelectedId.Value);
            }

            UpdateCalendarNotice();
            UpdateClockDisplays(_clock.UtcNow);
        });
    }

    private void OnQuotesUpdated(BatchQuoteResponse response)
    {
        RunOnUi(() =>
        {
            IsRefreshing = false;
            foreach (var item in WatchlistItems)
            {
                if (response.Quotes.TryGetValue(item.Id, out var quote))
                {
                    item.ApplyQuote(quote);
                }
            }

            var srcDisplay = response.ActiveSourceSummary
                .Replace("Tencent", "腾讯财经")
                .Replace("Eastmoney", "东方财富");

            IsConnectionHealthy = response.AnyFreshData;
            ConnectionStatusText = response.AnyFreshData
                ? $"{srcDisplay} · {ChinaTimeHelper.ToChinaTime(_clock.UtcNow):HH:mm:ss}"
                : "离线 · 保留上次报价";

            if (!string.IsNullOrWhiteSpace(response.OverallWarning))
            {
                BannerMessage = response.OverallWarning;
            }
            else if (_coordinator.InitialLoadResult?.WasCorrupted != true)
            {
                UpdateCalendarNotice();
            }
        });
    }

    private void OnSettingsUpdated(AppSettings settings) => SyncFromSettings(settings);

    private void OnClockTicked(DateTimeOffset utcNow)
    {
        RunOnUi(() => UpdateClockDisplays(utcNow));
    }

    private void OnThemeOrColorsChanged()
    {
        RunOnUi(() =>
        {
            IsDarkTheme = _themeManager.CurrentTheme == ThemeMode.Dark;
            foreach (var item in WatchlistItems)
            {
                item.RefreshColors();
            }
        });
    }

    private void UpdateCalendarNotice()
    {
        var calEval = _coordinator.EvaluateTodayCalendar();
        if (calEval.CoverageStatus == CalendarCoverageStatus.UnknownFallbackToWeekdays)
        {
            BannerMessage = calEval.Description;
        }
        else if (BannerMessage is not null && BannerMessage.Contains("交易日历"))
        {
            BannerMessage = null;
        }
    }

    private void UpdateClockDisplays(DateTimeOffset utcNow)
    {
        var chinaNow = ChinaTimeHelper.ToChinaTime(utcNow);
        BeijingTimeText = $"北京时间 {chinaNow:yyyy-MM-dd HH:mm:ss}";

        var today = DateOnly.FromDateTime(chinaNow.DateTime);
        var activeCutoff = _coordinator.Settings.ReminderRules
            .Where(r => r.IsEnabled)
            .Select(r => r.CutoffTime)
            .DefaultIfEmpty(new TimeOnly(15, 0))
            .Min();

        var calEval = _coordinator.EvaluateTodayCalendar();
        if (!calEval.IsTradingDay)
        {
            IsCutoffUrgent = false;
            CutoffCountdownText = $"今日非开放日（{calEval.Description}）";
        }
        else
        {
            var cutoffDt = ChinaTimeHelper.CreateChinaDateTimeOffset(today, activeCutoff);
            var rem = cutoffDt - chinaNow;
            if (rem <= TimeSpan.Zero)
            {
                IsCutoffUrgent = false;
                CutoffCountdownText = $"今日 {activeCutoff:HH:mm} 基金申赎已截止";
            }
            else
            {
                IsCutoffUrgent = rem <= TimeSpan.FromMinutes(30);
                CutoffCountdownText = $"距 {activeCutoff:HH:mm} 截止 {(int)rem.TotalHours:00}:{rem.Minutes:00}:{rem.Seconds:00}";
            }
        }

        var next = _coordinator.GetNextUpcomingReminder();
        if (next is null)
        {
            NextReminderText = "下一次提醒：无已启用规则";
        }
        else
        {
            var nextDate = DateOnly.FromDateTime(next.NextChinaTime.DateTime);
            var dayLabel = nextDate == today
                ? "今天"
                : nextDate == today.AddDays(1) ? "明天" : next.NextChinaTime.ToString("MM-dd");
            var strengthTag = next.Rule.Strength == ReminderStrength.Strong ? "[强]" : "";
            NextReminderText = $"下次提醒：{dayLabel} {next.NextChinaTime:HH:mm}{strengthTag}";
        }
    }

    [RelayCommand]
    private async Task ManualRefreshAsync()
    {
        IsRefreshing = true;
        ConnectionStatusText = "正在刷新...";
        await _coordinator.RefreshQuotesAsync(forceResetBackoff: true);
        if (IsIntradayDrawerOpen && SelectedItem is not null)
        {
            await IntradayChart.LoadForInstrumentAsync(SelectedItem.Instrument);
        }
    }

    [RelayCommand]
    private async Task ToggleThemeAsync()
    {
        var next = IsDarkTheme ? ThemeMode.Light : ThemeMode.Dark;
        await _coordinator.UpdateSettingsAsync(s => s.ThemeMode = next);
        _themeManager.Apply(next, _coordinator.Settings.ColorScheme);
    }

    [RelayCommand]
    private async Task ToggleTopMostAsync()
    {
        var next = !IsTopMost;
        IsTopMost = next;
        await _coordinator.UpdateSettingsAsync(s => s.TopMostMainWindow = next);
    }

    [RelayCommand]
    private void OpenSearch() => OpenSearchRequested?.Invoke();

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private void OpenHistory() => OpenHistoryRequested?.Invoke();

    [RelayCommand]
    private void OpenUpdates() => OpenUpdatesRequested?.Invoke();

    [RelayCommand]
    private void ToggleMiniWindow() => ToggleMiniWindowRequested?.Invoke();

    [RelayCommand]
    private void TestNormalReminder() => TestReminderRequested?.Invoke(ReminderStrength.Normal);

    [RelayCommand]
    private void TestStrongReminder() => TestReminderRequested?.Invoke(ReminderStrength.Strong);

    [RelayCommand]
    private void InspectIntraday(WatchlistItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target is null)
            return;
        SelectedItem = target;
        IsIntradayDrawerOpen = true;
        _ = IntradayChart.LoadForInstrumentAsync(target.Instrument);
    }

    [RelayCommand]
    private void CloseIntradayDrawer()
    {
        IsIntradayDrawerOpen = false;
    }

    [RelayCommand]
    private async Task RemoveWatchlistItemAsync(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;
        await _coordinator.UpdateSettingsAsync(s =>
        {
            s.Watchlist.RemoveAll(w => w.Id == item.Id);
        });
    }

    [RelayCommand]
    private async Task MoveItemUpAsync(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;
        await _coordinator.UpdateSettingsAsync(s =>
        {
            var idx = s.Watchlist.FindIndex(w => w.Id == item.Id);
            if (idx > 0)
            {
                (s.Watchlist[idx - 1], s.Watchlist[idx]) = (s.Watchlist[idx], s.Watchlist[idx - 1]);
            }
        });
    }

    [RelayCommand]
    private async Task MoveItemDownAsync(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;
        await _coordinator.UpdateSettingsAsync(s =>
        {
            var idx = s.Watchlist.FindIndex(w => w.Id == item.Id);
            if (idx >= 0 && idx < s.Watchlist.Count - 1)
            {
                (s.Watchlist[idx], s.Watchlist[idx + 1]) = (s.Watchlist[idx + 1], s.Watchlist[idx]);
            }
        });
    }

    [RelayCommand]
    private void AddPriceAlertForItem(WatchlistItemViewModel? item)
    {
        if (item is not null)
        {
            AddPriceAlertForInstrumentRequested?.Invoke(item.Instrument);
        }
    }

    [RelayCommand]
    private void CopyCode(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;
        try
        {
            System.Windows.Clipboard.SetText(item.DisplayCode);
        }
        catch { }
    }

    [RelayCommand]
    private void DismissBanner() => BannerMessage = null;

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
