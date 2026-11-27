using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;

namespace MarketWatch.Infrastructure.Providers;

/// <summary>
/// Coordinates primary and fallback quote providers with per-provider exponential backoff,
/// capability routing, timeout isolation, and last-known-good quote preservation on total outage.
/// </summary>
public sealed class QuoteAggregatorService : IQuoteAggregator
{
    private sealed class ProviderState
    {
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? BackoffUntil { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public string? LastError { get; set; }
    }

    private readonly IReadOnlyList<IQuoteProvider> _providers;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, ProviderState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<InstrumentId, Quote> _lastGoodQuotes = new();
    private readonly object _sync = new();

    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(8);

    public string PrimaryProviderName { get; set; } = "Eastmoney";

    public QuoteAggregatorService(
        IEnumerable<IQuoteProvider> providers,
        IClock clock,
        IAppLogger logger)
    {
        _providers = providers.ToList();
        _clock = clock;
        _logger = logger;
        foreach (var p in _providers)
        {
            _states[p.Name] = new ProviderState();
        }
    }

    public async Task<BatchQuoteResponse> GetQuotesAsync(
        IReadOnlyList<Instrument> instruments,
        CancellationToken cancellationToken = default)
    {
        if (instruments.Count == 0)
        {
            return new BatchQuoteResponse(new Dictionary<InstrumentId, Quote>(), "无自选", false, null);
        }

        var orderedProviders = GetOrderedProviders();
        var merged = new Dictionary<InstrumentId, Quote>();
        var pending = instruments.ToList();
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var skippedBackoffSeconds = new List<double>();
        var now = _clock.UtcNow;

        foreach (var provider in orderedProviders)
        {
            if (pending.Count == 0)
                break;

            if (!provider.Capabilities.HasFlag(ProviderCapabilities.BatchQuotes))
                continue;

            // Strictly respect exponential backoff even when ALL providers are in backoff;
            // only explicit manual refresh (ResetBackoff) bypasses the backoff timer.
            if (TryGetRemainingBackoffSeconds(provider.Name, now, out var remainingSec))
            {
                skippedBackoffSeconds.Add(remainingSec);
                continue;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(ProviderTimeout);

                var batch = await provider.GetQuotesAsync(pending, cts.Token).ConfigureAwait(false);
                RecordSuccess(provider.Name, _clock.UtcNow);

                var stillMissing = new List<Instrument>();
                foreach (var inst in pending)
                {
                    if (batch.TryGetValue(inst.Id, out var quote) && quote.HasPrice)
                    {
                        merged[inst.Id] = quote;
                        usedSources.Add(quote.Source);
                        lock (_sync)
                        {
                            _lastGoodQuotes[inst.Id] = quote;
                        }
                    }
                    else
                    {
                        // Keep partial non-priced quote only if no other provider gives a price later
                        if (batch.TryGetValue(inst.Id, out var partial) && !merged.ContainsKey(inst.Id))
                        {
                            merged[inst.Id] = partial;
                        }
                        stillMissing.Add(inst);
                    }
                }

                pending = stillMissing;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var msg = ex is OperationCanceledException ? "请求超时" : ex.Message;
                RecordFailure(provider.Name, _clock.UtcNow, msg);
                errors.Add($"{provider.Name}: {msg}");
                _logger.Warn("Quotes", $"Provider {provider.Name} batch quote failed: {msg}");
            }
        }

        // Preserve last known good quote with explicit stale status when all providers fail
        var anyFresh = usedSources.Count > 0;
        var currentNow = _clock.UtcNow;
        foreach (var inst in instruments)
        {
            if (merged.TryGetValue(inst.Id, out var q) && q.HasPrice)
                continue;

            Quote? cached = null;
            lock (_sync)
            {
                _lastGoodQuotes.TryGetValue(inst.Id, out cached);
            }

            if (cached is not null)
            {
                merged[inst.Id] = cached with
                {
                    ReceivedAt = currentNow,
                    DelayStatus = DelayStatus.Stale,
                    Error = errors.Count > 0
                        ? $"数据源暂时不可用（保留上次成功报价）"
                        : "当前无新成交价（保留上次成功报价）"
                };
            }
            else if (!merged.ContainsKey(inst.Id))
            {
                merged[inst.Id] = new Quote
                {
                    InstrumentId = inst.Id,
                    Name = inst.Name,
                    Currency = inst.Currency,
                    ReceivedAt = currentNow,
                    Source = "无可用源",
                    MarketStatus = MarketStatus.Unknown,
                    DelayStatus = DelayStatus.Stale,
                    Error = errors.Count > 0 ? "行情不可用（网络或数据源失败）" : "行情不可用"
                };
            }
        }

        var sourceSummary = usedSources.Count > 0
            ? string.Join(" + ", usedSources)
            : "离线/全部源不可用";
        string? warning;
        if (!anyFresh && errors.Count > 0)
        {
            warning = $"全部行情源暂时失败（{string.Join("；", errors.Take(2))}），已保留上次成功报价";
        }
        else if (!anyFresh && skippedBackoffSeconds.Count > 0)
        {
            var nextWait = Math.Max(1, (int)Math.Ceiling(skippedBackoffSeconds.Min()));
            warning = $"全部行情源退避等待中（约 {nextWait} 秒后自动重试，可点击“刷新”立即重试），已保留上次成功报价";
        }
        else if (errors.Count > 0)
        {
            warning = $"已自动切换至 {sourceSummary}";
        }
        else
        {
            warning = null;
        }

        return new BatchQuoteResponse(merged, sourceSummary, anyFresh, warning);
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResultItem>();

        // Prefer Eastmoney search first (surfaces indices like HSTECH and NDX100 cleanly), then Tencent fallback
        var searchOrder = _providers
            .Where(p => p.Capabilities.HasFlag(ProviderCapabilities.Search))
            .OrderByDescending(p => p.Name.Equals("Eastmoney", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var combined = new List<SearchResultItem>();
        var seen = new HashSet<InstrumentId>();

        foreach (var provider in searchOrder)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(ProviderTimeout);
                var found = await provider.SearchAsync(query, cts.Token).ConfigureAwait(false);
                foreach (var item in found)
                {
                    if (seen.Add(item.Instrument.Id))
                    {
                        combined.Add(item);
                    }
                }
                if (combined.Count >= 10)
                    break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Search", $"Provider {provider.Name} search failed: {ex.Message}");
            }
        }

        return combined;
    }

    public async Task<IntradaySeries> GetIntradayAsync(
        Instrument instrument,
        CancellationToken cancellationToken = default)
    {
        var cap = instrument.Id.Market switch
        {
            Market.HK => ProviderCapabilities.IntradayHk,
            Market.CN => ProviderCapabilities.IntradayCn,
            Market.US => ProviderCapabilities.IntradayUs,
            _ => ProviderCapabilities.None
        };

        // Route US intraday to Eastmoney first; HK/CN respect primary then fallback
        var ordered = instrument.Id.Market == Market.US
            ? _providers.Where(p => p.Capabilities.HasFlag(cap))
                        .OrderByDescending(p => p.Name.Equals("Eastmoney", StringComparison.OrdinalIgnoreCase))
                        .ToList()
            : GetOrderedProviders().Where(p => p.Capabilities.HasFlag(cap)).ToList();

        IntradaySeries? fallbackEmpty = null;

        foreach (var provider in ordered)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(ProviderTimeout);
                var series = await provider.GetIntradayAsync(instrument, cts.Token).ConfigureAwait(false);
                if (series.IsSupported && series.Points.Count > 1)
                {
                    return series;
                }
                fallbackEmpty ??= series;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("Intraday", $"Provider {provider.Name} intraday failed for {instrument.Id}: {ex.Message}");
            }
        }

        return fallbackEmpty ?? new IntradaySeries
        {
            InstrumentId = instrument.Id,
            Source = "无",
            IsSupported = false,
            StatusMessage = "当前数据源暂不支持该标的分时走势或网络不可用"
        };
    }

    public IReadOnlyList<ProviderHealthSnapshot> GetProviderHealthSnapshots()
    {
        lock (_sync)
        {
            return _providers.Select(p =>
            {
                var st = _states[p.Name];
                return new ProviderHealthSnapshot(
                    p.Name,
                    st.ConsecutiveFailures == 0,
                    st.ConsecutiveFailures,
                    st.BackoffUntil,
                    st.LastSuccessAt,
                    st.LastError);
            }).ToList();
        }
    }

    public void ResetBackoff()
    {
        lock (_sync)
        {
            foreach (var st in _states.Values)
            {
                st.BackoffUntil = null;
            }
        }
    }

    private List<IQuoteProvider> GetOrderedProviders() =>
        _providers
            .OrderByDescending(p => p.Name.Equals(PrimaryProviderName, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private bool TryGetRemainingBackoffSeconds(string providerName, DateTimeOffset now, out double remainingSeconds)
    {
        lock (_sync)
        {
            if (_states.TryGetValue(providerName, out var st) &&
                st.BackoffUntil.HasValue &&
                now < st.BackoffUntil.Value)
            {
                remainingSeconds = (st.BackoffUntil.Value - now).TotalSeconds;
                return true;
            }
        }
        remainingSeconds = 0;
        return false;
    }

    private void RecordSuccess(string providerName, DateTimeOffset now)
    {
        lock (_sync)
        {
            var st = _states[providerName];
            st.ConsecutiveFailures = 0;
            st.BackoffUntil = null;
            st.LastSuccessAt = now;
            st.LastError = null;
        }
    }

    private void RecordFailure(string providerName, DateTimeOffset now, string error)
    {
        lock (_sync)
        {
            var st = _states[providerName];
            st.ConsecutiveFailures++;
            var backoffSeconds = Math.Min(60, 5 * (1 << Math.Min(st.ConsecutiveFailures - 1, 4)));
            st.BackoffUntil = now.AddSeconds(backoffSeconds);
            st.LastError = error;
        }
    }
}
