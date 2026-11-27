using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;

namespace MarketWatch.Infrastructure.Calendar;

/// <summary>
/// Trading calendar supporting Weekdays (P0) and FundTradingDays (P1).
/// Embeds verified 2026 official holiday schedules for SSE/SZSE (CN), HKEX (HK), and NYSE/NASDAQ (US).
/// For fund subscription/redemption open days (which depend on specific fund contracts &amp; sales platforms
/// and may also close on HK public holidays for QDII/HK-linked funds), non-overridden open weekdays
/// explicitly signal UnknownFallbackToWeekdays so reminders never silently assume unverified fund TA rules.
/// </summary>
public sealed class TradingCalendarService : ITradingCalendar
{
    private readonly object _sync = new();
    private HashSet<DateOnly> _customHolidays = new();
    private HashSet<DateOnly> _customOpenDays = new();

    private static readonly DateOnly VerifiedStart = new(2026, 1, 1);
    private static readonly DateOnly VerifiedEnd = new(2026, 12, 31);

    /// <summary>
    /// Official 2026 mainland China stock exchange (SSE/SZSE) weekday statutory holiday closures (source: sse.com.cn).
    /// </summary>
    private static readonly HashSet<DateOnly> OfficialCnHolidays2026 =
    [
        new(2026, 1, 1), new(2026, 1, 2),                                                                     // New Year
        new(2026, 2, 16), new(2026, 2, 17), new(2026, 2, 18), new(2026, 2, 19), new(2026, 2, 20), new(2026, 2, 23), // Spring Festival
        new(2026, 4, 6),                                                                                      // Qingming
        new(2026, 5, 1), new(2026, 5, 4), new(2026, 5, 5),                                                    // Labor Day
        new(2026, 6, 19),                                                                                     // Dragon Boat
        new(2026, 9, 25),                                                                                     // Mid-Autumn
        new(2026, 10, 1), new(2026, 10, 2), new(2026, 10, 5), new(2026, 10, 6), new(2026, 10, 7)              // National Day
    ];

    /// <summary>
    /// Official 2026 HKEX full-day public holiday closures (source: hkex.com.hk).
    /// </summary>
    private static readonly HashSet<DateOnly> OfficialHkHolidays2026 =
    [
        new(2026, 1, 1),                      // New Year's Day
        new(2026, 2, 17), new(2026, 2, 18), new(2026, 2, 19), // Lunar New Year
        new(2026, 4, 3),                      // Good Friday
        new(2026, 4, 6),                      // Day following Ching Ming
        new(2026, 4, 7),                      // Day following Easter Monday
        new(2026, 5, 1),                      // Labour Day
        new(2026, 5, 25),                     // Day following Birthday of the Buddha
        new(2026, 6, 19),                     // Tuen Ng Festival
        new(2026, 7, 1),                      // HKSAR Establishment Day
        new(2026, 10, 1),                     // National Day
        new(2026, 10, 19),                    // Day following Chung Yeung
        new(2026, 12, 25)                     // Christmas Day
    ];

    /// <summary>
    /// Official 2026 NYSE / NASDAQ full-day closures (source: nyse.com / nasdaq.com).
    /// </summary>
    private static readonly HashSet<DateOnly> OfficialUsHolidays2026 =
    [
        new(2026, 1, 1),   // New Year's Day
        new(2026, 1, 19),  // Martin Luther King Jr. Day
        new(2026, 2, 16),  // Presidents' Day
        new(2026, 4, 3),   // Good Friday
        new(2026, 5, 25),  // Memorial Day
        new(2026, 6, 19),  // Juneteenth
        new(2026, 7, 3),   // Independence Day (Observed)
        new(2026, 9, 7),   // Labor Day
        new(2026, 11, 26), // Thanksgiving Day
        new(2026, 12, 25)  // Christmas Day
    ];

    public CalendarCoverageInfo CoverageInfo { get; } = new(
        VerifiedStart,
        VerifiedEnd,
        "已内置 2026 年沪深 / 港股(HKEX) / 美股(NYSE·NASDAQ) 官方交易所休市表；因具体场外基金/销售平台的申赎开放日（尤其 QDII/港股通基金）可能另行暂停，未显式配置基金日历的工作日会显著提示并回退至工作日提醒，可在下方添加自定义休市日。");

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
                return new CalendarDayEvaluation(chinaDate, true, CalendarCoverageStatus.CustomOverride, "用户自定义基金开放日");
            }
            if (_customHolidays.Contains(chinaDate))
            {
                return new CalendarDayEvaluation(chinaDate, false, CalendarCoverageStatus.CustomOverride, "用户自定义休市/暂停申赎日");
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

            if (OfficialHkHolidays2026.Contains(chinaDate))
            {
                // HK public holiday while mainland is open: QDII / HK-linked funds often suspend subscription,
                // but platform/product rules vary — warn prominently and fall back to weekday reminder instead of silently skipping.
                return new CalendarDayEvaluation(
                    chinaDate,
                    true,
                    CalendarCoverageStatus.UnknownFallbackToWeekdays,
                    $"今日（{chinaDate:yyyy-MM-dd}）为港股公众假期休市，恒科相关基金可能暂停申赎；因未配置具体基金平台日历，已回退至工作日提醒（请以平台公告为准）");
            }

            return new CalendarDayEvaluation(
                chinaDate,
                true,
                CalendarCoverageStatus.UnknownFallbackToWeekdays,
                "当前仅核验交易所休市日，未接入具体基金/平台申赎开放日历，已回退至工作日提醒（请以基金平台公告为准）");
        }

        return new CalendarDayEvaluation(
            chinaDate,
            true,
            CalendarCoverageStatus.UnknownFallbackToWeekdays,
            $"日期 {chinaDate:yyyy-MM-dd} 超出内置 2026 年交易所休市表范围，已回退至工作日提醒");
    }

    /// <summary>
    /// Official 2026 NYSE / NASDAQ early close dates (regular trading closes at 13:00 ET).
    /// </summary>
    private static readonly HashSet<DateOnly> OfficialUsEarlyClose2026 =
    [
        new(2026, 11, 27), // Day after Thanksgiving (Black Friday)
        new(2026, 12, 24)  // Christmas Eve
    ];

    /// <summary>
    /// Official 2026 HKEX half-day trading dates (morning session only, closes by 12:08 CST).
    /// </summary>
    private static readonly HashSet<DateOnly> OfficialHkHalfDays2026 =
    [
        new(2026, 2, 16),  // Eve of Lunar New Year
        new(2026, 12, 24), // Eve of Christmas Day
        new(2026, 12, 31)  // Eve of New Year
    ];

    public TimeOnly? GetEarlyCloseTime(Market market, DateOnly localMarketDate)
    {
        if (localMarketDate < VerifiedStart || localMarketDate > VerifiedEnd)
            return null;

        return market switch
        {
            Market.US when OfficialUsEarlyClose2026.Contains(localMarketDate) => new TimeOnly(13, 0),
            Market.HK when OfficialHkHalfDays2026.Contains(localMarketDate) => new TimeOnly(12, 8),
            _ => null
        };
    }

    public bool IsMarketTradingDay(Market market, DateOnly localMarketDate)
    {
        if (localMarketDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        if (localMarketDate >= VerifiedStart && localMarketDate <= VerifiedEnd)
        {
            return market switch
            {
                Market.CN => !OfficialCnHolidays2026.Contains(localMarketDate),
                Market.HK => !OfficialHkHolidays2026.Contains(localMarketDate),
                Market.US => !OfficialUsHolidays2026.Contains(localMarketDate),
                _ => true
            };
        }

        return true;
    }
}
