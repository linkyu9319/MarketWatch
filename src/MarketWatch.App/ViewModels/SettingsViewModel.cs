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

public sealed record TargetInstrumentOption(string Key, string Label);

public sealed partial class EditableReminderRuleViewModel : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _targetInstrumentKey = "";
    [ObservableProperty] private string _triggerHour = "14";
    [ObservableProperty] private string _triggerMinute = "30";
    [ObservableProperty] private string _cutoffHour = "15";
    [ObservableProperty] private string _cutoffMinute = "00";
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.FundTradingDays;
    [ObservableProperty] private ReminderStrength _strength = ReminderStrength.Normal;
    [ObservableProperty] private bool _isEnabled = true;

    public static EditableReminderRuleViewModel FromModel(ReminderRule m) => new()
    {
        Id = m.Id,
        Name = m.Name,
        TargetInstrumentKey = m.TargetInstrumentId?.CanonicalKey ?? "",
        TriggerHour = m.TriggerTime.Hour.ToString("00"),
        TriggerMinute = m.TriggerTime.Minute.ToString("00"),
        CutoffHour = m.CutoffTime.Hour.ToString("00"),
        CutoffMinute = m.CutoffTime.Minute.ToString("00"),
        RepeatMode = m.RepeatMode,
        Strength = m.Strength,
        IsEnabled = m.IsEnabled
    };

    public ReminderRule ToModel(bool allowFullScreen, bool allowSound)
    {
        var th = int.TryParse(TriggerHour, out var h1) ? Math.Clamp(h1, 0, 23) : 14;
        var tm = int.TryParse(TriggerMinute, out var m1) ? Math.Clamp(m1, 0, 59) : 30;
        var ch = int.TryParse(CutoffHour, out var h2) ? Math.Clamp(h2, 0, 23) : 15;
        var cm = int.TryParse(CutoffMinute, out var m2) ? Math.Clamp(m2, 0, 59) : 0;
        InstrumentId? targetId = InstrumentId.TryParse(TargetInstrumentKey, out var parsedId) ? parsedId : null;

        return new ReminderRule
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "定时交易提醒" : Name.Trim(),
            TargetInstrumentId = targetId,
            TriggerTime = new TimeOnly(th, tm),
            CutoffTime = new TimeOnly(ch, cm),
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
    public ObservableCollection<TargetInstrumentOption> TargetInstrumentOptions { get; } = new();
    public ObservableCollection<string> CustomHolidayDates { get; } = new();
    public ObservableCollection<string> CustomOpenDates { get; } = new();
    public ObservableCollection<ProviderHealthSnapshot> ProviderHealthList { get; } = new();

    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
    public IReadOnlyList<string> Minutes { get; } = Enumerable.Range(0, 60).Select(m => m.ToString("00")).ToList();
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
        TargetInstrumentOptions.Clear();
        TargetInstrumentOptions.Add(new TargetInstrumentOption("", "不关联特定标的"));
        var seenOptionKeys = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (var w in s.Watchlist)
        {
            AvailableInstruments.Add(w);
            if (seenOptionKeys.Add(w.Id.CanonicalKey))
                TargetInstrumentOptions.Add(new TargetInstrumentOption(w.Id.CanonicalKey, $"{w.Name} ({w.DisplayCode})"));
        }
        // Preserve target options for existing reminder rules even if the instrument was removed from Watchlist
        var catalog = DefaultPresets.CreateFullCatalog();
        foreach (var r in s.ReminderRules.Where(x => x.TargetInstrumentId.HasValue))
        {
            var id = r.TargetInstrumentId!.Value;
            if (seenOptionKeys.Add(id.CanonicalKey))
            {
                var known = catalog.FirstOrDefault(c => c.Id == id);
                var label = known is not null ? $"{known.Name} ({known.DisplayCode}) [未在自选]" : $"{id.Code} [未在自选]";
                TargetInstrumentOptions.Add(new TargetInstrumentOption(id.CanonicalKey, label));
            }
        }
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
            Name = "",
            TargetInstrumentKey = "",
            TriggerHour = "14",
            TriggerMinute = "50",
            CutoffHour = "15",
            CutoffMinute = "00",
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
            var builtRules = ReminderRules.Select(r => r.ToModel(EnableFullScreenOverlayForStrong, EnableReminderSound)).ToList();
            foreach (var rule in builtRules)
            {
                if (rule.TriggerTime >= rule.CutoffTime)
                {
                    StatusMessage = $"提醒规则「{rule.Name}」的触发时间 ({rule.TriggerTime:HH:mm}) 必须早于截止时间 ({rule.CutoffTime:HH:mm})，请调整后再保存。";
                    return;
                }
            }

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
                s.ReminderRules = builtRules;
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
