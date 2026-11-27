using MarketWatch.Core.Models;

namespace MarketWatch.Core.Services;

/// <summary>
/// Evaluates price and change-percent threshold crossing alerts.
/// Enforces cooldown, re-arm hysteresis, and strict stale-data rejection.
/// </summary>
public sealed class PriceAlertEvaluator
{
    private sealed class RuleRuntimeState
    {
        public decimal? LastObservedValue { get; set; }
        public DateTimeOffset? LastObservedQuoteTime { get; set; }
        public DateTimeOffset? LastTriggeredAtUtc { get; set; }
        public bool NeedsRearm { get; set; }
    }

    private readonly Dictionary<string, RuleRuntimeState> _states = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public static bool IsQuoteFreshForAlert(Quote quote, DateTimeOffset utcNow)
    {
        if (!quote.HasPrice)
            return false;
        if (!string.IsNullOrWhiteSpace(quote.Error))
            return false;
        if (quote.DelayStatus == DelayStatus.Stale)
            return false;

        // Data must have been received recently
        if (utcNow - quote.ReceivedAt > TimeSpan.FromMinutes(3))
            return false;

        // During active trading, reject severely stale exchange timestamps
        if (quote.MarketStatus == MarketStatus.Trading && quote.QuoteTime.HasValue)
        {
            if (utcNow - quote.QuoteTime.Value > TimeSpan.FromMinutes(20))
                return false;
        }

        return true;
    }

    public IReadOnlyList<PriceAlertRecord> Evaluate(
        IReadOnlyList<PriceAlertRule> rules,
        IReadOnlyDictionary<InstrumentId, Quote> quotes,
        DateTimeOffset utcNow)
    {
        var triggered = new List<PriceAlertRecord>();

        lock (_sync)
        {
            // Prune deleted rules
            var activeIds = rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var staleId in _states.Keys.Where(id => !activeIds.Contains(id)).ToList())
            {
                _states.Remove(staleId);
            }

            foreach (var rule in rules)
            {
                if (!rule.IsEnabled)
                    continue;

                if (!quotes.TryGetValue(rule.InstrumentId, out var quote))
                    continue;

                if (!IsQuoteFreshForAlert(quote, utcNow))
                    continue;

                var currentOpt = rule.Metric switch
                {
                    AlertMetric.Price => quote.LastPrice,
                    AlertMetric.ChangePercent => quote.ChangePercent,
                    _ => null
                };

                if (!currentOpt.HasValue)
                    continue;

                var current = currentOpt.Value;
                if (!_states.TryGetValue(rule.Id, out var state))
                {
                    state = new RuleRuntimeState
                    {
                        LastObservedValue = current,
                        LastObservedQuoteTime = quote.QuoteTime,
                        NeedsRearm = IsAlreadyBeyondThreshold(rule.Direction, current, rule.Threshold)
                    };
                    _states[rule.Id] = state;
                    continue;
                }

                // Guard against out-of-order/older quotes when failing over or switching providers
                if (state.LastObservedQuoteTime.HasValue)
                {
                    if (!quote.QuoteTime.HasValue || quote.QuoteTime.Value < state.LastObservedQuoteTime.Value)
                        continue;
                }

                if (quote.QuoteTime.HasValue)
                {
                    state.LastObservedQuoteTime = quote.QuoteTime.Value;
                }

                // Update re-arm condition if previously triggered or started beyond threshold
                if (state.NeedsRearm)
                {
                    if (IsRearmed(rule.Direction, current, rule.Threshold))
                    {
                        state.NeedsRearm = false;
                    }
                    state.LastObservedValue = current;
                    continue;
                }

                var prev = state.LastObservedValue ?? current;
                var crossed = rule.Direction switch
                {
                    AlertDirection.CrossAbove => prev < rule.Threshold && current >= rule.Threshold,
                    AlertDirection.CrossBelow => prev > rule.Threshold && current <= rule.Threshold,
                    _ => false
                };

                state.LastObservedValue = current;

                if (!crossed)
                    continue;

                var cooldown = TimeSpan.FromMinutes(Math.Max(0, rule.CooldownMinutes));
                if (state.LastTriggeredAtUtc.HasValue && utcNow - state.LastTriggeredAtUtc.Value < cooldown)
                {
                    continue;
                }

                state.LastTriggeredAtUtc = utcNow;
                state.NeedsRearm = true;

                triggered.Add(new PriceAlertRecord
                {
                    RuleId = rule.Id,
                    InstrumentId = rule.InstrumentId,
                    InstrumentName = string.IsNullOrWhiteSpace(rule.InstrumentName) ? quote.Name : rule.InstrumentName,
                    TriggeredAt = ChinaTimeHelper.ToChinaTime(utcNow),
                    Metric = rule.Metric,
                    Direction = rule.Direction,
                    Threshold = rule.Threshold,
                    ActualValue = current,
                    QuoteTime = quote.QuoteTime,
                    Source = quote.Source
                });
            }
        }

        return triggered;
    }

    public void ResetRuleState(string ruleId)
    {
        lock (_sync)
        {
            _states.Remove(ruleId);
        }
    }

    private static bool IsAlreadyBeyondThreshold(AlertDirection direction, decimal current, decimal threshold) =>
        direction switch
        {
            AlertDirection.CrossAbove => current >= threshold,
            AlertDirection.CrossBelow => current <= threshold,
            _ => false
        };

    private static bool IsRearmed(AlertDirection direction, decimal current, decimal threshold) =>
        direction switch
        {
            AlertDirection.CrossAbove => current < threshold,
            AlertDirection.CrossBelow => current > threshold,
            _ => false
        };
}
