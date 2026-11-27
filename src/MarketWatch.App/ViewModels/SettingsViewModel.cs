using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class EditableReminderRuleViewModel : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [ObservableProperty] private string _name = "恒生科技基金交易提醒";
    [ObservableProperty] private InstrumentId _targetInstrumentId = DefaultPresets.HstechId;
    [ObservableProperty] private string _triggerTimeText = "14:30";
    [ObservableProperty] private string _cutoffTimeText = "15:00";
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.FundTradingDays;
    [ObservableProperty] private ReminderStrength _strength = ReminderStrength.Normal;
    [ObservableProperty] private bool _isEnabled = true;

    public static EditableReminderRuleViewModel FromModel(ReminderRule m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        TargetInstrumentId = m.TargetInstrumentId ?? DefaultPresets.HstechId,
        TriggerTimeText = m.TriggerTime.ToString("HH:mm"),
        CutoffTimeText = m.CutoffTime.ToString("HH:mm"),
        RepeatMode = m.RepeatMode,
        Strength = m.Strength,
        IsEnabled = m.IsEnabled
    };

    public ReminderRule ToModel(bool allowFullScreen, bool allowSound)
    {
        var trigger = TimeOnly.TryParseExact(TriggerTimeText.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : new TimeOnly(14, 30);
        var cutoff = TimeOnly.TryParseExact(CutoffTimeText.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)
            ? c : new TimeOnly(15, 0);

        return new ReminderRule
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "恒生科技基金交易提醒" : Name.Trim(),
            TargetInstrumentId = TargetInstrumentId,
            TriggerTime = trigger,
            CutoffTime = cutoff,
            RepeatMode = RepeatMode,
            Strength = Strength,
            IsEnabled = IsEnabled,
            AllowFullScreenOverlay = allowFullScreen && Strength == ReminderStrength.Strong,
            AllowSound = allowSound
        };
    }
}

public sealed partial class EditablePriceAlertViewModel : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [ObservableProperty] private InstrumentId _instrumentId = DefaultPresets.HstechId;
    [ObservableProperty] private string _instrumentName = "恒生科技指数";
    [ObservableProperty] private AlertMetric _metric = AlertMetric.ChangePercent;
    [ObservableProperty] private AlertDirection _direction = AlertDirection.CrossBelow;
    [ObservableProperty] private decimal _threshold = -2.0m;
    [ObservableProperty] private int _cooldownMinutes = 10;
    [ObservableProperty] private bool _isEnabled = true;

    public string MetricDisplay => Metric == AlertMetric.Price ? "最新价" : "涨跌幅(%)";
    public string DirectionDisplay => Direction == AlertDirection.CrossAbove ? "上穿 ≥" : "下穿 ≤";

    public static EditablePriceAlertViewModel FromModel(PriceAlertRule m) => new()
    {
        Id = m.Id,
        InstrumentId = m.InstrumentId,
        InstrumentName = m.InstrumentName,
        Metric = m.Metric,
        Direction = m.Direction,
        Threshold = m.Threshold,
        CooldownMinutes = m.CooldownMinutes,
        IsEnabled = m.IsEnabled
    };

    public PriceAlertRule ToModel() => new()
    {
        Id = Id,
        InstrumentId = InstrumentId,
        InstrumentName = InstrumentName,
        Metric = Metric,
        Direction = Direction,
        Threshold = Threshold,
        CooldownMinutes = Math.Clamp(CooldownMinutes, 1, 1440),
        IsEnabled = IsEnabled
    };
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppStateCoordinator _coordinator;
    private readonly ThemeManager _themeManager;
    private readonly IAutoStartService _autoStartService;

    // General
    [ObservableProperty] private int _refreshIntervalSeconds = 10;
    [ObservableProperty] private ThemeMode _themeMode = ThemeMode.Dark;
    [ObservableProperty] private ColorScheme _colorScheme = ColorScheme.ChinaRedUpGreenDown;
    [ObservableProperty] private string _primaryProvider = "Eastmoney";
    [ObservableProperty] private bool _minimizeToTray = true;
    [ObservableProperty] private bool _closeToTray = true;
    [ObservableProperty] private bool _showQuoteInTrayTooltip = false;
    [ObservableProperty] private bool _enableAnimations = true;
    [ObservableProperty] private bool _enableReminderSound = false;
    [ObservableProperty] private bool _enableFullScreenOverlayForStrong = false;
    [ObservableProperty] private bool _autoStartEnabled = false;
    [ObservableProperty] private RepeatMode _defaultCalendarMode = RepeatMode.FundTradingDays;
    [ObservableProperty] private double _miniWindowOpacity = 0.94;

    // Calendar inputs
    [ObservableProperty] private string _newHolidayDateText = "";
    [ObservableProperty] private string _newOpenDateText = "";
    [ObservableProperty] private string _calendarCoverageText = "";
    [ObservableProperty] private string _todayCalendarStatusText = "";

    // New Price Alert inputs
    [ObservableProperty] private Instrument? _newAlertInstrument;
    [ObservableProperty] private AlertMetric _newAlertMetric = AlertMetric.ChangePercent;
    [ObservableProperty] private AlertDirection _newAlertDirection = AlertDirection.CrossBelow;
    [ObservableProperty] private string _newAlertThresholdText = "-2.0";
    [ObservableProperty] private int _newAlertCooldown = 10;

    // Status
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _configPathText = "";

    public ObservableCollection<EditableReminderRuleViewModel> ReminderRules { get; } = new();
    public ObservableCollection<EditablePriceAlertViewModel> PriceAlerts { get; } = new();
    public ObservableCollection<Instrument> AvailableInstruments { get; } = new();
    public ObservableCollection<string> CustomHolidayDates { get; } = new();
    public ObservableCollection<string> CustomOpenDates { get; } = new();
    public ObservableCollection<ProviderHealthSnapshot> ProviderHealthList { get; } = new();

    public IReadOnlyList<ThemeMode> ThemeModes { get; } = [ThemeMode.Dark, ThemeMode.Light];
    public IReadOnlyList<ColorScheme> ColorSchemes { get; } = [ColorScheme.ChinaRedUpGreenDown, ColorScheme.InternationalGreenUpRedDown];
    public IReadOnlyList<string> Providers { get; } = ["Eastmoney", "Tencent"];
    public IReadOnlyList<RepeatMode> RepeatModes { get; } = [RepeatMode.FundTradingDays, RepeatMode.Weekdays, RepeatMode.EveryDay];
    public IReadOnlyList<ReminderStrength> Strengths { get; } = [ReminderStrength.Normal, ReminderStrength.Strong];
    public IReadOnlyList<AlertMetric> AlertMetrics { get; } = [AlertMetric.Price, AlertMetric.ChangePercent];
    public IReadOnlyList<AlertDirection> AlertDirections { get; } = [AlertDirection.CrossAbove, AlertDirection.CrossBelow];

    public event Action? CloseRequested;
    public event Action<ReminderStrength>? TestReminderRequested;
    public event Action? OpenUpdatesRequested;

    public SettingsViewModel(
        AppStateCoordinator coordinator,
        ThemeManager themeManager,
        IAutoStartService autoStartService)
    {
        _coordinator = coordinator;
        _themeManager = themeManager;
        _autoStartService = autoStartService;
        LoadFromCurrentSettings();
    }

    public void LoadFromCurrentSettings()
    {
        var s = _coordinator.Settings;
        RefreshIntervalSeconds = s.RefreshIntervalSeconds;
        ThemeMode = s.ThemeMode;
        ColorScheme = s.ColorScheme;
        PrimaryProvider = s.PrimaryProvider;
        MinimizeToTray = s.MinimizeToTray;
        CloseToTray = s.CloseToTray;
        ShowQuoteInTrayTooltip = s.ShowQuoteInTrayTooltip;
        EnableAnimations = s.EnableAnimations;
        EnableReminderSound = s.EnableReminderSound;
        EnableFullScreenOverlayForStrong = s.EnableFullScreenOverlayForStrong;
        AutoStartEnabled = _autoStartService.IsEnabled();
        DefaultCalendarMode = s.DefaultCalendarMode;
        MiniWindowOpacity = s.MiniWindowOpacity;

        ConfigPathText = _coordinator.SettingsFilePath;

        AvailableInstruments.Clear();
        foreach (var w in s.Watchlist)
            AvailableInstruments.Add(w);
        NewAlertInstrument = AvailableInstruments.FirstOrDefault();

        ReminderRules.Clear();
        foreach (var r in s.ReminderRules)
            ReminderRules.Add(EditableReminderRuleViewModel.FromModel(r));

        PriceAlerts.Clear();
        foreach (var a in s.PriceAlerts)
            PriceAlerts.Add(EditablePriceAlertViewModel.FromModel(a));

        CustomHolidayDates.Clear();
        foreach (var d in s.CustomHolidayDates.OrderBy(x => x))
            CustomHolidayDates.Add(d.ToString("yyyy-MM-dd"));

        CustomOpenDates.Clear();
        foreach (var d in s.CustomOpenDates.OrderBy(x => x))
            CustomOpenDates.Add(d.ToString("yyyy-MM-dd"));

        var cov = _coordinator.CalendarCoverage;
        CalendarCoverageText = $"验证覆盖区间：{cov.VerifiedFrom:yyyy-MM-dd} 至 {cov.VerifiedTo:yyyy-MM-dd}\n{cov.SourceNote}";
        var todayEval = _coordinator.EvaluateTodayCalendar();
        TodayCalendarStatusText = $"今日（{todayEval.Date:yyyy-MM-dd}）：{(todayEval.IsTradingDay ? "开放/执行提醒" : "休市/跳过提醒")} — {todayEval.Description}";

        RefreshProviderHealth();
    }

    [RelayCommand]
    private void RefreshProviderHealth()
    {
        ProviderHealthList.Clear();
        foreach (var h in _coordinator.GetProviderHealth())
            ProviderHealthList.Add(h);
    }

    [RelayCommand]
    private void AddReminderRule()
    {
        ReminderRules.Add(new EditableReminderRuleViewModel
        {
            Name = "恒生科技基金交易提醒",
            TargetInstrumentId = AvailableInstruments.FirstOrDefault()?.Id ?? DefaultPresets.HstechId,
            TriggerTimeText = "14:50",
            CutoffTimeText = "15:00",
            RepeatMode = DefaultCalendarMode,
            Strength = ReminderStrength.Normal,
            IsEnabled = true
        });
    }

    [RelayCommand]
    private void RemoveReminderRule(EditableReminderRuleViewModel? rule)
    {
        if (rule is not null)
            ReminderRules.Remove(rule);
    }

    [RelayCommand]
    private void AddPriceAlert()
    {
        if (NewAlertInstrument is null)
        {
            StatusMessage = "请先选择一个自选标的。";
            return;
        }
        if (!decimal.TryParse(NewAlertThresholdText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            StatusMessage = "阈值格式无效，请输入有效数字（如 3500 或 -2.5）。";
            return;
        }

        PriceAlerts.Add(new EditablePriceAlertViewModel
        {
            InstrumentId = NewAlertInstrument.Id,
            InstrumentName = NewAlertInstrument.Name,
            Metric = NewAlertMetric,
            Direction = NewAlertDirection,
            Threshold = threshold,
            CooldownMinutes = Math.Clamp(NewAlertCooldown, 1, 1440),
            IsEnabled = true
        });
        StatusMessage = $"已添加预警：{NewAlertInstrument.Name}（点击保存后生效）";
    }

    [RelayCommand]
    private void RemovePriceAlert(EditablePriceAlertViewModel? rule)
    {
        if (rule is not null)
        {
            PriceAlerts.Remove(rule);
            _coordinator.ResetPriceAlertState(rule.Id);
        }
    }

    [RelayCommand]
    private void AddCustomHoliday()
    {
        if (DateOnly.TryParseExact(NewHolidayDateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            var s = d.ToString("yyyy-MM-dd");
            if (!CustomHolidayDates.Contains(s))
                CustomHolidayDates.Add(s);
            NewHolidayDateText = "";
            StatusMessage = $"已添加自定义休市日 {s}";
        }
        else
        {
            StatusMessage = "日期格式请使用 yyyy-MM-dd（例如 2026-12-25）。";
        }
    }

    [RelayCommand]
    private void RemoveCustomHoliday(string? dateStr)
    {
        if (!string.IsNullOrWhiteSpace(dateStr))
            CustomHolidayDates.Remove(dateStr);
    }

    [RelayCommand]
    private void AddCustomOpenDay()
    {
        if (DateOnly.TryParseExact(NewOpenDateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            var s = d.ToString("yyyy-MM-dd");
            if (!CustomOpenDates.Contains(s))
                CustomOpenDates.Add(s);
            NewOpenDateText = "";
            StatusMessage = $"已添加自定义开放日 {s}";
        }
        else
        {
            StatusMessage = "日期格式请使用 yyyy-MM-dd。";
        }
    }

    [RelayCommand]
    private void RemoveCustomOpenDay(string? dateStr)
    {
        if (!string.IsNullOrWhiteSpace(dateStr))
            CustomOpenDates.Remove(dateStr);
    }

    [RelayCommand]
    private void TestNormalReminder() => TestReminderRequested?.Invoke(ReminderStrength.Normal);

    [RelayCommand]
    private void TestStrongReminder() => TestReminderRequested?.Invoke(ReminderStrength.Strong);

    [RelayCommand]
    private void OpenUpdates() => OpenUpdatesRequested?.Invoke();

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var dir = Path.GetDirectoryName(_coordinator.SettingsFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task ExportConfigAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 MarketWatch 配置",
            Filter = "JSON 配置文件 (*.json)|*.json",
            FileName = $"MarketWatch-config-{DateTime.Now:yyyyMMdd}.json"
        };
        if (dlg.ShowDialog() == true)
        {
            await _coordinator.ExportSettingsAsync(dlg.FileName);
            StatusMessage = $"已导出配置至：{dlg.FileName}";
        }
    }

    [RelayCommand]
    private async Task ImportConfigAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 MarketWatch 配置",
            Filter = "JSON 配置文件 (*.json)|*.json"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                await _coordinator.ImportSettingsAsync(dlg.FileName);
                LoadFromCurrentSettings();
                _themeManager.Apply(ThemeMode, ColorScheme);
                StatusMessage = "配置导入成功并已立即生效。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"导入失败：{ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var holidays = CustomHolidayDates
                .Select(x => DateOnly.ParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList();
            var openDays = CustomOpenDates
                .Select(x => DateOnly.ParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList();

            var prevProvider = _coordinator.Settings.PrimaryProvider;

            await _coordinator.UpdateSettingsAsync(s =>
            {
                s.RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 5, 60);
                s.ThemeMode = ThemeMode;
                s.ColorScheme = ColorScheme;
                s.PrimaryProvider = PrimaryProvider;
                s.MinimizeToTray = MinimizeToTray;
                s.CloseToTray = CloseToTray;
                s.ShowQuoteInTrayTooltip = ShowQuoteInTrayTooltip;
                s.EnableAnimations = EnableAnimations;
                s.EnableReminderSound = EnableReminderSound;
                s.EnableFullScreenOverlayForStrong = EnableFullScreenOverlayForStrong;
                s.AutoStartEnabled = AutoStartEnabled;
                s.DefaultCalendarMode = DefaultCalendarMode;
                s.MiniWindowOpacity = Math.Clamp(MiniWindowOpacity, 0.55, 1.0);
                s.ReminderRules = ReminderRules.Select(r => r.ToModel(EnableFullScreenOverlayForStrong, EnableReminderSound)).ToList();
                s.PriceAlerts = PriceAlerts.Select(a => a.ToModel()).ToList();
                s.CustomHolidayDates = holidays;
                s.CustomOpenDates = openDays;
            }, triggerImmediateRefresh: !string.Equals(prevProvider, PrimaryProvider, StringComparison.OrdinalIgnoreCase));

            _autoStartService.SetEnabled(AutoStartEnabled);
            _themeManager.Apply(ThemeMode, ColorScheme);

            StatusMessage = "设置已保存。";
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();
}
