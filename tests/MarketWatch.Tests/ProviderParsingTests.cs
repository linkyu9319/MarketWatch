using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using MarketWatch.Infrastructure.Providers;
using Xunit;

namespace MarketWatch.Tests;

public sealed class ProviderParsingTests
{
    [Fact]
    public void TencentParser_ParsesRealCapturedHstechShUsPayloadsAccurately()
    {
        var raw =
            "v_hkHSTECH=\"100~恒生科技指数~HSTECH~3417.380~3378.260~3455.730~5048261~0~0~3417.380~0~0~0~0~0~0~0~0~0~3417.380~0~0~0~0~0~0~0~0~0~0.0~2026/11/26 16:08:28~39.120~1.16~3514.600~3417.380~3417.380~0~0~0~0~~0~0~2.88~0~0~Hang Seng TECH Index~0~6001.020~3367.150~0~0~0~0~0~0~0~0~0~0~0~0~0~ZS~~~0~0~0~0~0~0~0~~0~HKD~1~\";\n" +
            "v_sh000001=\"1~上证指数~000001~3476.88~3476.47~3490.14~454062959~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~20261126161427~0.41~0.01~3497.81~3455.04~3476.88~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~ZS~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~0~CNY~\";\n" +
            "v_usQQQ=\"200~纳指100ETF-Invesco~QQQ.OQ~633.93~638.10~617.10~81996734~0~0~636.54~0~0~0~0~0~0~0~0~0~636.62~0~0~0~0~0~0~0~0~0~0~2026-11-25 16:00:02~-4.17~-0.65~634.89~607.74~USD~0~0~0~0~0~0~0~0~0~0~Invesco~0~0~0~0~0~0~0~0~0~GP-ETF\";";

        var wl = DefaultPresets.CreateDefaultWatchlist();
        var map = new Dictionary<string, List<Instrument>>(StringComparer.OrdinalIgnoreCase)
        {
            ["hkHSTECH"] = [wl[0]],
            ["sh000001"] = [wl[2]],
            ["usQQQ"] = [wl[4]]
        };

        var receivedAt = new DateTimeOffset(2026, 11, 27, 1, 5, 19, TimeSpan.Zero);
        var quotes = TencentQuoteProvider.ParseBatchPayload(raw, map, receivedAt, new TradingCalendarService());

        var hstech = quotes[DefaultPresets.HstechId];
        Assert.Equal(3417.38m, hstech.LastPrice);
        Assert.Equal(3378.26m, hstech.PreviousClose);
        Assert.Equal(39.12m, hstech.Change);
        Assert.Equal(1.16m, hstech.ChangePercent);
        Assert.Equal("HKD", hstech.Currency);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 16, 8, 28, TimeSpan.FromHours(8)), hstech.QuoteTime);

        var sh = quotes[DefaultPresets.ShCompositeId];
        Assert.Equal(3476.88m, sh.LastPrice);
        Assert.Equal("CNY", sh.Currency);

        var qqq = quotes[DefaultPresets.QqqId];
        Assert.Equal(633.93m, qqq.LastPrice);
        Assert.Equal(-0.65m, qqq.ChangePercent);
        Assert.Equal("USD", qqq.Currency);
        // US EST (Nov 25 is winter EST = UTC-5) -> 16:00:02 EST == 2026-11-26 05:00:02 CST
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 5, 0, 2, TimeSpan.FromHours(8)), ChinaTimeHelper.ToChinaTime(qqq.QuoteTime!.Value));
    }

    [Fact]
    public void EastmoneyParser_TreatsPreOpenDashesAsNull_NeverZero()
    {
        var json = """
        {
          "data": {
            "diff": [
              {"f2":"-","f3":"-","f4":"-","f12":"HSTECH","f13":124,"f14":"恒生科技指数","f15":"-","f16":"-","f17":"-","f18":3417.38,"f124":1795741504,"f292":2},
              {"f2":26046.9,"f3":-0.64,"f4":-167.27,"f12":"NDX100","f13":100,"f14":"纳斯达克100","f15":26081.65,"f16":24970.66,"f17":25309.61,"f18":26214.17,"f124":1795640400,"f292":5}
            ]
          }
        }
        """;

        var wl = DefaultPresets.CreateDefaultWatchlist();
        var map = new Dictionary<string, List<Instrument>>(StringComparer.OrdinalIgnoreCase)
        {
            ["124.HSTECH"] = [wl[0]],
            ["100.NDX100"] = [wl[3]]
        };

        var receivedAt = new DateTimeOffset(2026, 11, 27, 1, 5, 19, TimeSpan.Zero);
        var quotes = EastmoneyQuoteProvider.ParseBatchPayload(json, map, receivedAt, new TradingCalendarService());

        var hstech = quotes[DefaultPresets.HstechId];
        Assert.Null(hstech.LastPrice);
        Assert.Null(hstech.ChangePercent);
        Assert.Equal(3417.38m, hstech.PreviousClose);

        var ndx = quotes[DefaultPresets.NdxId];
        Assert.Equal(26046.9m, ndx.LastPrice);
        Assert.Equal(-0.64m, ndx.ChangePercent);
    }

    [Fact]
    public async Task Aggregator_FailsOverToSecondary_AndRetainsStaleLastGoodQuoteOnTotalOutage()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var primary = new StubQuoteProvider("Tencent", shouldFail: true, price: null);
        var backup = new StubQuoteProvider("Eastmoney", shouldFail: false, price: 3425.50m);
        var agg = new QuoteAggregatorService([primary, backup], clock, new NullLogger())
        {
            PrimaryProviderName = "Tencent"
        };

        var hstech = DefaultPresets.CreateDefaultWatchlist()[0];

        // Call 1: Primary fails, backup succeeds
        var r1 = await agg.GetQuotesAsync([hstech]);
        Assert.True(r1.AnyFreshData);
        Assert.Equal(3425.50m, r1.Quotes[hstech.Id].LastPrice);
        Assert.Equal("Eastmoney", r1.Quotes[hstech.Id].Source);
        var originalQuoteTime = r1.Quotes[hstech.Id].QuoteTime;

        // Call 2: Now BOTH providers fail -> must preserve last good quote with Stale status & original QuoteTime
        backup.ShouldFail = true;
        clock.Advance(TimeSpan.FromMinutes(2));
        var r2 = await agg.GetQuotesAsync([hstech]);
        Assert.False(r2.AnyFreshData);
        var stale = r2.Quotes[hstech.Id];
        Assert.Equal(3425.50m, stale.LastPrice);
        Assert.Equal(DelayStatus.Stale, stale.DelayStatus);
        Assert.Equal(originalQuoteTime, stale.QuoteTime); // Never faked with ReceivedAt!
        Assert.NotNull(stale.Error);

        // Call 3: While BOTH providers are in backoff (2s later), MUST NOT hammer either provider!
        var primaryCallsBefore = primary.CallCount;
        var backupCallsBefore = backup.CallCount;
        clock.Advance(TimeSpan.FromSeconds(2));
        var r3 = await agg.GetQuotesAsync([hstech]);
        Assert.Equal(primaryCallsBefore, primary.CallCount);
        Assert.Equal(backupCallsBefore, backup.CallCount);
        Assert.Contains("退避等待中", r3.OverallWarning);

        // Call 4: After backoff expires (65s later), retries cleanly
        backup.ShouldFail = false;
        clock.Advance(TimeSpan.FromSeconds(65));
        var r4 = await agg.GetQuotesAsync([hstech]);
        Assert.True(r4.AnyFreshData);
        Assert.True(backup.CallCount > backupCallsBefore);
    }

    [Fact]
    public void IntradayParsers_ParseTencentMinuteAndEastmoneyTrends2AndSearchAccurately()
    {
        var txJson = """
        {
          "code": 0,
          "data": {
            "sh000001": {
              "data": {
                "date": "20261126",
                "data": ["0930 3490.14 4730979 5408591854.20", "1130 3480.00 1000000 100", "1300 3481.50 1000500 200"]
              },
              "qt": { "sh000001": ["1","上证指数","000001","3476.88","3476.47"] }
            }
          }
        }
        """;
        var txSeries = TencentQuoteProvider.ParseIntradayPayload(DefaultPresets.ShCompositeId, "sh000001", txJson);
        Assert.True(txSeries.IsSupported);
        Assert.Equal(3, txSeries.Points.Count);
        Assert.Equal(3476.47m, txSeries.PreviousClose);
        Assert.Equal(new DateOnly(2026, 11, 26), txSeries.TradingDate);

        var emTrendsJson = """
        {
          "data": {
            "code": "NDX100",
            "preClose": 26214.17,
            "trends": [
              "2026-11-25 22:30,25309.61,25309.61,25309.61,25309.61,0,0.00,25309.610",
              "2026-11-26 05:00,26064.57,26046.90,26069.97,26033.07,318462512,0.00,25565.826"
            ]
          }
        }
        """;
        var emSeries = EastmoneyQuoteProvider.ParseIntradayPayload(DefaultPresets.NdxId, emTrendsJson);
        Assert.True(emSeries.IsSupported);
        Assert.Equal(2, emSeries.Points.Count);
        Assert.Equal(26214.17m, emSeries.PreviousClose);
        Assert.Equal(26046.90m, emSeries.Points[^1].Price);

        var emSearchJson = """
        {
          "QuotationCodeTable": {
            "Data": [
              {"Code":"HSTECH","Name":"恒生科技指数","Classify":"UniversalIndex","SecurityTypeName":"指数","MktNum":"124","QuoteID":"124.HSTECH"},
              {"Code":"NDX100","Name":"纳斯达克100","Classify":"UniversalIndex","SecurityTypeName":"指数","MktNum":"100","QuoteID":"100.NDX100"}
            ]
          }
        }
        """;
        var found = EastmoneyQuoteProvider.ParseSearchPayload(emSearchJson);
        Assert.Equal(2, found.Count);
        Assert.Equal(DefaultPresets.HstechId, found[0].Instrument.Id);
        Assert.Equal(DefaultPresets.NdxId, found[1].Instrument.Id);
        Assert.Equal("100.NDX100", found[1].Instrument.EastmoneySecIdHint);
    }

    private sealed class StubQuoteProvider : IQuoteProvider
    {
        public string Name { get; }
        public bool ShouldFail { get; set; }
        public int CallCount { get; private set; }
        private readonly decimal? _price;

        public ProviderCapabilities Capabilities => ProviderCapabilities.BatchQuotes;

        public StubQuoteProvider(string name, bool shouldFail, decimal? price)
        {
            Name = name;
            ShouldFail = shouldFail;
            _price = price;
        }

        public Task<IReadOnlyDictionary<InstrumentId, Quote>> GetQuotesAsync(IReadOnlyList<Instrument> instruments, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ShouldFail)
                throw new HttpRequestException("Simulated outage");

            IReadOnlyDictionary<InstrumentId, Quote> dict = instruments.ToDictionary(
                i => i.Id,
                i => new Quote
                {
                    InstrumentId = i.Id,
                    Name = i.Name,
                    LastPrice = _price,
                    QuoteTime = new DateTimeOffset(2026, 11, 27, 9, 35, 0, TimeSpan.FromHours(8)),
                    ReceivedAt = DateTimeOffset.UtcNow,
                    Source = Name,
                    MarketStatus = MarketStatus.Trading,
                    DelayStatus = DelayStatus.NearRealtime
                });
            return Task.FromResult(dict);
        }

        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>(Array.Empty<SearchResultItem>());

        public Task<IntradaySeries> GetIntradayAsync(Instrument instrument, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IntradaySeries { InstrumentId = instrument.Id, Source = Name, IsSupported = false });
    }
}
