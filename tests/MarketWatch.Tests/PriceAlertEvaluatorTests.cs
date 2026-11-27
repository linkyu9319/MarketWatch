using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using Xunit;

namespace MarketWatch.Tests;

public sealed class PriceAlertEvaluatorTests
{
    private static Quote MakeQuote(decimal? price, decimal? pct, DateTimeOffset now, DelayStatus delay = DelayStatus.NearRealtime, string? err = null) =>
        new()
        {
            InstrumentId = DefaultPresets.HstechId,
            Name = "恒生科技指数",
            LastPrice = price,
            ChangePercent = pct,
            QuoteTime = now.AddSeconds(-5),
            ReceivedAt = now,
            Source = "Tencent",
            MarketStatus = MarketStatus.Trading,
            DelayStatus = delay,
            Error = err
        };

    [Fact]
    public void CrossBelow_TriggersOnce_RespectsRearmAndCooldown()
    {
        var eval = new PriceAlertEvaluator();
        var rule = new PriceAlertRule
        {
            Id = "r1",
            InstrumentId = DefaultPresets.HstechId,
            InstrumentName = "恒生科技指数",
            Metric = AlertMetric.ChangePercent,
            Direction = AlertDirection.CrossBelow,
            Threshold = -2.0m,
            CooldownMinutes = 5,
            IsEnabled = true
        };
        var t0 = DateTimeOffset.UtcNow;

        // Tick 1: -1.5% (baseline observation)
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3400, -1.5m, t0) }, t0));

        // Tick 2: -2.1% -> crosses below -2.0%, triggers!
        var t1 = t0.AddSeconds(10);
        var fired = eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3380, -2.1m, t1) }, t1);
        Assert.Single(fired);
        Assert.Equal(-2.1m, fired[0].ActualValue);

        // Tick 3: still -2.5% -> needs re-arm, must NOT spam
        var t2 = t1.AddSeconds(10);
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3360, -2.5m, t2) }, t2));

        // Tick 4: bounces to -1.8% (re-arms), then drops to -2.2% within 5-min cooldown -> suppressed by cooldown
        var t3 = t2.AddMinutes(1);
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3390, -1.8m, t3) }, t3));
        var t4 = t3.AddSeconds(20);
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3375, -2.2m, t4) }, t4));

        // Tick 5: after cooldown expires (6 min later), bounces to -1.7% then crosses below -2.0% -> triggers again!
        var t5 = t1.AddMinutes(6);
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3395, -1.7m, t5) }, t5));
        var t6 = t5.AddSeconds(15);
        var firedAgain = eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3370, -2.3m, t6) }, t6);
        Assert.Single(firedAgain);
    }

    [Fact]
    public void StaleOrErroredQuotes_NeverTriggerAlerts()
    {
        var eval = new PriceAlertEvaluator();
        var rule = new PriceAlertRule
        {
            Id = "r2",
            InstrumentId = DefaultPresets.HstechId,
            InstrumentName = "恒生科技指数",
            Metric = AlertMetric.Price,
            Direction = AlertDirection.CrossAbove,
            Threshold = 3500m,
            CooldownMinutes = 1,
            IsEnabled = true
        };
        var t0 = DateTimeOffset.UtcNow;

        // Baseline 3450
        eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3450, 0m, t0) }, t0);

        // Stale quote at 3550 -> MUST NOT trigger
        var t1 = t0.AddSeconds(10);
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3550, 2m, t1, DelayStatus.Stale) }, t1));

        // Errored quote at 3550 -> MUST NOT trigger
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = MakeQuote(3550, 2m, t1, DelayStatus.NearRealtime, "网络失败") }, t1));

        // Old ReceivedAt (> 3 min) -> MUST NOT trigger
        var staleReceived = MakeQuote(3550, 2m, t1.AddMinutes(-5));
        Assert.Empty(eval.Evaluate([rule], new Dictionary<InstrumentId, Quote> { [rule.InstrumentId] = staleReceived }, t1));
    }
}
