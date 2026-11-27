using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using Xunit;

namespace MarketWatch.Tests;

public sealed class ReminderSchedulerTests
{
    private static DateTimeOffset Cst(int year, int month, int day, int hour, int minute, int second = 0) =>
        new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromHours(8)).ToUniversalTime();

    [Fact]
    public void DefaultSchedule_Triggers_1430_1445_1455_ExactlyOnceEach()
    {
        var clock = new FakeClock(Cst(2026, 11, 27, 14, 29, 55)); // Friday, verified 2026 trading day
        var calendar = new TradingCalendarService();
        var scheduler = new ReminderScheduler(clock, calendar);
        var rules = DefaultPresets.CreateDefaultReminderRules();

        // 14:29:55 -> nothing
        Assert.Empty(scheduler.EvaluateTick(rules).PopupsToShow);

        // 14:30:02 -> 14:30 normal fires once
        clock.UtcNow = Cst(2026, 11, 27, 14, 30, 2);
        var t1 = scheduler.EvaluateTick(rules);
        Assert.Single(t1.PopupsToShow);
        Assert.Equal(new TimeOnly(14, 30), t1.PopupsToShow[0].ScheduledTime);
        Assert.Equal(ReminderStrength.Normal, t1.PopupsToShow[0].Rule.Strength);
        Assert.False(t1.PopupsToShow[0].IsCatchUp);

        // 14:30:10 -> must NOT re-trigger
        clock.UtcNow = Cst(2026, 11, 27, 14, 30, 10);
        Assert.Empty(scheduler.EvaluateTick(rules).PopupsToShow);

        // 14:45:01 -> 14:45 normal fires once
        clock.UtcNow = Cst(2026, 11, 27, 14, 45, 1);
        var t2 = scheduler.EvaluateTick(rules);
        Assert.Single(t2.PopupsToShow);
        Assert.Equal(new TimeOnly(14, 45), t2.PopupsToShow[0].ScheduledTime);
        Assert.Equal(ReminderStrength.Normal, t2.PopupsToShow[0].Rule.Strength);

        // 14:55:00 -> 14:55 strong fires once
        clock.UtcNow = Cst(2026, 11, 27, 14, 55, 0);
        var t3 = scheduler.EvaluateTick(rules);
        Assert.Single(t3.PopupsToShow);
        Assert.Equal(new TimeOnly(14, 55), t3.PopupsToShow[0].ScheduledTime);
        Assert.Equal(ReminderStrength.Strong, t3.PopupsToShow[0].Rule.Strength);

        // 14:56:00 -> nothing left
        clock.UtcNow = Cst(2026, 11, 27, 14, 56, 0);
        Assert.Empty(scheduler.EvaluateTick(rules).PopupsToShow);
    }

    [Fact]
    public void CrossTimezone_SystemInUsPacific_StillTriggersAtBeijing1430()
    {
        var pacific = TimeZoneInfo.CreateCustomTimeZone("FakePST", TimeSpan.FromHours(-8), "FakePST", "FakePST");
        // Beijing 2026-11-27 14:30:05 == UTC 2026-11-27 06:30:05 == PST 2026-11-26 22:30:05 (previous day locally!)
        var clock = new FakeClock(Cst(2026, 11, 27, 14, 30, 5), pacific);
        var scheduler = new ReminderScheduler(clock, new TradingCalendarService());
        var rules = DefaultPresets.CreateDefaultReminderRules();

        var tick = scheduler.EvaluateTick(rules);
        Assert.Single(tick.PopupsToShow);
        Assert.Equal(new DateOnly(2026, 11, 27), tick.PopupsToShow[0].BusinessDate);
        Assert.Equal(new TimeOnly(14, 30), tick.PopupsToShow[0].ScheduledTime);
    }

    [Fact]
    public void AppRestart_SeedFromHistory_PreventsDuplicateTrigger()
    {
        var clock = new FakeClock(Cst(2026, 11, 27, 14, 32, 0));
        var calendar = new TradingCalendarService();
        var rules = DefaultPresets.CreateDefaultReminderRules();

        var firstRun = new ReminderScheduler(clock, calendar);
        var tick1 = firstRun.EvaluateTick(rules);
        Assert.Single(tick1.PopupsToShow);

        // Simulate app restart at 14:35 with persisted records loaded
        clock.UtcNow = Cst(2026, 11, 27, 14, 35, 0);
        var restarted = new ReminderScheduler(clock, calendar);
        restarted.SeedFromHistory(tick1.RecordsToPersist);

        Assert.Empty(restarted.EvaluateTick(rules).PopupsToShow);

        // Next rule 14:45 still fires normally
        clock.UtcNow = Cst(2026, 11, 27, 14, 45, 5);
        var tick2 = restarted.EvaluateTick(rules);
        Assert.Single(tick2.PopupsToShow);
        Assert.Equal(new TimeOnly(14, 45), tick2.PopupsToShow[0].ScheduledTime);
    }

    [Fact]
    public void SleepResumeBeforeCutoff_MergesMissedRulesIntoLatestWithHighestStrength()
    {
        // Laptop wakes up at 14:56:20 having missed 14:30, 14:45, and 14:55
        var clock = new FakeClock(Cst(2026, 11, 27, 14, 56, 20));
        var scheduler = new ReminderScheduler(clock, new TradingCalendarService());
        var rules = DefaultPresets.CreateDefaultReminderRules();

        var tick = scheduler.EvaluateTick(rules);

        Assert.Single(tick.PopupsToShow);
        var popup = tick.PopupsToShow[0];
        Assert.True(popup.IsCatchUp);
        Assert.Equal(new TimeOnly(14, 55), popup.ScheduledTime);
        Assert.Equal(ReminderStrength.Strong, popup.Rule.Strength);
        Assert.Equal(["14:30", "14:45", "14:55"], popup.MergedRuleSummary);
        Assert.Equal(3, tick.RecordsToPersist.Count);
        Assert.All(tick.RecordsToPersist, r => Assert.Equal(ReminderTriggerStatus.CatchUpTriggered, r.Status));

        // Subsequent ticks do not re-trigger
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Empty(scheduler.EvaluateTick(rules).PopupsToShow);
    }

    [Fact]
    public void ResumeAfterCutoff_DoesNotPopup_OnlyRecordsMissedAfterCutoff()
    {
        // Wakes up at 15:05 after 15:00 cutoff
        var clock = new FakeClock(Cst(2026, 11, 27, 15, 5, 0));
        var scheduler = new ReminderScheduler(clock, new TradingCalendarService());
        var rules = DefaultPresets.CreateDefaultReminderRules();

        var tick = scheduler.EvaluateTick(rules);

        Assert.Empty(tick.PopupsToShow);
        Assert.Equal(3, tick.RecordsToPersist.Count);
        Assert.All(tick.RecordsToPersist, r => Assert.Equal(ReminderTriggerStatus.MissedAfterCutoff, r.Status));

        // Next tick produces nothing
        clock.Advance(TimeSpan.FromSeconds(10));
        var tick2 = scheduler.EvaluateTick(rules);
        Assert.Empty(tick2.PopupsToShow);
        Assert.Empty(tick2.RecordsToPersist);
    }

    [Fact]
    public void SnoozeOneMinute_FiresBeforeCutoff_AndSuppressesPopupAfterCutoffOrNextDay()
    {
        var clock = new FakeClock(Cst(2026, 11, 27, 14, 55, 10));
        var scheduler = new ReminderScheduler(clock, new TradingCalendarService());
        var rules = DefaultPresets.CreateDefaultReminderRules();

        var initial = scheduler.EvaluateTick(rules);
        Assert.Single(initial.PopupsToShow);

        // Snooze #1 at 14:55:10 -> matures at 14:56:10 (before 15:00 cutoff) -> MUST popup
        scheduler.ScheduleSnooze(initial.PopupsToShow[0].Rule, initial.PopupsToShow[0].BusinessDate, TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Empty(scheduler.EvaluateTick(rules).PopupsToShow);

        clock.Advance(TimeSpan.FromSeconds(35)); // 14:56:15
        var snoozedOnTime = scheduler.EvaluateTick(rules);
        Assert.Single(snoozedOnTime.PopupsToShow);
        Assert.True(snoozedOnTime.PopupsToShow[0].IsSnoozedFollowUp);
        Assert.False(snoozedOnTime.PopupsToShow[0].IsAfterCutoff);

        // Snooze #2 at 14:59:20 -> matures at 15:00:25 (past cutoff!) -> MUST NOT popup, only record MissedAfterCutoff
        clock.UtcNow = Cst(2026, 11, 27, 14, 59, 20);
        scheduler.ScheduleSnooze(initial.PopupsToShow[0].Rule, initial.PopupsToShow[0].BusinessDate, TimeSpan.FromMinutes(1));
        clock.Advance(TimeSpan.FromSeconds(65)); // 15:00:25
        var snoozedPastCutoff = scheduler.EvaluateTick(rules);
        Assert.Empty(snoozedPastCutoff.PopupsToShow);
        Assert.Single(snoozedPastCutoff.RecordsToPersist);
        Assert.Equal(ReminderTriggerStatus.MissedAfterCutoff, snoozedPastCutoff.RecordsToPersist[0].Status);

        // Snooze #3 resumed next day -> MUST NOT popup stale previous-day reminder
        clock.UtcNow = Cst(2026, 11, 27, 14, 59, 30);
        scheduler.ScheduleSnooze(initial.PopupsToShow[0].Rule, new DateOnly(2026, 11, 27), TimeSpan.FromMinutes(1));
        clock.UtcNow = Cst(2026, 11, 28, 9, 0, 0);
        var nextDay = scheduler.EvaluateTick(rules);
        Assert.Empty(nextDay.PopupsToShow);
        Assert.Single(nextDay.RecordsToPersist);
        Assert.Equal(ReminderTriggerStatus.MissedAfterCutoff, nextDay.RecordsToPersist[0].Status);
    }

    [Fact]
    public void LockedSession_DefersTriggersUntilUnlock_ThenCatchesUpOrRecordsMiss()
    {
        var clock = new FakeClock(Cst(2026, 11, 27, 14, 30, 5));
        var scheduler = new ReminderScheduler(clock, new TradingCalendarService());
        var rules = DefaultPresets.CreateDefaultReminderRules();

        // While locked at 14:30 and 14:45 -> must NOT fire or mark keys triggered
        var locked1 = scheduler.EvaluateTick(rules, isSessionLocked: true);
        Assert.Empty(locked1.PopupsToShow);
        Assert.Empty(locked1.RecordsToPersist);

        clock.UtcNow = Cst(2026, 11, 27, 14, 46, 0);
        var locked2 = scheduler.EvaluateTick(rules, isSessionLocked: true);
        Assert.Empty(locked2.PopupsToShow);
        Assert.Empty(locked2.RecordsToPersist);

        // Unlock at 14:47 (before 15:00 cutoff) -> merges 14:30 and 14:45 into ONE catch-up popup!
        clock.UtcNow = Cst(2026, 11, 27, 14, 47, 0);
        var unlocked = scheduler.EvaluateTick(rules, isSessionLocked: false);
        Assert.Single(unlocked.PopupsToShow);
        Assert.True(unlocked.PopupsToShow[0].IsCatchUp);
        Assert.Equal(["14:30", "14:45"], unlocked.PopupsToShow[0].MergedRuleSummary);
    }

    [Fact]
    public void HolidayAndUnknownCalendarRange_SkipsOfficialHoliday_AndMarksUnverifiedFundCalendar()
    {
        var calendar = new TradingCalendarService();
        var rules = DefaultPresets.CreateDefaultReminderRules();

        // 2026-10-01 Thursday is National Day holiday -> must NOT fire in FundTradingDays mode
        var holidayClock = new FakeClock(Cst(2026, 10, 1, 14, 30, 5));
        var holidayScheduler = new ReminderScheduler(holidayClock, calendar);
        Assert.Empty(holidayScheduler.EvaluateTick(rules).PopupsToShow);

        // 2026-11-27 Friday -> fires reminder AND honestly flags unverified fund TA calendar
        var openEval = calendar.EvaluateFundTradingDay(new DateOnly(2026, 11, 27), RepeatMode.FundTradingDays);
        Assert.True(openEval.IsTradingDay);
        Assert.Equal(CalendarCoverageStatus.UnknownFallbackToWeekdays, openEval.CoverageStatus);

        // 2026-04-03 Good Friday (HK public holiday, CN open) -> fires with explicit HK holiday warning
        var hkHolEval = calendar.EvaluateFundTradingDay(new DateOnly(2026, 4, 3), RepeatMode.FundTradingDays);
        Assert.True(hkHolEval.IsTradingDay);
        Assert.Equal(CalendarCoverageStatus.UnknownFallbackToWeekdays, hkHolEval.CoverageStatus);
        Assert.Contains("港股公众假期", hkHolEval.Description);

        // 2026-11-28 Saturday -> must NOT fire
        var weekendClock = new FakeClock(Cst(2026, 11, 28, 14, 30, 5));
        var weekendScheduler = new ReminderScheduler(weekendClock, calendar);
        Assert.Empty(weekendScheduler.EvaluateTick(rules).PopupsToShow);

        // 2027-03-15 Monday (outside 2026 verified range) -> MUST fall back to weekday reminder with CalendarWarning
        var futureClock = new FakeClock(Cst(2027, 3, 15, 14, 30, 5));
        var futureScheduler = new ReminderScheduler(futureClock, calendar);
        var futureTick = futureScheduler.EvaluateTick(rules);
        Assert.Single(futureTick.PopupsToShow);
        Assert.NotNull(futureTick.PopupsToShow[0].CalendarWarning);
        Assert.Contains("回退至工作日", futureTick.PopupsToShow[0].CalendarWarning);
    }
}
