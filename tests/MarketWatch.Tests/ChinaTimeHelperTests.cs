using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using Xunit;

namespace MarketWatch.Tests;

public sealed class ChinaTimeHelperTests
{
    [Fact]
    public void UsSessionStatus_RespectsDaylightSavingTimeAndOfficialHolidays()
    {
        var calendar = new TradingCalendarService();

        // Summer EDT (UTC-4): 2026-07-15 14:00 UTC == 10:00 EDT -> Trading!
        var summerUtc = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);
        Assert.True(calendar.IsMarketTradingDay(Market.US, new DateOnly(2026, 7, 15)));
        Assert.Equal(MarketStatus.Trading, ChinaTimeHelper.ComputeMarketStatus(Market.US, summerUtc, true));

        // Winter EST (UTC-5): 2026-11-27 14:00 UTC == 09:00 EST -> PreMarket (not yet 09:30)!
        var winterUtc = new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(MarketStatus.PreMarket, ChinaTimeHelper.ComputeMarketStatus(Market.US, winterUtc, true));

        // Winter EST: 2026-11-27 14:35 UTC == 09:35 EST -> Trading!
        var winterOpenUtc = new DateTimeOffset(2026, 11, 27, 14, 35, 0, TimeSpan.Zero);
        Assert.Equal(MarketStatus.Trading, ChinaTimeHelper.ComputeMarketStatus(Market.US, winterOpenUtc, true));

        // 2026-11-26 US Thanksgiving Day at 10:00 EST (15:00 UTC) -> MUST be Closed!
        var thanksgivingUtc = new DateTimeOffset(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);
        var isUsOpen = calendar.IsMarketTradingDay(Market.US, ChinaTimeHelper.GetMarketLocalDate(Market.US, thanksgivingUtc));
        Assert.False(isUsOpen);
        Assert.Equal(MarketStatus.Closed, ChinaTimeHelper.ComputeMarketStatus(Market.US, thanksgivingUtc, isUsOpen));

        // 2026-04-03 HK Good Friday at 10:00 CST -> MUST be Closed for HK!
        var hkGoodFridayUtc = new DateTimeOffset(2026, 4, 3, 2, 0, 0, TimeSpan.Zero);
        var isHkOpen = calendar.IsMarketTradingDay(Market.HK, ChinaTimeHelper.GetMarketLocalDate(Market.HK, hkGoodFridayUtc));
        Assert.False(isHkOpen);
        Assert.Equal(MarketStatus.Closed, ChinaTimeHelper.ComputeMarketStatus(Market.HK, hkGoodFridayUtc, isHkOpen));

        // 2026-11-27 US Black Friday early close at 13:00 EST -> at 14:00 EST (19:00 UTC) must NOT be Trading!
        var blackFriday1400EtUtc = new DateTimeOffset(2026, 11, 27, 19, 0, 0, TimeSpan.Zero);
        var bfDate = ChinaTimeHelper.GetMarketLocalDate(Market.US, blackFriday1400EtUtc);
        var bfEarlyClose = calendar.GetEarlyCloseTime(Market.US, bfDate);
        Assert.Equal(new TimeOnly(13, 0), bfEarlyClose);
        Assert.Equal(MarketStatus.AfterHours, ChinaTimeHelper.ComputeMarketStatus(Market.US, blackFriday1400EtUtc, true, bfEarlyClose));

        // 2026-12-24 HK Christmas Eve half-day (CAS random close 12:08–12:10 CST):
        // at 12:08:30 CST still Trading; at 12:11:00 and 14:00 CST must be Closed!
        var hkEveClose = calendar.GetEarlyCloseTime(Market.HK, new DateOnly(2026, 12, 24));
        Assert.Equal(new TimeOnly(12, 10), hkEveClose);
        var hkEve120830CstUtc = new DateTimeOffset(2026, 12, 24, 4, 8, 30, TimeSpan.Zero);
        Assert.Equal(MarketStatus.Trading, ChinaTimeHelper.ComputeMarketStatus(Market.HK, hkEve120830CstUtc, true, hkEveClose));
        var hkEve121100CstUtc = new DateTimeOffset(2026, 12, 24, 4, 11, 0, TimeSpan.Zero);
        Assert.Equal(MarketStatus.Closed, ChinaTimeHelper.ComputeMarketStatus(Market.HK, hkEve121100CstUtc, true, hkEveClose));
        var hkEve1400CstUtc = new DateTimeOffset(2026, 12, 24, 6, 0, 0, TimeSpan.Zero);
        Assert.Equal(MarketStatus.Closed, ChinaTimeHelper.ComputeMarketStatus(Market.HK, hkEve1400CstUtc, true, hkEveClose));
    }

    [Fact]
    public void HkAndCnSessions_HandleLunchBreakAndCloseProperly()
    {
        var hkLunch = new DateTimeOffset(2026, 11, 27, 12, 30, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(MarketStatus.LunchBreak, ChinaTimeHelper.ComputeMarketStatus(Market.HK, hkLunch, true));

        var cnAfternoon = new DateTimeOffset(2026, 11, 27, 14, 55, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(MarketStatus.Trading, ChinaTimeHelper.ComputeMarketStatus(Market.CN, cnAfternoon, true));
        Assert.Equal(MarketStatus.Trading, ChinaTimeHelper.ComputeMarketStatus(Market.HK, cnAfternoon, true));

        var cnClosedHkOpen = new DateTimeOffset(2026, 11, 27, 15, 30, 0, TimeSpan.FromHours(8)).ToUniversalTime();
        Assert.Equal(MarketStatus.Closed, ChinaTimeHelper.ComputeMarketStatus(Market.CN, cnClosedHkOpen, true));
        Assert.Equal(MarketStatus.Trading, ChinaTimeHelper.ComputeMarketStatus(Market.HK, cnClosedHkOpen, true));
    }

    [Fact]
    public void InstrumentId_PreventsCollisionsBetweenIndexAndStock()
    {
        var shIndex = new InstrumentId(Market.CN, AssetType.Index, "SH000001");
        var szBank = new InstrumentId(Market.CN, AssetType.Stock, "SZ000001");
        var sameCodeStock = new InstrumentId(Market.CN, AssetType.Stock, "SH000001");

        Assert.NotEqual(shIndex, szBank);
        Assert.NotEqual(shIndex, sameCodeStock);
        Assert.Equal(shIndex, InstrumentId.Parse(shIndex.CanonicalKey));
    }
}
