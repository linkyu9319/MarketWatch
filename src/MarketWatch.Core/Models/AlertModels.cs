namespace MarketWatch.Core.Models;

public sealed class ReminderRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "恒生科技基金交易提醒";
    public InstrumentId? TargetInstrumentId { get; set; }
    public string? TargetDisplayCode { get; set; }
    public string? TargetTencentCodeHint { get; set; }
    public string? TargetEastmoneySecIdHint { get; set; }
    public TimeOnly TriggerTime { get; set; } = new(14, 30);
    public TimeOnly CutoffTime { get; set; } = new(15, 0);
    public RepeatMode RepeatMode { get; set; } = RepeatMode.FundTradingDays;
    public ReminderStrength Strength { get; set; } = ReminderStrength.Normal;
    public bool IsEnabled { get; set; } = true;
    public bool AllowFullScreenOverlay { get; set; } = false;
    public bool AllowSound { get; set; } = false;

    public ReminderRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        TargetInstrumentId = TargetInstrumentId,
        TargetDisplayCode = TargetDisplayCode,
        TargetTencentCodeHint = TargetTencentCodeHint,
        TargetEastmoneySecIdHint = TargetEastmoneySecIdHint,
        TriggerTime = TriggerTime,
        CutoffTime = CutoffTime,
        RepeatMode = RepeatMode,
        Strength = Strength,
        IsEnabled = IsEnabled,
        AllowFullScreenOverlay = AllowFullScreenOverlay,
        AllowSound = AllowSound
    };
}

public sealed record ReminderTriggerRecord
{
    public required string TriggerKey { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public DateOnly BusinessDate { get; init; }
    public TimeOnly ScheduledTime { get; init; }
    public DateTimeOffset TriggeredAt { get; init; }
    public ReminderTriggerStatus Status { get; init; }
    public string? Note { get; init; }
}

public sealed record ScheduledReminderEvent
{
    public required ReminderRule Rule { get; init; }
    public required string TriggerKey { get; init; }
    public required DateOnly BusinessDate { get; init; }
    public required TimeOnly ScheduledTime { get; init; }
    public bool IsCatchUp { get; init; }
    public bool IsSnoozedFollowUp { get; init; }
    public bool IsTest { get; init; }
    public bool IsAfterCutoff { get; init; }
    public IReadOnlyList<string> MergedRuleSummary { get; init; } = Array.Empty<string>();
    public string? CalendarWarning { get; init; }
}

public sealed class PriceAlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public InstrumentId InstrumentId { get; set; }
    public string InstrumentName { get; set; } = string.Empty;
    public string? DisplayCode { get; set; }
    public string? TencentCodeHint { get; set; }
    public string? EastmoneySecIdHint { get; set; }
    public AlertMetric Metric { get; set; } = AlertMetric.ChangePercent;
    public AlertDirection Direction { get; set; } = AlertDirection.CrossBelow;
    public decimal Threshold { get; set; } = -2.0m;
    public int CooldownMinutes { get; set; } = 10;
    public bool IsEnabled { get; set; } = true;

    public PriceAlertRule Clone() => new()
    {
        Id = Id,
        InstrumentId = InstrumentId,
        InstrumentName = InstrumentName,
        DisplayCode = DisplayCode,
        TencentCodeHint = TencentCodeHint,
        EastmoneySecIdHint = EastmoneySecIdHint,
        Metric = Metric,
        Direction = Direction,
        Threshold = Threshold,
        CooldownMinutes = CooldownMinutes,
        IsEnabled = IsEnabled
    };
}

public sealed record PriceAlertRecord
{
    public required string RuleId { get; init; }
    public required InstrumentId InstrumentId { get; init; }
    public required string InstrumentName { get; init; }
    public DateTimeOffset TriggeredAt { get; init; }
    public AlertMetric Metric { get; init; }
    public AlertDirection Direction { get; init; }
    public decimal Threshold { get; init; }
    public decimal ActualValue { get; init; }
    public DateTimeOffset? QuoteTime { get; init; }
    public string Source { get; init; } = string.Empty;
}
