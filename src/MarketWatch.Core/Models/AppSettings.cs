namespace MarketWatch.Core.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public int RefreshIntervalSeconds { get; set; } = 10;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;
    public ColorScheme ColorScheme { get; set; } = ColorScheme.ChinaRedUpGreenDown;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool ShowQuoteInTrayTooltip { get; set; } = false;
    public bool TopMostMainWindow { get; set; } = false;
    public bool EnableAnimations { get; set; } = true;
    public bool EnableReminderSound { get; set; } = false;
    public bool EnableFullScreenOverlayForStrong { get; set; } = false;
    public bool AutoStartEnabled { get; set; } = false;
    public RepeatMode DefaultCalendarMode { get; set; } = RepeatMode.FundTradingDays;
    public string PrimaryProvider { get; set; } = "Eastmoney";

    public double WindowWidth { get; set; } = 820;
    public double WindowHeight { get; set; } = 540;
    public bool MiniWindowVisible { get; set; } = false;
    public double? MiniWindowLeft { get; set; }
    public double? MiniWindowTop { get; set; }
    public double MiniWindowOpacity { get; set; } = 0.94;

    public List<Instrument> Watchlist { get; set; } = new();
    public List<ReminderRule> ReminderRules { get; set; } = new();
    public List<PriceAlertRule> PriceAlerts { get; set; } = new();
    public List<DateOnly> CustomHolidayDates { get; set; } = new();
    public List<DateOnly> CustomOpenDates { get; set; } = new();

    public void Normalize()
    {
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 5, 60);
        MiniWindowOpacity = Math.Clamp(MiniWindowOpacity, 0.55, 1.0);
        WindowWidth = Math.Clamp(WindowWidth, 620, 2200);
        WindowHeight = Math.Clamp(WindowHeight, 440, 1400);
        if (string.IsNullOrWhiteSpace(PrimaryProvider))
            PrimaryProvider = "Eastmoney";
        Watchlist ??= new();
        ReminderRules ??= new();
        PriceAlerts ??= new();
        CustomHolidayDates ??= new();
        CustomOpenDates ??= new();
    }
}

public sealed class AlertHistoryData
{
    public List<ReminderTriggerRecord> ReminderRecords { get; set; } = new();
    public List<PriceAlertRecord> PriceAlertRecords { get; set; } = new();
}
