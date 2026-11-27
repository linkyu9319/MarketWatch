using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.App.Services;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class ReminderPopupViewModel : ObservableObject
{
    private readonly IClock _clock;
    private readonly AppStateCoordinator _coordinator;
    private readonly ThemeManager _themeManager;

    public ScheduledReminderEvent? ReminderEvent { get; }
    public PriceAlertRecord? PriceAlert { get; }

    [ObservableProperty] private string _title = "恒生科技基金交易提醒";
    [ObservableProperty] private string _badgeText = "交易截止提醒";
    [ObservableProperty] private string _instrumentTitle = "恒生科技指数 (HSTECH)";
    [ObservableProperty] private string _priceText = "--";
    [ObservableProperty] private string _changeText = "--";
    [ObservableProperty] private string _quoteMetaText = "行情不可用";
    [ObservableProperty] private bool _isQuoteUnavailable;
    [ObservableProperty] private string _countdownText = "";
    [ObservableProperty] private bool _isAfterCutoff;
    [ObservableProperty] private string _beijingTimeText = "";
    [ObservableProperty] private string _disclaimerText = "提示：15:00 为常见场外基金申赎截止时间，非恒科收盘时间亦非成交锁定价，请以基金合同与销售平台规则为准。";
    [ObservableProperty] private string? _extraNoticeText;
    [ObservableProperty] private bool _isStrong;
    [ObservableProperty] private bool _enableBreathingAnimation;
    [ObservableProperty] private bool _canSnooze = true;
    [ObservableProperty] private System.Windows.Media.Brush _changeBrush;

    public event Action? RequestClose;

    public ReminderPopupViewModel(
        ScheduledReminderEvent reminderEvent,
        IClock clock,
        AppStateCoordinator coordinator,
        ThemeManager themeManager)
    {
        ReminderEvent = reminderEvent;
        _clock = clock;
        _coordinator = coordinator;
        _themeManager = themeManager;
        _changeBrush = themeManager.GetDirectionBrush(null);

        Title = string.IsNullOrWhiteSpace(reminderEvent.Rule.Name) ? "恒生科技基金交易提醒" : reminderEvent.Rule.Name;
        IsStrong = reminderEvent.Rule.Strength == ReminderStrength.Strong;
        EnableBreathingAnimation = IsStrong && coordinator.Settings.EnableAnimations;

        BadgeText = reminderEvent.IsTest
            ? (IsStrong ? "静音强提醒测试" : "静音普通提醒测试")
            : reminderEvent.IsSnoozedFollowUp
                ? "稍后提醒（1分钟）"
                : reminderEvent.IsCatchUp
                    ? $"补提醒（合并：{string.Join("、", reminderEvent.MergedRuleSummary)}）"
                    : (IsStrong ? $"临近截止 · 强提醒 ({reminderEvent.ScheduledTime:HH:mm})" : $"定时提醒 ({reminderEvent.ScheduledTime:HH:mm})");

        if (!string.IsNullOrWhiteSpace(reminderEvent.CalendarWarning))
        {
            ExtraNoticeText = $"日历提醒：{reminderEvent.CalendarWarning}";
        }

        UpdateLiveState();
    }

    public ReminderPopupViewModel(
        PriceAlertRecord priceAlert,
        IClock clock,
        AppStateCoordinator coordinator,
        ThemeManager themeManager)
    {
        PriceAlert = priceAlert;
        _clock = clock;
        _coordinator = coordinator;
        _themeManager = themeManager;
        _changeBrush = themeManager.GetDirectionBrush(priceAlert.Metric == AlertMetric.ChangePercent ? priceAlert.ActualValue : null);

        Title = $"价格预警 · {priceAlert.InstrumentName}";
        BadgeText = priceAlert.Direction == AlertDirection.CrossAbove ? "上穿阈值触发" : "下穿阈值触发";
        InstrumentTitle = $"{priceAlert.InstrumentName} ({priceAlert.InstrumentId.Code})";

        var metricLabel = priceAlert.Metric == AlertMetric.Price ? "最新价" : "涨跌幅";
        var dirLabel = priceAlert.Direction == AlertDirection.CrossAbove ? "上穿" : "下穿";
        var unit = priceAlert.Metric == AlertMetric.ChangePercent ? "%" : "";

        PriceText = priceAlert.Metric == AlertMetric.ChangePercent
            ? $"{(priceAlert.ActualValue > 0 ? "+" : "")}{priceAlert.ActualValue:0.00}%"
            : $"{priceAlert.ActualValue:N2}";
        ChangeText = $"触发条件：{metricLabel} {dirLabel} {priceAlert.Threshold:0.##}{unit}";

        var qtStr = priceAlert.QuoteTime.HasValue
            ? ChinaTimeHelper.ToChinaTime(priceAlert.QuoteTime.Value).ToString("HH:mm:ss")
            : "--";
        QuoteMetaText = $"行情时间：{qtStr}  |  来源：{FormatSource(priceAlert.Source)}";
        CountdownText = $"触发时刻（北京时间）：{priceAlert.TriggeredAt:HH:mm:ss}";
        DisclaimerText = "本提醒仅为行情阈值通知，不构成投资建议，不执行任何自动买卖操作。";
        IsStrong = false;
        EnableBreathingAnimation = false;
        CanSnooze = false;
        UpdateLiveState();
    }

    public void UpdateLiveState()
    {
        var chinaNow = ChinaTimeHelper.ToChinaTime(_clock.UtcNow);
        BeijingTimeText = $"北京时间 {chinaNow:yyyy-MM-dd HH:mm:ss}";

        if (ReminderEvent is null)
            return;

        var rule = ReminderEvent.Rule;
        if (!rule.TargetInstrumentId.HasValue)
        {
            IsQuoteUnavailable = false;
            InstrumentTitle = "通用定时提醒（未关联特定标的）";
            PriceText = $"{rule.TriggerTime:HH:mm}";
            ChangeText = $"截止时间 {rule.CutoffTime:HH:mm}";
            ChangeBrush = _themeManager.GetDirectionBrush(null);
            QuoteMetaText = "纯本地北京时间定时触发 · 不依赖网络连接";
        }
        else
        {
            var targetId = rule.TargetInstrumentId.Value;
            var inst = _coordinator.Settings.Watchlist.FirstOrDefault(x => x.Id == targetId)
                       ?? DefaultPresets.CreateFullCatalog().FirstOrDefault(x => x.Id == targetId);
            var quote = _coordinator.GetLatestQuote(targetId);

            InstrumentTitle = inst is not null
                ? $"{inst.Name} ({inst.DisplayCode})"
                : targetId.Code;

            if (quote is not null && quote.HasPrice)
            {
                IsQuoteUnavailable = false;
                var p = quote.LastPrice!.Value;
                PriceText = p < 10m ? p.ToString("0.000") : p.ToString("N2");

                var chgStr = quote.Change.HasValue ? $"{(quote.Change.Value > 0 ? "+" : "")}{quote.Change.Value:0.00}" : "--";
                var pctStr = quote.ChangePercent.HasValue ? $"{(quote.ChangePercent.Value > 0 ? "+" : "")}{quote.ChangePercent.Value:0.00}%" : "--";
                ChangeText = $"{chgStr}  ({pctStr})";
                ChangeBrush = _themeManager.GetDirectionBrush(quote.ChangePercent ?? quote.Change);

                var qtStr = quote.QuoteTime.HasValue
                    ? ChinaTimeHelper.ToChinaTime(quote.QuoteTime.Value).ToString("MM-dd HH:mm:ss")
                    : "--";
                var staleSuffix = quote.DelayStatus == DelayStatus.Stale ? " · 已过期/缓存" : "";
                QuoteMetaText = $"行情时间：{qtStr}  |  来源：{FormatSource(quote.Source)}{staleSuffix}";
            }
            else
            {
                IsQuoteUnavailable = true;
                PriceText = "行情不可用";
                ChangeText = quote?.Error ?? "暂未获取到最新报价，但本地提醒照常准时弹出";
                ChangeBrush = _themeManager.GetDirectionBrush(null);
                QuoteMetaText = "离线或数据源暂不可用 · 请以交易平台实时页面为准";
            }
        }

        var cutoffDt = ChinaTimeHelper.CreateChinaDateTimeOffset(ReminderEvent.BusinessDate, rule.CutoffTime);
        var remaining = cutoffDt - chinaNow;

        if (remaining <= TimeSpan.Zero)
        {
            IsAfterCutoff = true;
            CanSnooze = false;
            CountdownText = $"已超过当日 {rule.CutoffTime:HH:mm} 截止时间（当前申请将按下一交易日确认）";
        }
        else
        {
            IsAfterCutoff = false;
            CanSnooze = true;
            CountdownText = remaining.TotalHours >= 1
                ? $"距离 {rule.CutoffTime:HH:mm} 截止还剩 {(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"距离 {rule.CutoffTime:HH:mm} 截止还剩 {remaining.Minutes:00} 分 {remaining.Seconds:00} 秒";
        }
    }

    private static string FormatSource(string? source) => source switch
    {
        "Tencent" => "腾讯财经",
        "Eastmoney" => "东方财富",
        null or "" => "未知",
        _ => source
    };

    [RelayCommand]
    private void Dismiss()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void SnoozeOneMinute()
    {
        if (ReminderEvent is not null && CanSnooze)
        {
            _coordinator.ScheduleSnooze(ReminderEvent.Rule, ReminderEvent.BusinessDate, TimeSpan.FromMinutes(1));
        }
        RequestClose?.Invoke();
    }
}
