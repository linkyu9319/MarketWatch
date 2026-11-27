using MarketWatch.Core.Models;

namespace MarketWatch.Core.Services;

public static class ChinaTimeHelper
{
    public static readonly TimeZoneInfo ChinaTimeZone = ResolveTimeZone(
        ["China Standard Time", "Asia/Shanghai"],
        TimeSpan.FromHours(8),
        "China Standard Time");

    public static readonly TimeZoneInfo UsEasternTimeZone = ResolveTimeZone(
        ["Eastern Standard Time", "America/New_York"],
        TimeSpan.FromHours(-5),
        "Eastern Standard Time");

    private static TimeZoneInfo ResolveTimeZone(string[] candidateIds, TimeSpan fallbackOffset, string fallbackName)
    {
        foreach (var id in candidateIds)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone(fallbackName, fallbackOffset, fallbackName, fallbackName);
    }

    public static DateTimeOffset ToChinaTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, ChinaTimeZone);

    public static DateTimeOffset ToUsEasternTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, UsEasternTimeZone);

    public static DateOnly GetChinaDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(ToChinaTime(instant).DateTime);

    public static TimeOnly GetChinaTimeOfDay(DateTimeOffset instant) =>
        TimeOnly.FromDateTime(ToChinaTime(instant).DateTime);

    public static DateTimeOffset CreateChinaDateTimeOffset(DateOnly date, TimeOnly time)
    {
        var dt = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, ChinaTimeZone.GetUtcOffset(dt));
    }

    public static DateTimeOffset CreateUsEasternDateTimeOffset(DateOnly date, TimeOnly time)
    {
        var dt = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(dt, UsEasternTimeZone.GetUtcOffset(dt));
    }

    public static DateOnly GetMarketLocalDate(Market market, DateTimeOffset utcNow) =>
        market switch
        {
            Market.US => DateOnly.FromDateTime(ToUsEasternTime(utcNow).DateTime),
            _ => GetChinaDate(utcNow)
        };

    /// <summary>
    /// Computes standard exchange session status using the market's local timezone (including US DST and official early closures).
    /// </summary>
    public static MarketStatus ComputeMarketStatus(
        Market market,
        DateTimeOffset utcNow,
        bool isMarketTradingDay,
        TimeOnly? earlyCloseTime = null)
    {
        if (!isMarketTradingDay)
            return MarketStatus.Closed;

        if (market == Market.US)
        {
            var et = ToUsEasternTime(utcNow);
            var t = TimeOnly.FromDateTime(et.DateTime);
            var regularClose = earlyCloseTime ?? new TimeOnly(16, 0);
            var afterHoursEnd = earlyCloseTime.HasValue ? regularClose.AddHours(4) : new TimeOnly(20, 0);

            if (t >= new TimeOnly(4, 0) && t < new TimeOnly(9, 30))
                return MarketStatus.PreMarket;
            if (t >= new TimeOnly(9, 30) && t <= regularClose)
                return MarketStatus.Trading;
            if (t > regularClose && t <= afterHoursEnd)
                return MarketStatus.AfterHours;
            return MarketStatus.Closed;
        }

        var cst = ToChinaTime(utcNow);
        var ct = TimeOnly.FromDateTime(cst.DateTime);

        if (market == Market.HK)
        {
            if (ct >= new TimeOnly(9, 0) && ct < new TimeOnly(9, 30))
                return MarketStatus.PreMarket;

            // Half-day eves (e.g. LNY/Christmas/NY Eve): morning session only, closed after 12:08
            if (earlyCloseTime.HasValue)
            {
                if (ct >= new TimeOnly(9, 30) && ct <= earlyCloseTime.Value)
                    return MarketStatus.Trading;
                return MarketStatus.Closed;
            }

            if (ct >= new TimeOnly(9, 30) && ct < new TimeOnly(12, 0))
                return MarketStatus.Trading;
            if (ct >= new TimeOnly(12, 0) && ct < new TimeOnly(13, 0))
                return MarketStatus.LunchBreak;
            if (ct >= new TimeOnly(13, 0) && ct <= new TimeOnly(16, 10))
                return MarketStatus.Trading;
            return MarketStatus.Closed;
        }

        // CN A-share
        if (ct >= new TimeOnly(9, 15) && ct < new TimeOnly(9, 30))
            return MarketStatus.PreMarket;
        if (ct >= new TimeOnly(9, 30) && ct <= new TimeOnly(11, 30))
            return MarketStatus.Trading;
        if (ct > new TimeOnly(11, 30) && ct < new TimeOnly(13, 0))
            return MarketStatus.LunchBreak;
        if (ct >= new TimeOnly(13, 0) && ct <= new TimeOnly(15, 0))
            return MarketStatus.Trading;
        return MarketStatus.Closed;
    }

    public static string GetDefaultCurrency(Market market) => market switch
    {
        Market.HK => "HKD",
        Market.CN => "CNY",
        Market.US => "USD",
        _ => ""
    };
}
