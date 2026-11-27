namespace MarketWatch.Core.Models;

public enum Market
{
    HK,
    CN,
    US
}

public enum AssetType
{
    Index,
    Stock,
    Etf
}

public enum MarketStatus
{
    Unknown,
    Trading,
    PreMarket,
    LunchBreak,
    AfterHours,
    Closed,
    Suspended
}

public enum DelayStatus
{
    Unknown,
    NearRealtime,
    Delayed,
    EndOfDay,
    Stale
}

public enum ThemeMode
{
    Dark,
    Light
}

public enum ColorScheme
{
    ChinaRedUpGreenDown,
    InternationalGreenUpRedDown
}

public enum ReminderStrength
{
    Normal,
    Strong
}

public enum RepeatMode
{
    Weekdays,
    FundTradingDays,
    EveryDay
}

public enum ReminderTriggerStatus
{
    Triggered,
    CatchUpTriggered,
    Snoozed,
    Dismissed,
    MissedAfterCutoff
}

public enum AlertMetric
{
    Price,
    ChangePercent
}

public enum AlertDirection
{
    CrossAbove,
    CrossBelow
}

[Flags]
public enum ProviderCapabilities
{
    None = 0,
    BatchQuotes = 1 << 0,
    Search = 1 << 1,
    IntradayHk = 1 << 2,
    IntradayCn = 1 << 3,
    IntradayUs = 1 << 4
}

public enum CalendarCoverageStatus
{
    Verified,
    CustomOverride,
    UnknownFallbackToWeekdays
}
