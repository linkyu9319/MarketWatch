using MarketWatch.Core.Models;

namespace MarketWatch.Core.Services;

public static class DefaultPresets
{
    public static readonly InstrumentId HstechId = new(Market.HK, AssetType.Index, "HSTECH");
    public static readonly InstrumentId HsiId = new(Market.HK, AssetType.Index, "HSI");
    public static readonly InstrumentId HsceiId = new(Market.HK, AssetType.Index, "HSCEI");
    public static readonly InstrumentId ShCompositeId = new(Market.CN, AssetType.Index, "SH000001");
    public static readonly InstrumentId NdxId = new(Market.US, AssetType.Index, "NDX");
    public static readonly InstrumentId QqqId = new(Market.US, AssetType.Etf, "QQQ");

    public static List<Instrument> CreateDefaultWatchlist() =>
    [
        new()
        {
            Id = HstechId,
            Name = "恒生科技指数",
            DisplayCode = "HSTECH",
            Currency = "HKD",
            TencentCodeHint = "hkHSTECH",
            EastmoneySecIdHint = "124.HSTECH"
        },
        new()
        {
            Id = HsiId,
            Name = "恒生指数",
            DisplayCode = "HSI",
            Currency = "HKD",
            TencentCodeHint = "hkHSI",
            EastmoneySecIdHint = "100.HSI"
        },
        new()
        {
            Id = ShCompositeId,
            Name = "上证指数",
            DisplayCode = "SH 000001",
            Currency = "CNY",
            TencentCodeHint = "sh000001",
            EastmoneySecIdHint = "1.000001"
        },
        new()
        {
            Id = NdxId,
            Name = "纳斯达克100",
            DisplayCode = "NDX",
            Currency = "USD",
            TencentCodeHint = "us.NDX",
            EastmoneySecIdHint = "100.NDX100"
        },
        new()
        {
            Id = QqqId,
            Name = "纳指100ETF-QQQ",
            DisplayCode = "QQQ",
            Currency = "USD",
            TencentCodeHint = "usQQQ",
            EastmoneySecIdHint = "105.QQQ"
        }
    ];

    public static IReadOnlyList<Instrument> CreateFullCatalog() =>
    [
        // HK Indices
        new() { Id = HstechId, Name = "恒生科技指数", DisplayCode = "HSTECH", Currency = "HKD", TencentCodeHint = "hkHSTECH", EastmoneySecIdHint = "124.HSTECH" },
        new() { Id = HsiId, Name = "恒生指数", DisplayCode = "HSI", Currency = "HKD", TencentCodeHint = "hkHSI", EastmoneySecIdHint = "100.HSI" },
        new() { Id = HsceiId, Name = "国企指数", DisplayCode = "HSCEI", Currency = "HKD", TencentCodeHint = "hkHSCEI", EastmoneySecIdHint = "100.HSCEI" },
        // HK Stocks
        new() { Id = new(Market.HK, AssetType.Stock, "00700"), Name = "腾讯控股", DisplayCode = "00700", Currency = "HKD", TencentCodeHint = "hk00700", EastmoneySecIdHint = "116.00700" },
        new() { Id = new(Market.HK, AssetType.Stock, "01810"), Name = "小米集团-W", DisplayCode = "01810", Currency = "HKD", TencentCodeHint = "hk01810", EastmoneySecIdHint = "116.01810" },
        new() { Id = new(Market.HK, AssetType.Stock, "09988"), Name = "阿里巴巴-W", DisplayCode = "09988", Currency = "HKD", TencentCodeHint = "hk09988", EastmoneySecIdHint = "116.09988" },
        // CN Indices
        new() { Id = ShCompositeId, Name = "上证指数", DisplayCode = "SH 000001", Currency = "CNY", TencentCodeHint = "sh000001", EastmoneySecIdHint = "1.000001" },
        new() { Id = new(Market.CN, AssetType.Index, "SZ399001"), Name = "深证成指", DisplayCode = "SZ 399001", Currency = "CNY", TencentCodeHint = "sz399001", EastmoneySecIdHint = "0.399001" },
        new() { Id = new(Market.CN, AssetType.Index, "SZ399006"), Name = "创业板指", DisplayCode = "SZ 399006", Currency = "CNY", TencentCodeHint = "sz399006", EastmoneySecIdHint = "0.399006" },
        new() { Id = new(Market.CN, AssetType.Index, "SH000300"), Name = "沪深300", DisplayCode = "SH 000300", Currency = "CNY", TencentCodeHint = "sh000300", EastmoneySecIdHint = "1.000300" },
        // CN Stocks & ETFs
        new() { Id = new(Market.CN, AssetType.Stock, "SH600519"), Name = "贵州茅台", DisplayCode = "SH 600519", Currency = "CNY", TencentCodeHint = "sh600519", EastmoneySecIdHint = "1.600519" },
        new() { Id = new(Market.CN, AssetType.Etf, "SH513130"), Name = "恒生科技ETF华泰柏瑞", DisplayCode = "SH 513130", Currency = "CNY", TencentCodeHint = "sh513130", EastmoneySecIdHint = "1.513130" },
        new() { Id = new(Market.CN, AssetType.Etf, "SH513180"), Name = "恒生科技ETF华夏", DisplayCode = "SH 513180", Currency = "CNY", TencentCodeHint = "sh513180", EastmoneySecIdHint = "1.513180" },
        // US Indices
        new() { Id = NdxId, Name = "纳斯达克100", DisplayCode = "NDX", Currency = "USD", TencentCodeHint = "us.NDX", EastmoneySecIdHint = "100.NDX100" },
        new() { Id = new(Market.US, AssetType.Index, "SPX"), Name = "标普500", DisplayCode = "SPX", Currency = "USD", TencentCodeHint = "us.INX", EastmoneySecIdHint = "100.SPX" },
        new() { Id = new(Market.US, AssetType.Index, "DJI"), Name = "道琼斯", DisplayCode = "DJI", Currency = "USD", TencentCodeHint = "us.DJI", EastmoneySecIdHint = "100.DJIA" },
        // US Stocks & ETFs
        new() { Id = new(Market.US, AssetType.Stock, "NVDA"), Name = "英伟达", DisplayCode = "NVDA", Currency = "USD", TencentCodeHint = "usNVDA", EastmoneySecIdHint = "105.NVDA" },
        new() { Id = new(Market.US, AssetType.Stock, "AAPL"), Name = "苹果", DisplayCode = "AAPL", Currency = "USD", TencentCodeHint = "usAAPL", EastmoneySecIdHint = "105.AAPL" },
        new() { Id = QqqId, Name = "纳指100ETF-QQQ", DisplayCode = "QQQ", Currency = "USD", TencentCodeHint = "usQQQ", EastmoneySecIdHint = "105.QQQ" }
    ];

    public static List<ReminderRule> CreateDefaultReminderRules() =>
    [
        new()
        {
            Id = "hstech-1430",
            Name = "恒生科技基金交易提醒",
            TargetInstrumentId = HstechId,
            TriggerTime = new TimeOnly(14, 30),
            CutoffTime = new TimeOnly(15, 0),
            RepeatMode = RepeatMode.FundTradingDays,
            Strength = ReminderStrength.Normal,
            IsEnabled = true,
            AllowFullScreenOverlay = false,
            AllowSound = false
        },
        new()
        {
            Id = "hstech-1445",
            Name = "恒生科技基金交易提醒",
            TargetInstrumentId = HstechId,
            TriggerTime = new TimeOnly(14, 45),
            CutoffTime = new TimeOnly(15, 0),
            RepeatMode = RepeatMode.FundTradingDays,
            Strength = ReminderStrength.Normal,
            IsEnabled = true,
            AllowFullScreenOverlay = false,
            AllowSound = false
        },
        new()
        {
            Id = "hstech-1455",
            Name = "恒生科技基金交易提醒",
            TargetInstrumentId = HstechId,
            TriggerTime = new TimeOnly(14, 55),
            CutoffTime = new TimeOnly(15, 0),
            RepeatMode = RepeatMode.FundTradingDays,
            Strength = ReminderStrength.Strong,
            IsEnabled = true,
            AllowFullScreenOverlay = false,
            AllowSound = false
        }
    ];

    public static AppSettings CreateDefaultSettings() => new()
    {
        Watchlist = CreateDefaultWatchlist(),
        ReminderRules = CreateDefaultReminderRules(),
        PriceAlerts = new(),
        RefreshIntervalSeconds = 10,
        ThemeMode = ThemeMode.Dark,
        ColorScheme = ColorScheme.ChinaRedUpGreenDown,
        MinimizeToTray = true,
        CloseToTray = true,
        TopMostMainWindow = false,
        EnableAnimations = true,
        EnableReminderSound = false,
        EnableFullScreenOverlayForStrong = false,
        AutoStartEnabled = false,
        DefaultCalendarMode = RepeatMode.FundTradingDays,
        PrimaryProvider = "Eastmoney"
    };
}
