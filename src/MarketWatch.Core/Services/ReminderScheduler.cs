using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;

namespace MarketWatch.Core.Services;

public sealed record SchedulerTickResult(
    IReadOnlyList<ScheduledReminderEvent> PopupsToShow,
    IReadOnlyList<ReminderTriggerRecord> RecordsToPersist);

public sealed record NextReminderInfo(
    ReminderRule Rule,
    DateTimeOffset NextChinaTime,
    TimeSpan Remaining,
    CalendarCoverageStatus CoverageStatus,
    string? CalendarNote);

/// <summary>
/// Clock-driven independent reminder scheduler using China Standard Time (UTC+8).
/// Prevents duplicate triggers via unique keys (RuleId|BusinessDate|ScheduledTime),
/// merges missed pre-cutoff alerts into the latest valid catch-up alert,
/// and records missed post-cutoff alerts without showing misleading active trade prompts.
/// </summary>
public sealed class ReminderScheduler
{
    private readonly IClock _clock;
    private readonly ITradingCalendar _calendar;
    private readonly HashSet<string> _processedTriggerKeys = new(StringComparer.Ordinal);
    private readonly List<PendingSnooze> _pendingSnoozes = new();
    private readonly object _sync = new();

    private static readonly TimeSpan OnTimeGraceWindow = TimeSpan.FromSeconds(75);

    private sealed record PendingSnooze(
        ReminderRule Rule,
        DateOnly BusinessDate,
        DateTimeOffset DueAtUtc);

    public ReminderScheduler(IClock clock, ITradingCalendar calendar)
    {
        _clock = clock;
        _calendar = calendar;
    }

    public static string BuildTriggerKey(string ruleId, DateOnly businessDate, TimeOnly scheduledTime) =>
        $"{ruleId}|{businessDate:yyyy-MM-dd}|{scheduledTime:HH:mm}";

    public void SeedFromHistory(IEnumerable<ReminderTriggerRecord> existingRecords)
    {
        lock (_sync)
        {
            foreach (var rec in existingRecords)
            {
                if (!string.IsNullOrWhiteSpace(rec.TriggerKey))
                {
                    _processedTriggerKeys.Add(rec.TriggerKey);
                }
            }
        }
    }

    public void ScheduleSnooze(ReminderRule rule, DateOnly businessDate, TimeSpan delay)
    {
        lock (_sync)
        {
            var dueAt = _clock.UtcNow.Add(delay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : delay);
            _pendingSnoozes.Add(new PendingSnooze(rule.Clone(), businessDate, dueAt));
        }
    }

    public SchedulerTickResult EvaluateTick(IReadOnlyList<ReminderRule> rules)
    {
        lock (_sync)
        {
            var utcNow = _clock.UtcNow;
            var chinaNow = ChinaTimeHelper.ToChinaTime(utcNow);
            var today = DateOnly.FromDateTime(chinaNow.DateTime);
            var nowTime = TimeOnly.FromDateTime(chinaNow.DateTime);

            var popups = new List<ScheduledReminderEvent>();
            var records = new List<ReminderTriggerRecord>();

            // 1. Evaluate matured snoozes
            for (var i = _pendingSnoozes.Count - 1; i >= 0; i--)
            {
                var snooze = _pendingSnoozes[i];
                if (utcNow >= snooze.DueAtUtc)
                {
                    _pendingSnoozes.RemoveAt(i);
                    var isAfterCutoff = today > snooze.BusinessDate || nowTime >= snooze.Rule.CutoffTime;
                    var snoozeKey = $"{snooze.Rule.Id}|{snooze.BusinessDate:yyyy-MM-dd}|SNOOZE|{utcNow.ToUnixTimeSeconds()}";

                    popups.Add(new ScheduledReminderEvent
                    {
                        Rule = snooze.Rule,
                        TriggerKey = snoozeKey,
                        BusinessDate = snooze.BusinessDate,
                        ScheduledTime = snooze.Rule.TriggerTime,
                        IsCatchUp = false,
                        IsSnoozedFollowUp = true,
                        IsTest = false,
                        IsAfterCutoff = isAfterCutoff
                    });

                    records.Add(new ReminderTriggerRecord
                    {
                        TriggerKey = snoozeKey,
                        RuleId = snooze.Rule.Id,
                        RuleName = snooze.Rule.Name,
                        BusinessDate = snooze.BusinessDate,
                        ScheduledTime = snooze.Rule.TriggerTime,
                        TriggeredAt = chinaNow,
                        Status = ReminderTriggerStatus.Snoozed,
                        Note = isAfterCutoff ? "稍后提醒到期（已过当日截止时间）" : "稍后提醒（1分钟）再次弹出"
                    });
                }
            }

            // 2. Evaluate scheduled rules for today's Beijing business date
            var dueBeforeCutoff = new List<(ReminderRule Rule, string Key, CalendarDayEvaluation CalEval)>();

            foreach (var rule in rules)
            {
                if (!rule.IsEnabled)
                    continue;

                var calEval = _calendar.EvaluateFundTradingDay(today, rule.RepeatMode);
                if (!calEval.IsTradingDay)
                    continue;

                var triggerKey = BuildTriggerKey(rule.Id, today, rule.TriggerTime);
                if (_processedTriggerKeys.Contains(triggerKey))
                    continue;

                if (nowTime < rule.TriggerTime)
                    continue;

                if (nowTime >= rule.CutoffTime)
                {
                    // Missed after cutoff: persist record ONLY, do NOT show misleading active order popup
                    _processedTriggerKeys.Add(triggerKey);
                    records.Add(new ReminderTriggerRecord
                    {
                        TriggerKey = triggerKey,
                        RuleId = rule.Id,
                        RuleName = rule.Name,
                        BusinessDate = today,
                        ScheduledTime = rule.TriggerTime,
                        TriggeredAt = chinaNow,
                        Status = ReminderTriggerStatus.MissedAfterCutoff,
                        Note = $"已过当日截止时间 {rule.CutoffTime:HH:mm}，仅记录未触发提醒，不弹出过时下单提示"
                    });
                }
                else
                {
                    dueBeforeCutoff.Add((rule, triggerKey, calEval));
                }
            }

            // 3. Group pre-cutoff due rules by (TargetInstrumentId, CutoffTime) to merge catch-up reminders
            foreach (var group in dueBeforeCutoff.GroupBy(x => (x.Rule.TargetInstrumentId, x.Rule.CutoffTime)))
            {
                var ordered = group.OrderBy(x => x.Rule.TriggerTime).ToList();
                var latest = ordered[^1];
                var lateness = nowTime - latest.Rule.TriggerTime;
                var isCatchUp = ordered.Count > 1 || lateness > OnTimeGraceWindow;

                var effectiveRule = latest.Rule.Clone();
                if (ordered.Any(x => x.Rule.Strength == ReminderStrength.Strong))
                {
                    effectiveRule.Strength = ReminderStrength.Strong;
                }

                foreach (var item in ordered)
                {
                    _processedTriggerKeys.Add(item.Key);
                }

                var mergedTimes = ordered.Select(x => x.Rule.TriggerTime.ToString("HH:mm")).ToList();
                var calendarWarning = latest.CalEval.CoverageStatus == CalendarCoverageStatus.UnknownFallbackToWeekdays
                    ? latest.CalEval.Description
                    : null;

                popups.Add(new ScheduledReminderEvent
                {
                    Rule = effectiveRule,
                    TriggerKey = latest.Key,
                    BusinessDate = today,
                    ScheduledTime = latest.Rule.TriggerTime,
                    IsCatchUp = isCatchUp,
                    IsSnoozedFollowUp = false,
                    IsTest = false,
                    IsAfterCutoff = false,
                    MergedRuleSummary = isCatchUp ? mergedTimes : Array.Empty<string>(),
                    CalendarWarning = calendarWarning
                });

                if (!isCatchUp)
                {
                    records.Add(new ReminderTriggerRecord
                    {
                        TriggerKey = latest.Key,
                        RuleId = latest.Rule.Id,
                        RuleName = latest.Rule.Name,
                        BusinessDate = today,
                        ScheduledTime = latest.Rule.TriggerTime,
                        TriggeredAt = chinaNow,
                        Status = ReminderTriggerStatus.Triggered,
                        Note = calendarWarning
                    });
                }
                else
                {
                    var mergedText = string.Join("、", mergedTimes);
                    foreach (var item in ordered)
                    {
                        var isWinner = item.Key == latest.Key;
                        records.Add(new ReminderTriggerRecord
                        {
                            TriggerKey = item.Key,
                            RuleId = item.Rule.Id,
                            RuleName = item.Rule.Name,
                            BusinessDate = today,
                            ScheduledTime = item.Rule.TriggerTime,
                            TriggeredAt = chinaNow,
                            Status = ReminderTriggerStatus.CatchUpTriggered,
                            Note = isWinner
                                ? $"休眠/重启补提醒（合并时段：{mergedText}）"
                                : $"已合并至 {latest.Rule.TriggerTime:HH:mm} 补提醒"
                        });
                    }
                }
            }

            return new SchedulerTickResult(popups, records);
        }
    }

    public NextReminderInfo? GetNextUpcomingReminder(IReadOnlyList<ReminderRule> rules)
    {
        var enabled = rules.Where(r => r.IsEnabled).ToList();
        if (enabled.Count == 0)
            return null;

        var chinaNow = ChinaTimeHelper.ToChinaTime(_clock.UtcNow);
        var startDate = DateOnly.FromDateTime(chinaNow.DateTime);

        NextReminderInfo? best = null;

        for (var offset = 0; offset <= 14; offset++)
        {
            var date = startDate.AddDays(offset);
            foreach (var rule in enabled)
            {
                var eval = _calendar.EvaluateFundTradingDay(date, rule.RepeatMode);
                if (!eval.IsTradingDay)
                    continue;

                var candidateTime = ChinaTimeHelper.CreateChinaDateTimeOffset(date, rule.TriggerTime);
                if (candidateTime <= chinaNow)
                    continue;

                lock (_sync)
                {
                    var key = BuildTriggerKey(rule.Id, date, rule.TriggerTime);
                    if (_processedTriggerKeys.Contains(key))
                        continue;
                }

                var remaining = candidateTime - chinaNow;
                if (best is null || candidateTime < best.NextChinaTime)
                {
                    best = new NextReminderInfo(
                        rule,
                        candidateTime,
                        remaining,
                        eval.CoverageStatus,
                        eval.CoverageStatus == CalendarCoverageStatus.UnknownFallbackToWeekdays ? eval.Description : null);
                }
            }

            if (best is not null)
                break;
        }

        return best;
    }
}
