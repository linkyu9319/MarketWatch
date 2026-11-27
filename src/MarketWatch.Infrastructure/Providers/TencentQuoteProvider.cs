using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.Infrastructure.Providers;

/// <summary>
/// Tencent Finance public market data adapter (qt.gtimg.cn, smartbox.gtimg.cn, web.ifzq.gtimg.cn).
/// Supports batch quotes across HK, CN, and US markets using GB18030 decoding,
/// plus intraday minute data for HK and CN markets.
/// </summary>
public sealed partial class TencentQuoteProvider : IQuoteProvider
{
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly ITradingCalendar _calendar;
    private static readonly Encoding GbkEncoding;

    static TencentQuoteProvider()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        GbkEncoding = Encoding.GetEncoding("GB18030");
    }

    public TencentQuoteProvider(HttpClient httpClient, IClock clock, ITradingCalendar calendar)
    {
        _httpClient = httpClient;
        _clock = clock;
        _calendar = calendar;
    }

    public string Name => "Tencent";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.BatchQuotes |
        ProviderCapabilities.Search |
        ProviderCapabilities.IntradayHk |
        ProviderCapabilities.IntradayCn;

    public static string? ResolveTencentCode(Instrument instrument)
    {
        if (!string.IsNullOrWhiteSpace(instrument.TencentCodeHint))
            return instrument.TencentCodeHint.Trim();

        var code = instrument.Id.Code.Trim().ToUpperInvariant();
        return instrument.Id.Market switch
        {
            Market.HK => "hk" + code,
            Market.CN => ResolveCnCode(code),
            Market.US => ResolveUsCode(instrument.Id.AssetType, code),
            _ => null
        };
    }

    private static string ResolveCnCode(string code)
    {
        if (code.StartsWith("SH", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("SZ", StringComparison.OrdinalIgnoreCase) ||
            code.StartsWith("BJ", StringComparison.OrdinalIgnoreCase))
        {
            return code.ToLowerInvariant();
        }
        return code.StartsWith('6') || code.StartsWith('5') ? "sh" + code : "sz" + code;
    }

    private static string ResolveUsCode(AssetType assetType, string code)
    {
        if (assetType == AssetType.Index)
        {
            return code switch
            {
                "NDX" or "NDX100" => "us.NDX",
                "SPX" or "INX" => "us.INX",
                "DJI" or "DJIA" => "us.DJI",
                "IXIC" => "us.IXIC",
                _ => code.StartsWith('.') ? "us" + code : "us." + code
            };
        }
        return "us" + code;
    }

    public async Task<IReadOnlyDictionary<InstrumentId, Quote>> GetQuotesAsync(
        IReadOnlyList<Instrument> instruments,
        CancellationToken cancellationToken = default)
    {
        var codeToInstruments = new Dictionary<string, List<Instrument>>(StringComparer.OrdinalIgnoreCase);
        var queryCodes = new List<string>();

        foreach (var inst in instruments)
        {
            var txCode = ResolveTencentCode(inst);
            if (string.IsNullOrWhiteSpace(txCode))
                continue;

            if (!codeToInstruments.TryGetValue(txCode, out var list))
            {
                list = new List<Instrument>();
                codeToInstruments[txCode] = list;
                queryCodes.Add(txCode);
            }
            list.Add(inst);
        }

        if (queryCodes.Count == 0)
            return new Dictionary<InstrumentId, Quote>();

        var url = "https://qt.gtimg.cn/q=" + string.Join(',', queryCodes);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var rawText = GbkEncoding.GetString(bytes);
        var receivedAt = _clock.UtcNow;

        return ParseBatchPayload(rawText, codeToInstruments, receivedAt, _calendar);
    }

    public static IReadOnlyDictionary<InstrumentId, Quote> ParseBatchPayload(
        string rawText,
        IReadOnlyDictionary<string, List<Instrument>> codeToInstruments,
        DateTimeOffset receivedAt,
        ITradingCalendar calendar)
    {
        var result = new Dictionary<InstrumentId, Quote>();
        var lines = rawText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var match = QuoteLineRegex().Match(line);
            if (!match.Success)
                continue;

            var returnedKey = match.Groups[1].Value;
            var body = match.Groups[2].Value;

            if (!codeToInstruments.TryGetValue(returnedKey, out var matchedInstruments))
            {
                // Tencent sometimes returns v_usNDX or v_us_NDX vs v_us.NDX
                var alt = codeToInstruments.FirstOrDefault(kv =>
                    string.Equals(kv.Key.Replace(".", ""), returnedKey.Replace(".", ""), StringComparison.OrdinalIgnoreCase));
                if (alt.Value is null)
                    continue;
                matchedInstruments = alt.Value;
            }

            if (string.IsNullOrWhiteSpace(body))
                continue;

            var parts = body.Split('~');
            if (parts.Length < 35)
                continue;

            foreach (var inst in matchedInstruments)
            {
                var quote = ParseSingleInstrument(inst, parts, receivedAt, calendar);
                if (quote is not null)
                {
                    result[inst.Id] = quote;
                }
            }
        }

        return result;
    }

    private static Quote? ParseSingleInstrument(
        Instrument inst,
        string[] parts,
        DateTimeOffset receivedAt,
        ITradingCalendar calendar)
    {
        var name = string.IsNullOrWhiteSpace(parts[1]) ? inst.Name : parts[1].Trim();
        var lastPrice = ParsePositiveDecimal(parts[3]);
        var prevClose = ParsePositiveDecimal(parts[4]);
        var open = ParsePositiveDecimal(parts[5]);
        var change = ParseDecimal(parts[31]);
        var changePct = ParseDecimal(parts[32]);
        var high = ParsePositiveDecimal(parts[33]);
        var low = ParsePositiveDecimal(parts[34]);

        // If last price is missing/zero, treat as unavailable (never fabricate 0)
        if (!lastPrice.HasValue)
        {
            return new Quote
            {
                InstrumentId = inst.Id,
                Name = name,
                Currency = inst.Currency ?? ChinaTimeHelper.GetDefaultCurrency(inst.Id.Market),
                PreviousClose = prevClose,
                ReceivedAt = receivedAt,
                Source = "Tencent",
                MarketStatus = MarketStatus.Unknown,
                DelayStatus = DelayStatus.Unknown,
                Error = "报价暂不可用"
            };
        }

        // Recompute change if missing but prevClose is valid
        if (!change.HasValue && prevClose.HasValue && prevClose.Value > 0)
        {
            change = lastPrice.Value - prevClose.Value;
        }
        if (!changePct.HasValue && change.HasValue && prevClose.HasValue && prevClose.Value > 0)
        {
            changePct = Math.Round(change.Value / prevClose.Value * 100m, 2);
        }

        var quoteTime = ParseTencentQuoteTime(inst.Id.Market, parts[30]);
        var currency = ExtractCurrency(inst.Id.Market, parts) ?? inst.Currency ?? ChinaTimeHelper.GetDefaultCurrency(inst.Id.Market);

        var localDate = ChinaTimeHelper.GetMarketLocalDate(inst.Id.Market, receivedAt);
        var isTradingDay = calendar.IsMarketTradingDay(inst.Id.Market, localDate);
        var marketStatus = ChinaTimeHelper.ComputeMarketStatus(inst.Id.Market, receivedAt, isTradingDay);
        var delayStatus = InferDelayStatus(marketStatus, quoteTime, receivedAt);

        return new Quote
        {
            InstrumentId = inst.Id,
            Name = name,
            Currency = currency,
            LastPrice = lastPrice,
            PreviousClose = prevClose,
            Change = change,
            ChangePercent = changePct,
            Open = open,
            High = high,
            Low = low,
            QuoteTime = quoteTime,
            ReceivedAt = receivedAt,
            Source = "Tencent",
            MarketStatus = marketStatus,
            DelayStatus = delayStatus,
            Error = null
        };
    }

    private static DateTimeOffset? ParseTencentQuoteTime(Market market, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        if (market == Market.HK &&
            DateTime.TryParseExact(s, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hkDt))
        {
            return ChinaTimeHelper.CreateChinaDateTimeOffset(DateOnly.FromDateTime(hkDt), TimeOnly.FromDateTime(hkDt));
        }

        if (market == Market.CN &&
            DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cnDt))
        {
            return ChinaTimeHelper.CreateChinaDateTimeOffset(DateOnly.FromDateTime(cnDt), TimeOnly.FromDateTime(cnDt));
        }

        if (market == Market.US &&
            DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var usDt))
        {
            return ChinaTimeHelper.CreateUsEasternDateTimeOffset(DateOnly.FromDateTime(usDt), TimeOnly.FromDateTime(usDt));
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fallbackDt))
        {
            return market == Market.US
                ? ChinaTimeHelper.CreateUsEasternDateTimeOffset(DateOnly.FromDateTime(fallbackDt), TimeOnly.FromDateTime(fallbackDt))
                : ChinaTimeHelper.CreateChinaDateTimeOffset(DateOnly.FromDateTime(fallbackDt), TimeOnly.FromDateTime(fallbackDt));
        }

        return null;
    }

    private static string? ExtractCurrency(Market market, string[] parts)
    {
        var idx = market switch
        {
            Market.HK => 75,
            Market.CN => 82,
            Market.US => 35,
            _ => -1
        };

        if (idx >= 0 && idx < parts.Length && !string.IsNullOrWhiteSpace(parts[idx]))
        {
            var val = parts[idx].Trim();
            if (val.Length is >= 2 and <= 4 && val.All(char.IsLetter))
                return val.ToUpperInvariant();
        }
        return null;
    }

    internal static DelayStatus InferDelayStatus(MarketStatus marketStatus, DateTimeOffset? quoteTime, DateTimeOffset receivedAt)
    {
        if (marketStatus == MarketStatus.Closed)
            return DelayStatus.EndOfDay;

        if (!quoteTime.HasValue)
            return DelayStatus.Unknown;

        var age = receivedAt - quoteTime.Value;
        if (marketStatus == MarketStatus.Trading)
        {
            if (age <= TimeSpan.FromSeconds(45))
                return DelayStatus.NearRealtime;
            if (age >= TimeSpan.FromMinutes(3))
                return DelayStatus.Delayed;
            return DelayStatus.Unknown;
        }

        if (age > TimeSpan.FromHours(6))
            return DelayStatus.EndOfDay;

        return DelayStatus.Unknown;
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResultItem>();

        var url = $"https://smartbox.gtimg.cn/s3/?v=2&q={Uri.EscapeDataString(query.Trim())}&t=all";
        using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var match = HintRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return Array.Empty<SearchResultItem>();

        var results = new List<SearchResultItem>();
        var items = match.Groups[1].Value.Split('^', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in items)
        {
            var f = raw.Split('~');
            if (f.Length < 5)
                continue;

            var mktRaw = f[0].Trim().ToLowerInvariant();
            var codeRaw = f[1].Trim().ToUpperInvariant();
            var name = DecodeUnicodeEscapes(f[2].Trim());
            var typeRaw = f[4].Trim().ToUpperInvariant();

            var mapped = MapSearchItem(mktRaw, codeRaw, name, typeRaw);
            if (mapped is not null)
                results.Add(mapped);
        }

        return results;
    }

    private static SearchResultItem? MapSearchItem(string mktRaw, string codeRaw, string name, string typeRaw)
    {
        var isEtf = typeRaw.Contains("ETF", StringComparison.OrdinalIgnoreCase);
        var isIndex = typeRaw.Contains("ZS", StringComparison.OrdinalIgnoreCase) || typeRaw.Contains("INDEX", StringComparison.OrdinalIgnoreCase);
        var assetType = isIndex ? AssetType.Index : isEtf ? AssetType.Etf : AssetType.Stock;

        if (mktRaw is "sh" or "sz")
        {
            var canonicalCode = mktRaw.ToUpperInvariant() + codeRaw;
            var emPrefix = mktRaw == "sh" ? "1" : "0";
            return new SearchResultItem
            {
                Instrument = new Instrument
                {
                    Id = new InstrumentId(Market.CN, assetType, canonicalCode),
                    Name = name,
                    DisplayCode = $"{mktRaw.ToUpperInvariant()} {codeRaw}",
                    Currency = "CNY",
                    TencentCodeHint = mktRaw + codeRaw,
                    EastmoneySecIdHint = $"{emPrefix}.{codeRaw}"
                },
                MarketLabel = mktRaw == "sh" ? "沪市" : "深市",
                SecurityTypeLabel = isIndex ? "指数" : isEtf ? "ETF" : "A股",
                Source = "Tencent"
            };
        }

        if (mktRaw == "hk")
        {
            return new SearchResultItem
            {
                Instrument = new Instrument
                {
                    Id = new InstrumentId(Market.HK, assetType, codeRaw),
                    Name = name,
                    DisplayCode = codeRaw,
                    Currency = "HKD",
                    TencentCodeHint = "hk" + codeRaw,
                    EastmoneySecIdHint = codeRaw == "HSTECH" ? "124.HSTECH" : isIndex ? $"100.{codeRaw}" : $"116.{codeRaw}"
                },
                MarketLabel = "港股",
                SecurityTypeLabel = isIndex ? "指数" : isEtf ? "ETF" : "港股",
                Source = "Tencent"
            };
        }

        if (mktRaw == "us")
        {
            var cleanCode = codeRaw.Split('.')[0].TrimStart('.');
            if (string.IsNullOrWhiteSpace(cleanCode))
                return null;
            return new SearchResultItem
            {
                Instrument = new Instrument
                {
                    Id = new InstrumentId(Market.US, assetType, cleanCode),
                    Name = name,
                    DisplayCode = cleanCode,
                    Currency = "USD",
                    TencentCodeHint = isIndex ? "us." + cleanCode : "us" + cleanCode
                },
                MarketLabel = "美股",
                SecurityTypeLabel = isIndex ? "指数" : isEtf ? "ETF" : "美股",
                Source = "Tencent"
            };
        }

        return null;
    }

    public async Task<IntradaySeries> GetIntradayAsync(
        Instrument instrument,
        CancellationToken cancellationToken = default)
    {
        if (instrument.Id.Market == Market.US)
        {
            return new IntradaySeries
            {
                InstrumentId = instrument.Id,
                Source = Name,
                IsSupported = false,
                StatusMessage = "腾讯公开分时接口不支持美股完整分钟线"
            };
        }

        var txCode = ResolveTencentCode(instrument);
        if (string.IsNullOrWhiteSpace(txCode))
        {
            return new IntradaySeries
            {
                InstrumentId = instrument.Id,
                Source = Name,
                IsSupported = false,
                StatusMessage = "无法映射该标的的腾讯分时代码"
            };
        }

        var url = $"https://web.ifzq.gtimg.cn/appstock/app/minute/query?code={Uri.EscapeDataString(txCode)}";
        using var resp = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return ParseIntradayPayload(instrument.Id, txCode, json);
    }

    public static IntradaySeries ParseIntradayPayload(InstrumentId instrumentId, string txCode, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var dataNode) || dataNode.ValueKind != JsonValueKind.Object)
        {
            return new IntradaySeries
            {
                InstrumentId = instrumentId,
                Source = "Tencent",
                IsSupported = false,
                StatusMessage = "未返回分时数据"
            };
        }

        if (!dataNode.TryGetProperty(txCode, out var symNode))
        {
            // Fallback to first object property
            var firstProp = dataNode.EnumerateObject().FirstOrDefault();
            if (firstProp.Value.ValueKind != JsonValueKind.Object)
            {
                return new IntradaySeries
                {
                    InstrumentId = instrumentId,
                    Source = "Tencent",
                    IsSupported = false,
                    StatusMessage = "分时数据中未找到对应标的"
                };
            }
            symNode = firstProp.Value;
        }

        decimal? prevClose = null;
        if (symNode.TryGetProperty("qt", out var qtNode) && qtNode.ValueKind == JsonValueKind.Object)
        {
            var qtArr = qtNode.EnumerateObject().FirstOrDefault().Value;
            if (qtArr.ValueKind == JsonValueKind.Array && qtArr.GetArrayLength() > 4)
            {
                prevClose = ParsePositiveDecimal(qtArr[4].GetString());
            }
        }

        DateOnly? tradingDate = null;
        var points = new List<IntradayPoint>();

        if (symNode.TryGetProperty("data", out var innerData) && innerData.ValueKind == JsonValueKind.Object)
        {
            if (innerData.TryGetProperty("date", out var dateEl) &&
                DateOnly.TryParseExact(dateEl.GetString(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                tradingDate = parsedDate;
            }

            if (tradingDate.HasValue &&
                innerData.TryGetProperty("data", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in arr.EnumerateArray())
                {
                    var line = entry.GetString();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length < 2)
                        continue;

                    if (!TimeOnly.TryParseExact(tokens[0], "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var tm))
                        continue;

                    var price = ParsePositiveDecimal(tokens[1]);
                    if (!price.HasValue)
                        continue;

                    long? vol = tokens.Length >= 3 && long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                        ? v : null;

                    var ts = ChinaTimeHelper.CreateChinaDateTimeOffset(tradingDate.Value, tm);
                    points.Add(new IntradayPoint(ts, price.Value, null, vol));
                }
            }
        }

        var statusMsg = points.Count <= 1
            ? "当前尚未进入连续交易时段，暂无完整当日分时曲线"
            : null;

        return new IntradaySeries
        {
            InstrumentId = instrumentId,
            TradingDate = tradingDate,
            PreviousClose = prevClose,
            Points = points,
            Source = "Tencent",
            IsSupported = true,
            StatusMessage = statusMsg
        };
    }

    private static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static decimal? ParsePositiveDecimal(string? s)
    {
        var v = ParseDecimal(s);
        return v.HasValue && v.Value > 0m ? v : null;
    }

    private static string DecodeUnicodeEscapes(string input)
    {
        return UnicodeEscapeRegex().Replace(input, m =>
            ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
    }

    [GeneratedRegex(@"^v_([^=]+)=""(.*)"";?$", RegexOptions.CultureInvariant)]
    private static partial Regex QuoteLineRegex();

    [GeneratedRegex(@"v_hint=""(.*)"";?$", RegexOptions.Singleline)]
    private static partial Regex HintRegex();

    [GeneratedRegex(@"\\u([0-9a-fA-F]{4})")]
    private static partial Regex UnicodeEscapeRegex();
}
