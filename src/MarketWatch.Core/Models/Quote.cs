namespace MarketWatch.Core.Models;

public sealed class Instrument
{
    public InstrumentId Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayCode { get; set; } = string.Empty;
    public string? Currency { get; set; }

    /// <summary>
    /// Optional provider-specific code hints populated by search results
    /// (e.g. Eastmoney QuoteID like "106.BABA" or Tencent symbol like "hk00700").
    /// </summary>
    public string? TencentCodeHint { get; set; }
    public string? EastmoneySecIdHint { get; set; }

    public Instrument Clone() => new()
    {
        Id = Id,
        Name = Name,
        DisplayCode = DisplayCode,
        Currency = Currency,
        TencentCodeHint = TencentCodeHint,
        EastmoneySecIdHint = EastmoneySecIdHint
    };
}

public sealed record Quote
{
    public required InstrumentId InstrumentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Currency { get; init; }
    public decimal? LastPrice { get; init; }
    public decimal? PreviousClose { get; init; }
    public decimal? Change { get; init; }
    public decimal? ChangePercent { get; init; }
    public decimal? Open { get; init; }
    public decimal? High { get; init; }
    public decimal? Low { get; init; }
    public DateTimeOffset? QuoteTime { get; init; }
    public DateTimeOffset ReceivedAt { get; init; }
    public string Source { get; init; } = string.Empty;
    public MarketStatus MarketStatus { get; init; } = MarketStatus.Unknown;
    public DelayStatus DelayStatus { get; init; } = DelayStatus.Unknown;
    public string? Error { get; init; }

    public bool HasPrice => LastPrice.HasValue;
    public bool IsHealthy => LastPrice.HasValue && string.IsNullOrWhiteSpace(Error) && DelayStatus != DelayStatus.Stale;
}

public sealed record IntradayPoint(
    DateTimeOffset Timestamp,
    decimal Price,
    decimal? AveragePrice = null,
    long? Volume = null);

public sealed record IntradaySeries
{
    public required InstrumentId InstrumentId { get; init; }
    public DateOnly? TradingDate { get; init; }
    public decimal? PreviousClose { get; init; }
    public IReadOnlyList<IntradayPoint> Points { get; init; } = Array.Empty<IntradayPoint>();
    public string Source { get; init; } = string.Empty;
    public bool IsSupported { get; init; }
    public string? StatusMessage { get; init; }
}

public sealed record SearchResultItem
{
    public required Instrument Instrument { get; init; }
    public string MarketLabel { get; init; } = string.Empty;
    public string SecurityTypeLabel { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}
