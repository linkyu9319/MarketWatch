using MarketWatch.Core.Models;

namespace MarketWatch.Core.Interfaces;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    TimeZoneInfo LocalTimeZone { get; }
}

public interface IQuoteProvider
{
    string Name { get; }
    ProviderCapabilities Capabilities { get; }

    Task<IReadOnlyDictionary<InstrumentId, Quote>> GetQuotesAsync(
        IReadOnlyList<Instrument> instruments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<IntradaySeries> GetIntradayAsync(
        Instrument instrument,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderHealthSnapshot(
    string ProviderName,
    bool IsHealthy,
    int ConsecutiveFailures,
    DateTimeOffset? BackoffUntil,
    DateTimeOffset? LastSuccessAt,
    string? LastError);

public sealed record BatchQuoteResponse(
    IReadOnlyDictionary<InstrumentId, Quote> Quotes,
    string ActiveSourceSummary,
    bool AnyFreshData,
    string? OverallWarning);

public interface IQuoteAggregator
{
    string PrimaryProviderName { get; set; }

    Task<BatchQuoteResponse> GetQuotesAsync(
        IReadOnlyList<Instrument> instruments,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<IntradaySeries> GetIntradayAsync(
        Instrument instrument,
        CancellationToken cancellationToken = default);

    IReadOnlyList<ProviderHealthSnapshot> GetProviderHealthSnapshots();
}

public sealed record CalendarDayEvaluation(
    DateOnly Date,
    bool IsTradingDay,
    CalendarCoverageStatus CoverageStatus,
    string Description);

public sealed record CalendarCoverageInfo(
    DateOnly VerifiedFrom,
    DateOnly VerifiedTo,
    string SourceNote);

public interface ITradingCalendar
{
    CalendarCoverageInfo CoverageInfo { get; }
    void UpdateCustomOverrides(IEnumerable<DateOnly> customHolidays, IEnumerable<DateOnly> customOpenDays);
    CalendarDayEvaluation EvaluateFundTradingDay(DateOnly chinaDate, RepeatMode repeatMode);
    bool IsMarketTradingDay(Market market, DateOnly localMarketDate);
    TimeOnly? GetEarlyCloseTime(Market market, DateOnly localMarketDate) => null;
}

public sealed record SettingsLoadResult(
    AppSettings Settings,
    bool WasCorrupted,
    string? BackupFilePath,
    string? Notice);

public interface ISettingsStore
{
    string SettingsFilePath { get; }
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task ExportAsync(AppSettings settings, string targetPath, CancellationToken cancellationToken = default);
    Task<AppSettings> ImportAsync(string sourcePath, CancellationToken cancellationToken = default);
}

public interface IAlertHistoryStore
{
    string HistoryFilePath { get; }
    Task<AlertHistoryData> LoadAsync(CancellationToken cancellationToken = default);
    Task AppendReminderRecordAsync(ReminderTriggerRecord record, CancellationToken cancellationToken = default);
    Task AppendPriceAlertRecordAsync(PriceAlertRecord record, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Independent persistent daily trigger deduplication keys that survive user clearing of history logs.
    /// </summary>
    Task<IReadOnlySet<string>> LoadDedupKeysAsync(CancellationToken cancellationToken = default);
    Task RecordDedupKeysAsync(IEnumerable<ReminderTriggerRecord> records, CancellationToken cancellationToken = default);
}

public interface ISessionStateProvider
{
    bool IsSessionLocked { get; }
    event Action? SessionUnlocked;
}

public interface IAutoStartService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

public interface IAppLogger
{
    void Info(string category, string message);
    void Warn(string category, string message);
    void Error(string category, string message, Exception? ex = null);
}
