using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public enum WatchlistGroupTab
{
    All,
    HK,
    CN,
    US,
    MiniWindow
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;
    private readonly IClock _clock;
    private readonly ThemeManager _themeManager;
    private readonly List<WatchlistItemViewModel> _allWatchlistItems = new();
    private bool _calendarNoticeDismissed;
    private DateOnly _calendarNoticeDismissedDate;

    public ObservableCollection<WatchlistItemViewModel> WatchlistItems { get; } = new();
    public IntradayChartViewModel IntradayChart { get; }

    [ObservableProperty] private WatchlistGroupTab _selectedGroupTab = WatchlistGroupTab.All;
    [ObservableProperty] private bool _isAllTabSelected = true;
    [ObservableProperty] private bool _isHkTabSelected;
    [ObservableProperty] private bool _isCnTabSelected;
    [ObservableProperty] private bool _isUsTabSelected;
    [ObservableProperty] private bool _isMiniTabSelected;
    [ObservableProperty] private string _allTabLabel = "全部 (0)";
    [ObservableProperty] private string _hkTabLabel = "港股 (0)";
    [ObservableProperty] private string _cnTabLabel = "沪深 (0)";
    [ObservableProperty] private string _usTabLabel = "美股 (0)";
    [ObservableProperty] private string _miniTabLabel = "小窗 (0)";
    [ObservableProperty] private bool _isCurrentGroupEmpty;
    [ObservableProperty] private string _emptyGroupHint = "";

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

            _allWatchlistItems.Clear();
            foreach (var inst in settings.Watchlist)
            {
                var vm = new WatchlistItemViewModel(inst, _themeManager, HandleMiniWindowToggled);
                if (existingQuotes.TryGetValue(inst.Id, out var q))
                {
                    vm.ApplyQuote(q);
                }
                _allWatchlistItems.Add(vm);
            }

            RefreshGroupTabLabels();
            ApplyGroupFilter(prevSelectedId);

            UpdateCalendarNotice();
            UpdateClockDisplays(_clock.UtcNow);
        });
    }

    private void RefreshGroupTabLabels()
    {
        var total = _allWatchlistItems.Count;
        var hk = _allWatchlistItems.Count(x => x.Id.Market == Market.HK);
        var cn = _allWatchlistItems.Count(x => x.Id.Market == Market.CN);
        var us = _allWatchlistItems.Count(x => x.Id.Market == Market.US);
        var mini = _allWatchlistItems.Count(x => x.ShowInMiniWindow);

        AllTabLabel = $"全部 ({total})";
        HkTabLabel = $"港股 ({hk})";
        CnTabLabel = $"沪深 ({cn})";
        UsTabLabel = $"美股 ({us})";
        MiniTabLabel = $"小窗 ({mini})";
    }

    private void ApplyGroupFilter(InstrumentId? preserveSelectionId = null)
    {
        var targetId = preserveSelectionId ?? SelectedItem?.Id;
        var filtered = _allWatchlistItems.Where(MatchesSelectedGroup).ToList();

        WatchlistItems.Clear();
        foreach (var item in filtered)
        {
            WatchlistItems.Add(item);
        }

        IsCurrentGroupEmpty = WatchlistItems.Count == 0;
        EmptyGroupHint = SelectedGroupTab switch
        {
            WatchlistGroupTab.MiniWindow => "暂未勾选小窗标的，请在「全部」或对应市场中勾选右侧「小窗」复选框。",
            WatchlistGroupTab.HK => "当前港股分组暂无自选标的，可点击右上角「+ 添加」搜索添加。",
            WatchlistGroupTab.CN => "当前沪深分组暂无自选标的，可点击右上角「+ 添加」搜索添加。",
            WatchlistGroupTab.US => "当前美股分组暂无自选标的，可点击右上角「+ 添加」搜索添加。",
            _ => "自选列表为空，可点击右上角「+ 添加」添加港股、沪深或美股标的。"
        };

        if (targetId.HasValue)
        {
            SelectedItem = WatchlistItems.FirstOrDefault(x => x.Id == targetId.Value);
        }
        else if (SelectedItem is not null && !WatchlistItems.Contains(SelectedItem))
        {
            SelectedItem = null;
        }
    }

    private bool MatchesSelectedGroup(WatchlistItemViewModel item) => SelectedGroupTab switch
    {
        WatchlistGroupTab.HK => item.Id.Market == Market.HK,
        WatchlistGroupTab.CN => item.Id.Market == Market.CN,
        WatchlistGroupTab.US => item.Id.Market == Market.US,
        WatchlistGroupTab.MiniWindow => item.ShowInMiniWindow,
        _ => true
    };

    private void HandleMiniWindowToggled(WatchlistItemViewModel item, bool showInMini)
    {
        RefreshGroupTabLabels();
        if (SelectedGroupTab == WatchlistGroupTab.MiniWindow)
        {
            ApplyGroupFilter();
        }

        _ = _coordinator.UpdateSettingsAsync(s =>
        {
            var existing = s.Watchlist.FirstOrDefault(w => w.Id == item.Id);
            if (existing is not null)
            {
                existing.ShowInMiniWindow = showInMini;
            }
        });
    }

    private void OnQuotesUpdated(BatchQuoteResponse response)
    {
        RunOnUi(() =>
        {
            IsRefreshing = false;
            foreach (var item in _allWatchlistItems)
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
            foreach (var item in _allWatchlistItems)
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
            if (!_calendarNoticeDismissed || _calendarNoticeDismissedDate != calEval.Date)
            {
                BannerMessage = calEval.Description;
            }
        }
        else if (BannerMessage is not null && (BannerMessage.Contains("交易日历") || BannerMessage.Contains("申赎")))
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
    private void SelectGroupTab(string? tabKey)
    {
        var next = tabKey?.Trim().ToUpperInvariant() switch
        {
            "HK" => WatchlistGroupTab.HK,
            "CN" => WatchlistGroupTab.CN,
            "US" => WatchlistGroupTab.US,
            "MINI" or "MINIWINDOW" => WatchlistGroupTab.MiniWindow,
            _ => WatchlistGroupTab.All
        };

        SelectedGroupTab = next;
        IsAllTabSelected = next == WatchlistGroupTab.All;
        IsHkTabSelected = next == WatchlistGroupTab.HK;
        IsCnTabSelected = next == WatchlistGroupTab.CN;
        IsUsTabSelected = next == WatchlistGroupTab.US;
        IsMiniTabSelected = next == WatchlistGroupTab.MiniWindow;
        ApplyGroupFilter();
    }

    [RelayCommand]
    private async Task ToggleMiniWindowMembershipAsync(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;

        var next = !item.ShowInMiniWindow;
        await _coordinator.UpdateSettingsAsync(s =>
        {
            var existing = s.Watchlist.FirstOrDefault(w => w.Id == item.Id);
            if (existing is not null)
            {
                existing.ShowInMiniWindow = next;
            }
        });
    }

    [RelayCommand]
    private async Task MoveItemUpAsync(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;

        var visibleIdx = WatchlistItems.IndexOf(item);
        if (visibleIdx <= 0)
            return;

        var neighborId = WatchlistItems[visibleIdx - 1].Id;
        await _coordinator.UpdateSettingsAsync(s =>
        {
            var idx = s.Watchlist.FindIndex(w => w.Id == item.Id);
            var neighborIdx = s.Watchlist.FindIndex(w => w.Id == neighborId);
            if (idx > 0 && neighborIdx >= 0)
            {
                (s.Watchlist[neighborIdx], s.Watchlist[idx]) = (s.Watchlist[idx], s.Watchlist[neighborIdx]);
            }
        });
    }

    [RelayCommand]
    private async Task MoveItemDownAsync(WatchlistItemViewModel? item)
    {
        if (item is null)
            return;

        var visibleIdx = WatchlistItems.IndexOf(item);
        if (visibleIdx < 0 || visibleIdx >= WatchlistItems.Count - 1)
            return;

        var neighborId = WatchlistItems[visibleIdx + 1].Id;
        await _coordinator.UpdateSettingsAsync(s =>
        {
            var idx = s.Watchlist.FindIndex(w => w.Id == item.Id);
            var neighborIdx = s.Watchlist.FindIndex(w => w.Id == neighborId);
            if (idx >= 0 && neighborIdx >= 0)
            {
                (s.Watchlist[idx], s.Watchlist[neighborIdx]) = (s.Watchlist[neighborIdx], s.Watchlist[idx]);
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
    private void DismissBanner()
    {
        _calendarNoticeDismissed = true;
        _calendarNoticeDismissedDate = ChinaTimeHelper.GetChinaDate(_clock.UtcNow);
        BannerMessage = null;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished || !dispatcher.Thread.IsAlive)
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}
