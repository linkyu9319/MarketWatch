using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;

namespace MarketWatch.Infrastructure.Calendar;

/// <summary>
/// Trading calendar supporting Weekdays (P0) and FundTradingDays (P1).
/// Embeds verified 2026 SSE/SZSE official holiday schedule (source: sse.com.cn).
/// For dates outside the verified range, explicitly marks coverage as Unknown
/// and falls back to weekday reminders rather than silently missing alerts.
/// </summary>
public sealed class TradingCalendarService : ITradingCalendar
{
    private readonly object _sync = new();
    private HashSet<DateOnly> _customHolidays = new();
    private HashSet<DateOnly> _customOpenDays = new();

    private static readonly DateOnly VerifiedStart = new(2026, 1, 1);
    private static readonly DateOnly VerifiedEnd = new(2026, 12, 31);

    /// <summary>
    /// Official 2026 mainland China stock exchange (SSE/SZSE) weekday statutory holiday closures.
    /// Source: Shanghai Stock Exchange 2026 Holiday Schedule Announcement.
    /// Note: Weekends are always closed even on mainland statutory work-makeup weekends.
    /// </summary>
    private static readonly HashSet<DateOnly> OfficialCnHolidays2026 =
    [
        // New Year's Day: Jan 1 (Thu) - Jan 3 (Sat)
        new(2026, 1, 1), new(2026, 1, 2),
        // Spring Festival: Feb 15 (Sun) - Feb 23 (Mon)
        new(2026, 2, 16), new(2026, 2, 17), new(2026, 2, 18), new(2026, 2, 19), new(2026, 2, 20), new(2026, 2, 23),
        // Qingming Festival: Apr 4 (Sat) - Apr 6 (Mon)
        new(2026, 4, 6),
        // Labor Day: May 1 (Fri) - May 5 (Tue)
        new(2026, 5, 1), new(2026, 5, 4), new(2026, 5, 5),
        // Dragon Boat Festival: Jun 19 (Fri) - Jun 21 (Sun)
        new(2026, 6, 19),
        // Mid-Autumn Festival: Sep 25 (Fri) - Sep 27 (Sun)
        new(2026, 9, 25),
        // National Day: Oct 1 (Thu) - Oct 7 (Wed)
        new(2026, 10, 1), new(2026, 10, 2), new(2026, 10, 5), new(2026, 10, 6), new(2026, 10, 7)
    ];

    public CalendarCoverageInfo CoverageInfo { get; } = new(
        VerifiedStart,
        VerifiedEnd,
        "内置 2026 年沪深交易所官方休市日历（注：港股通/QDII 基金遇港股公众假期可能暂停申赎，请以基金平台公告为准，可手动添加自定义休市日）");

    public void UpdateCustomOverrides(IEnumerable<DateOnly> customHolidays, IEnumerable<DateOnly> customOpenDays)
    {
        lock (_sync)
        {
            _customHolidays = customHolidays.ToHashSet();
            _customOpenDays = customOpenDays.ToHashSet();
        }
    }

    public CalendarDayEvaluation EvaluateFundTradingDay(DateOnly chinaDate, RepeatMode repeatMode)
    {
        if (repeatMode == RepeatMode.EveryDay)
        {
            return new CalendarDayEvaluation(chinaDate, true, CalendarCoverageStatus.Verified, "每日提醒模式");
        }

        var isWeekend = chinaDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

        if (repeatMode == RepeatMode.Weekdays)
        {
            return new CalendarDayEvaluation(
                chinaDate,
                !isWeekend,
                CalendarCoverageStatus.Verified,
                isWeekend ? "周末（工作日模式不提醒）" : "工作日模式");
        }

        // RepeatMode.FundTradingDays
        lock (_sync)
        {
            if (_customOpenDays.Contains(chinaDate))
            {
                return new CalendarDayEvaluation(chinaDate, true, CalendarCoverageStatus.CustomOverride, "用户自定义开放日");
            }
            if (_customHolidays.Contains(chinaDate))
            {
                return new CalendarDayEvaluation(chinaDate, false, CalendarCoverageStatus.CustomOverride, "用户自定义休市日");
            }
        }

        if (isWeekend)
        {
            return new CalendarDayEvaluation(chinaDate, false, CalendarCoverageStatus.Verified, "周末休市");
        }

        if (chinaDate >= VerifiedStart && chinaDate <= VerifiedEnd)
        {
            if (OfficialCnHolidays2026.Contains(chinaDate))
            {
                return new CalendarDayEvaluation(chinaDate, false, CalendarCoverageStatus.Verified, "2026 年内地交易所法定节假日休市");
            }
            return new CalendarDayEvaluation(chinaDate, true, CalendarCoverageStatus.Verified, "2026 年内地基金交易日（港股假期暂停申赎请以平台为准）");
        }

        // Outside verified coverage: NEVER silently miss reminders — fall back to weekdays with explicit warning
        return new CalendarDayEvaluation(
            chinaDate,
            true,
            CalendarCoverageStatus.UnknownFallbackToWeekdays,
            $"日期 {chinaDate:yyyy-MM-dd} 超出内置交易日历范围（仅覆盖 {VerifiedStart:yyyy-MM-dd}~{VerifiedEnd:yyyy-MM-dd}），已回退至工作日提醒");
    }

    public bool IsMarketTradingDay(Market market, DateOnly localMarketDate)
    {
        if (localMarketDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        if (market == Market.CN && localMarketDate >= VerifiedStart && localMarketDate <= VerifiedEnd)
        {
            return !OfficialCnHolidays2026.Contains(localMarketDate);
        }

        // For HK/US public holidays, avoid guessing unverified holiday schedules;
        // weekdays default to open and quote timestamps/provider status refine display.
        return true;
    }
}
