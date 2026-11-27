using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MarketWatch.Core.Models;
using AppThemeMode = MarketWatch.Core.Models.ThemeMode;

namespace MarketWatch.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Visibility.Visible;
}

public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        !string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AppThemeMode.Dark => "深色主题",
        AppThemeMode.Light => "浅色主题",
        ColorScheme.ChinaRedUpGreenDown => "红涨绿跌（中国市场默认）",
        ColorScheme.InternationalGreenUpRedDown => "绿涨红跌（国际习惯）",
        RepeatMode.FundTradingDays => "基金交易日（内置日历+自定义）",
        RepeatMode.Weekdays => "仅工作日（周一至周五）",
        RepeatMode.EveryDay => "每天",
        ReminderStrength.Normal => "普通置顶提醒",
        ReminderStrength.Strong => "强提醒（呼吸边框）",
        AlertMetric.Price => "最新价",
        AlertMetric.ChangePercent => "涨跌幅 (%)",
        AlertDirection.CrossAbove => "上穿 ≥",
        AlertDirection.CrossBelow => "下穿 ≤",
        ReminderTriggerStatus.Triggered => "准时触发",
        ReminderTriggerStatus.CatchUpTriggered => "休眠/重启补提醒",
        ReminderTriggerStatus.Snoozed => "稍后提醒再弹",
        ReminderTriggerStatus.Dismissed => "已确认",
        ReminderTriggerStatus.MissedAfterCutoff => "已过截止（未弹窗）",
        string s when s == "Tencent" => "腾讯财经 (Tencent)",
        string s when s == "Eastmoney" => "东方财富 (Eastmoney)",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

