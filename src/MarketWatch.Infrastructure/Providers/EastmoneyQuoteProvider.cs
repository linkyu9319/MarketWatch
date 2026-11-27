using System.Globalization;
using System.Text.Json;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.Infrastructure.Providers;

/// <summary>
/// Eastmoney public market data adapter (push2.eastmoney.com & searchapi.eastmoney.com).
/// Provides UTF-8 JSON batch quotes, cross-market search, and full minute intraday curves (including US).
/// Strictly treats "-" as null and never substitutes 0 for missing prices.
/// </summary>
public sealed class EastmoneyQuoteProvider : IQuoteProvider
{
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly ITradingCalendar _calendar;

    private const string QuoteFields = "f1,f2,f3,f4,f12,f13,f14,f15,f16,f17,f18,f124,f152,f292";
    private const string SearchToken = "D43BF722C8E33BDC906FB84D85E326E8";

    public EastmoneyQuoteProvider(HttpClient httpClient, IClock clock, ITradingCalendar calendar)
    {
        _httpClient = httpClient;
        _clock = clock;
        _calendar = calendar;
    }

    public string Name => "Eastmoney";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.BatchQuotes |
        ProviderCapabilities.Search |
        ProviderCapabilities.IntradayHk |
        ProviderCapabilities.IntradayCn |
        ProviderCapabilities.IntradayUs;

    public static string? ResolveEastmoneySecId(Instrument instrument)
    {
        if (!string.IsNullOrWhiteSpace(instrument.EastmoneySecIdHint))
            return instrument.EastmoneySecIdHint.Trim();

        var code = instrument.Id.Code.Trim().ToUpperInvariant();
        return instrument.Id.Market switch
        {
            Market.HK => ResolveHkSecId(instrument.Id.AssetType, code),
            Market.CN => ResolveCnSecId(code),
            Market.US => ResolveUsSecId(instrument.Id.AssetType, code),
            _ => null
        };
    }

    private static string ResolveHkSecId(AssetType assetType, string code)
    {
        if (code == "HSTECH")
            return "124.HSTECH";
        if (assetType == AssetType.Index)
            return $"100.{code}";
        return $"116.{code}";
    }

    private static string ResolveCnSecId(string code)
    {
        if (code.StartsWith("SH", StringComparison.OrdinalIgnoreCase))
            return $"1.{code[2..]}";
        if (code.StartsWith("SZ", StringComparison.OrdinalIgnoreCase))
            return $"0.{code[2..]}";
        return code.StartsWith('6') || code.StartsWith('5') ? $"1.{code}" : $"0.{code}";
    }

    private static string ResolveUsSecId(AssetType assetType, string code)
    {
        if (assetType == AssetType.Index)
        {
            return code switch
            {
                "NDX" or "NDX100" => "100.NDX100",
                "SPX" or "INX" => "100.SPX",
                "DJI" or "DJIA" => "100.DJIA",
                "IXIC" => "100.NDX",
                _ => $"100.{code}"
            };
        }
        return $"105.{code}";
    }

    public async Task<IReadOnlyDictionary<InstrumentId, Quote>> GetQuotesAsync(
        IReadOnlyList<Instrument> instruments,
        CancellationToken cancellationToken = default)
    {
        var secIdToInstruments = new Dictionary<string, List<Instrument>>(StringComparer.OrdinalIgnoreCase);
        var secIds = new List<string>();

        foreach (var inst in instruments)
        {
            var secId = ResolveEastmoneySecId(inst);
            if (string.IsNullOrWhiteSpace(secId))
                continue;

            if (!secIdToInstruments.TryGetValue(secId, out var list))
            {
                list = new List<Instrument>();
                secIdToInstruments[secId] = list;
                secIds.Add(secId);
            }
            list.Add(inst);
        }

        if (secIds.Count == 0)
            return new Dictionary<InstrumentId, Quote>();

        var url = $"https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2&fields={QuoteFields}&secids={Uri.EscapeDataString(string.Join(',', secIds))}";
        using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var receivedAt = _clock.UtcNow;

        return ParseBatchPayload(json, secIdToInstruments, receivedAt, _calendar);
    }

    public static IReadOnlyDictionary<InstrumentId, Quote> ParseBatchPayload(
        string json,
        IReadOnlyDictionary<string, List<Instrument>> secIdToInstruments,
        DateTimeOffset receivedAt,
        ITradingCalendar calendar)
    {
        var result = new Dictionary<InstrumentId, Quote>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("data", out var dataNode) ||
            dataNode.ValueKind != JsonValueKind.Object ||
            !dataNode.TryGetProperty("diff", out var diffNode) ||
            diffNode.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in diffNode.EnumerateArray())
        {
            var f13 = ReadStringOrNumber(item, "f13");
            var f12 = ReadStringOrNumber(item, "f12");
            if (string.IsNullOrWhiteSpace(f13) || string.IsNullOrWhiteSpace(f12))
                continue;

            var secId = $"{f13}.{f12}";
            if (!secIdToInstruments.TryGetValue(secId, out var matchedInstruments))
                continue;

            var nameRaw = ReadStringOrNumber(item, "f14");
            var lastPrice = ReadNullablePositiveDecimal(item, "f2");
            var changePct = ReadNullableDecimal(item, "f3");
            var change = ReadNullableDecimal(item, "f4");
            var high = ReadNullablePositiveDecimal(item, "f15");
            var low = ReadNullablePositiveDecimal(item, "f16");
            var open = ReadNullablePositiveDecimal(item, "f17");
            var prevClose = ReadNullablePositiveDecimal(item, "f18");
            var unixSec = ReadNullableLong(item, "f124");

            foreach (var inst in matchedInstruments)
            {
                var quoteTime = unixSec.HasValue && unixSec.Value > 0
                    ? (inst.Id.Market == Market.US
                        ? ChinaTimeHelper.ToUsEasternTime(DateTimeOffset.FromUnixTimeSeconds(unixSec.Value))
                        : ChinaTimeHelper.ToChinaTime(DateTimeOffset.FromUnixTimeSeconds(unixSec.Value)))
                    : (DateTimeOffset?)null;

                var localDate = ChinaTimeHelper.GetMarketLocalDate(inst.Id.Market, receivedAt);
                var isTradingDay = calendar.IsMarketTradingDay(inst.Id.Market, localDate);
                var marketStatus = ChinaTimeHelper.ComputeMarketStatus(inst.Id.Market, receivedAt, isTradingDay);
                var delayStatus = lastPrice.HasValue
                    ? TencentQuoteProvider.InferDelayStatus(marketStatus, quoteTime, receivedAt)
                    : DelayStatus.Unknown;

                result[inst.Id] = new Quote
                {
                    InstrumentId = inst.Id,
                    Name = string.IsNullOrWhiteSpace(nameRaw) ? inst.Name : nameRaw.Trim(),
                    Currency = inst.Currency ?? ChinaTimeHelper.GetDefaultCurrency(inst.Id.Market),
                    LastPrice = lastPrice,
                    PreviousClose = prevClose,
                    Change = change,
                    ChangePercent = changePct,
                    Open = open,
                    High = high,
                    Low = low,
                    QuoteTime = quoteTime,
                    ReceivedAt = receivedAt,
                    Source = "Eastmoney",
                    MarketStatus = marketStatus,
                    DelayStatus = delayStatus,
                    Error = lastPrice.HasValue ? null : "盘前或暂未产出最新成交价"
                };
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResultItem>();

        var url = $"https://searchapi.eastmoney.com/api/suggest/get?input={Uri.EscapeDataString(query.Trim())}&type=14&token={SearchToken}&count=15";
        using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseSearchPayload(json);
    }

    public static IReadOnlyList<SearchResultItem> ParseSearchPayload(string json)
    {
        var list = new List<SearchResultItem>();
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("QuotationCodeTable", out var table) ||
            !table.TryGetProperty("Data", out var dataArr) ||
            dataArr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in dataArr.EnumerateArray())
        {
            var code = ReadStringOrNumber(item, "Code")?.Trim().ToUpperInvariant();
            var name = ReadStringOrNumber(item, "Name")?.Trim();
            var mktNum = ReadStringOrNumber(item, "MktNum")?.Trim();
            var quoteId = ReadStringOrNumber(item, "QuoteID")?.Trim();
            var classify = ReadStringOrNumber(item, "Classify")?.Trim() ?? "";
            var secTypeName = ReadStringOrNumber(item, "SecurityTypeName")?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mktNum))
                continue;

            var mapped = MapEastmoneySearchItem(code, name, mktNum, quoteId ?? $"{mktNum}.{code}", classify, secTypeName);
            if (mapped is not null)
                list.Add(mapped);
        }

        return list;
    }

    private static SearchResultItem? MapEastmoneySearchItem(
        string code,
        string name,
        string mktNum,
        string quoteId,
        string classify,
        string secTypeName)
    {
        var isFundOrEtf = classify.Equals("Fund", StringComparison.OrdinalIgnoreCase) ||
                          secTypeName.Contains("ETF", StringComparison.OrdinalIgnoreCase) ||
                          secTypeName.Contains("基金", StringComparison.OrdinalIgnoreCase);
        var isIndex = classify.Contains("Index", StringComparison.OrdinalIgnoreCase) ||
                      secTypeName.Contains("指数", StringComparison.OrdinalIgnoreCase);
        var assetType = isIndex ? AssetType.Index : isFundOrEtf ? AssetType.Etf : AssetType.Stock;

        switch (mktNum)
        {
            case "1": // Shanghai
            case "0": // Shenzhen
            {
                var prefix = mktNum == "1" ? "SH" : "SZ";
                return new SearchResultItem
                {
                    Instrument = new Instrument
                    {
                        Id = new InstrumentId(Market.CN, assetType, prefix + code),
                        Name = name,
                        DisplayCode = $"{prefix} {code}",
                        Currency = "CNY",
                        TencentCodeHint = prefix.ToLowerInvariant() + code,
                        EastmoneySecIdHint = quoteId
                    },
                    MarketLabel = mktNum == "1" ? "沪市" : "深市",
                    SecurityTypeLabel = string.IsNullOrWhiteSpace(secTypeName) ? "A股" : secTypeName,
                    Source = "Eastmoney"
                };
            }
            case "116": // HK stock/ETF
                return new SearchResultItem
                {
                    Instrument = new Instrument
                    {
                        Id = new InstrumentId(Market.HK, assetType, code),
                        Name = name,
                        DisplayCode = code,
                        Currency = "HKD",
                        TencentCodeHint = "hk" + code,
                        EastmoneySecIdHint = quoteId
                    },
                    MarketLabel = "港股",
                    SecurityTypeLabel = string.IsNullOrWhiteSpace(secTypeName) ? "港股" : secTypeName,
                    Source = "Eastmoney"
                };
            case "124": // HK index (e.g., HSTECH)
                return new SearchResultItem
                {
                    Instrument = new Instrument
                    {
                        Id = new InstrumentId(Market.HK, AssetType.Index, code),
                        Name = name,
                        DisplayCode = code,
                        Currency = "HKD",
                        TencentCodeHint = "hk" + code,
                        EastmoneySecIdHint = quoteId
                    },
                    MarketLabel = "港股指数",
                    SecurityTypeLabel = "指数",
                    Source = "Eastmoney"
                };
            case "100": // Global/HK/US Index
            {
                if (code is "HSI" or "HSCEI" or "HSTECH")
                {
                    return new SearchResultItem
                    {
                        Instrument = new Instrument
                        {
                            Id = new InstrumentId(Market.HK, AssetType.Index, code),
                            Name = name,
                            DisplayCode = code,
                            Currency = "HKD",
                            TencentCodeHint = "hk" + code,
                            EastmoneySecIdHint = quoteId
                        },
                        MarketLabel = "港股指数",
                        SecurityTypeLabel = "指数",
                        Source = "Eastmoney"
                    };
                }

                var canonicalUsCode = code switch
                {
                    "NDX100" => "NDX",
                    "NDX" => "IXIC",
                    "DJIA" => "DJI",
                    _ => code
                };
                var txHint = canonicalUsCode switch
                {
                    "NDX" => "us.NDX",
                    "IXIC" => "us.IXIC",
                    "SPX" => "us.INX",
                    "DJI" => "us.DJI",
                    _ => null
                };
                if (txHint is null && code is not ("SPX" or "NDX100" or "NDX" or "DJIA"))
                    return null;

                return new SearchResultItem
                {
                    Instrument = new Instrument
                    {
                        Id = new InstrumentId(Market.US, AssetType.Index, canonicalUsCode),
                        Name = name,
                        DisplayCode = canonicalUsCode,
                        Currency = "USD",
                        TencentCodeHint = txHint,
                        EastmoneySecIdHint = quoteId
                    },
                    MarketLabel = "美股指数",
                    SecurityTypeLabel = "指数",
                    Source = "Eastmoney"
                };
            }
            case "105": // US NASDAQ
            case "106": // US NYSE
            case "107": // US AMEX
                return new SearchResultItem
                {
                    Instrument = new Instrument
                    {
                        Id = new InstrumentId(Market.US, assetType, code),
                        Name = name,
                        DisplayCode = code,
                        Currency = "USD",
                        TencentCodeHint = "us" + code,
                        EastmoneySecIdHint = quoteId
                    },
                    MarketLabel = "美股",
                    SecurityTypeLabel = string.IsNullOrWhiteSpace(secTypeName) ? "美股" : secTypeName,
                    Source = "Eastmoney"
                };
            default:
                return null;
        }
    }

    public async Task<IntradaySeries> GetIntradayAsync(
        Instrument instrument,
        CancellationToken cancellationToken = default)
    {
        var secId = ResolveEastmoneySecId(instrument);
        if (string.IsNullOrWhiteSpace(secId))
        {
            return new IntradaySeries
            {
                InstrumentId = instrument.Id,
                Source = Name,
                IsSupported = false,
                StatusMessage = "无法映射该标的的东方财富分时代码"
            };
        }

        var url = $"https://push2.eastmoney.com/api/qt/stock/trends2/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58&ndays=1&iscr=0&secid={Uri.EscapeDataString(secId)}";
        using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseIntradayPayload(instrument.Id, json);
    }

    public static IntradaySeries ParseIntradayPayload(InstrumentId instrumentId, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
        {
            return new IntradaySeries
            {
                InstrumentId = instrumentId,
                Source = "Eastmoney",
                IsSupported = false,
                StatusMessage = "东方财富未返回该标的分时数据"
            };
        }

        var preClose = ReadNullablePositiveDecimal(dataNode, "preClose");
        var points = new List<IntradayPoint>();
        DateOnly? tradingDate = null;

        if (dataNode.TryGetProperty("trends", out var trends) && trends.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in trends.EnumerateArray())
            {
                var line = item.GetString();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Format: "2026-11-25 22:30,open,close,high,low,vol,amount,avg"
                var parts = line.Split(',');
                if (parts.Length < 3)
                    continue;

                if (!DateTime.TryParseExact(parts[0].Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    continue;

                if (!decimal.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price <= 0m)
                    continue;

                decimal? avg = parts.Length >= 8 && decimal.TryParse(parts[7].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && a > 0m
                    ? a : null;
                long? vol = parts.Length >= 6 && long.TryParse(parts[5].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                    ? v : null;

                var date = DateOnly.FromDateTime(dt);
                tradingDate ??= date;
                var ts = ChinaTimeHelper.CreateChinaDateTimeOffset(date, TimeOnly.FromDateTime(dt));
                points.Add(new IntradayPoint(ts, price, avg, vol));
            }
        }

        return new IntradaySeries
        {
            InstrumentId = instrumentId,
            TradingDate = tradingDate,
            PreviousClose = preClose,
            Points = points,
            Source = "Eastmoney",
            IsSupported = true,
            StatusMessage = points.Count == 0 ? "当前尚未开盘或暂无当日分时数据" : null
        };
    }

    private static string? ReadStringOrNumber(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static decimal? ReadNullableDecimal(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
            return d;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s) && s != "-" &&
                decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    private static decimal? ReadNullablePositiveDecimal(JsonElement el, string prop)
    {
        var v = ReadNullableDecimal(el, prop);
        return v.HasValue && v.Value > 0m ? v : null;
    }

    private static long? ReadNullableLong(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l))
            return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
