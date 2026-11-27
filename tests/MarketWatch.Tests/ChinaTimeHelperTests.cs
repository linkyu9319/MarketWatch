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
