using System.Collections.Concurrent;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Providers;

namespace MarketWatch.App.Services;

/// <summary>
/// Central background orchestrator decoupled from WPF views:
/// 1. Runs an independent 1-second alarm/clock loop so local reminders NEVER depend on market quotes.
/// 2. Runs a non-overlapping async quote polling loop with timeout/cancellation and price-alert evaluation.
/// </summary>
public sealed class AppStateCoordinator : IDisposable
{
    private readonly IClock _clock;
    private readonly IQuoteAggregator _aggregator;
    private readonly ITradingCalendar _calendar;
    private readonly ISettingsStore _settingsStore;
    private readonly IAlertHistoryStore _historyStore;
    private readonly IAutoStartService _autoStartService;
    private readonly ISessionStateProvider _sessionState;
    private readonly IAppLogger _logger;
    private readonly ReminderScheduler _scheduler;
    private readonly PriceAlertEvaluator _priceAlertEvaluator;

    private readonly ConcurrentDictionary<InstrumentId, Quote> _latestQuotes = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _schedulerGate = new(1, 1);
    private readonly object _settingsLock = new();

    private AppSettings _settings = DefaultPresets.CreateDefaultSettings();
    private CancellationTokenSource? _loopCts;
    private DateTimeOffset _nextQuotePollUtc = DateTimeOffset.MinValue;
    private bool _started;

    public AppSettings Settings
    {
        get { lock (_settingsLock) return _settings; }
    }

    public SettingsLoadResult? InitialLoadResult { get; private set; }
    public string ActiveSourceSummary { get; private set; } = "初始化中...";
    public string? ConnectionWarning { get; private set; }
    public bool IsRefreshing { get; private set; }
    public DateTimeOffset? LastRefreshAt { get; private set; }

    public event Action<BatchQuoteResponse>? QuotesUpdated;
    public event Action<ScheduledReminderEvent>? ReminderDue;
    public event Action<PriceAlertRecord>? PriceAlertTriggered;
    public event Action<AppSettings>? SettingsUpdated;
    public event Action? HistoryUpdated;
    public event Action<DateTimeOffset>? ClockTicked;

    public AppStateCoordinator(
        IClock clock,
        IQuoteAggregator aggregator,
        ITradingCalendar calendar,
        ISettingsStore settingsStore,
        IAlertHistoryStore historyStore,
        IAutoStartService autoStartService,
        ISessionStateProvider sessionState,
        IAppLogger logger)
    {
        _clock = clock;
        _aggregator = aggregator;
        _calendar = calendar;
        _settingsStore = settingsStore;
        _historyStore = historyStore;
        _autoStartService = autoStartService;
        _sessionState = sessionState;
        _logger = logger;
        _scheduler = new ReminderScheduler(clock, calendar);
        _priceAlertEvaluator = new PriceAlertEvaluator();

        _sessionState.SessionUnlocked += OnSessionUnlocked;
    }

    private void OnSessionUnlocked()
    {
        _ = Task.Run(async () =>
        {
            await TriggerImmediateSchedulerCheckAsync().ConfigureAwait(false);
            await RefreshQuotesAsync(forceResetBackoff: false).ConfigureAwait(false);
        });
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loadResult = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        InitialLoadResult = loadResult;

        lock (_settingsLock)
        {
            _settings = loadResult.Settings;
            // Sync auto-start display with actual registry if needed
            _settings.AutoStartEnabled = _autoStartService.IsEnabled();
        }

        ApplySettingsSideEffects(Settings);

        var dedupKeys = await _historyStore.LoadDedupKeysAsync(cancellationToken).ConfigureAwait(false);
        _scheduler.SeedDedupKeys(dedupKeys);

        var history = await _historyStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _scheduler.SeedFromHistory(history.ReminderRecords);

        _logger.Info("App", $"Initialized with {Settings.Watchlist.Count} watchlist items, {Settings.ReminderRules.Count} reminder rules, {dedupKeys.Count} dedup keys.");
    }

    public void StartBackgroundLoops()
    {
        if (_started)
            return;
        _started = true;
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;

        // Loop 1: Independent 1-second scheduler & Beijing clock ticker (NEVER awaits quotes!)
        _ = Task.Run(() => RunSchedulerLoopAsync(ct), ct);

        // Loop 2: Independent quote refresh loop
        _ = Task.Run(() => RunQuoteLoopAsync(ct), ct);
    }

    private async Task RunSchedulerLoopAsync(CancellationToken ct)
    {
        // Evaluate immediately on startup (handles restart/catch-up/post-cutoff misses)
        await EvaluateSchedulerSafeAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;

                ClockTicked?.Invoke(_clock.UtcNow);
                await EvaluateSchedulerSafeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Scheduler", "Unexpected error in scheduler loop", ex);
            }
        }
    }

    public Task TriggerImmediateSchedulerCheckAsync(CancellationToken ct = default) =>
        EvaluateSchedulerSafeAsync(ct);

    private async Task EvaluateSchedulerSafeAsync(CancellationToken ct)
    {
        await _schedulerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<ReminderRule> rulesSnapshot;
            lock (_settingsLock)
            {
                rulesSnapshot = _settings.ReminderRules.Select(r => r.Clone()).ToList();
            }

            var tick = _scheduler.EvaluateTick(rulesSnapshot, _sessionState.IsSessionLocked);

            if (tick.RecordsToPersist.Count > 0)
            {
                try
                {
                    await _historyStore.RecordDedupKeysAsync(tick.RecordsToPersist, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn("History", $"Failed persisting dedup keys: {ex.Message}");
                }

                foreach (var rec in tick.RecordsToPersist)
                {
                    try
                    {
                        await _historyStore.AppendReminderRecordAsync(rec, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("History", $"Failed persisting reminder record: {ex.Message}");
                    }
                }

                HistoryUpdated?.Invoke();
            }

            foreach (var ev in tick.PopupsToShow)
            {
                _logger.Info("Scheduler", $"Triggering reminder popup: {ev.Rule.Name} ({ev.ScheduledTime:HH:mm}, catchUp={ev.IsCatchUp}, snooze={ev.IsSnoozedFollowUp})");
                ReminderDue?.Invoke(ev);
            }
        }
        finally
        {
            _schedulerGate.Release();
        }
    }

    private async Task RunQuoteLoopAsync(CancellationToken ct)
    {
        await RefreshQuotesAsync(forceResetBackoff: false, ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    break;

                if (_clock.UtcNow >= _nextQuotePollUtc)
                {
                    await RefreshQuotesAsync(forceResetBackoff: false, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Quotes", "Unexpected error in quote polling loop", ex);
            }
        }
    }

    public async Task RefreshQuotesAsync(bool forceResetBackoff = false, CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Non-overlapping protection: skip if a refresh is already running
            return;
        }

        try
        {
            IsRefreshing = true;

            if (forceResetBackoff && _aggregator is QuoteAggregatorService agg)
            {
                agg.ResetBackoff();
            }

            List<Instrument> fetchList;
            List<PriceAlertRule> alertRulesSnapshot;
            int intervalSec;
            lock (_settingsLock)
            {
                fetchList = BuildRequiredInstrumentsLocked();
                alertRulesSnapshot = _settings.PriceAlerts.Select(a => a.Clone()).ToList();
                intervalSec = _settings.RefreshIntervalSeconds;
            }

            _nextQuotePollUtc = _clock.UtcNow.AddSeconds(Math.Clamp(intervalSec, 5, 60));

            var response = await _aggregator.GetQuotesAsync(fetchList, cancellationToken).ConfigureAwait(false);

            foreach (var (id, quote) in response.Quotes)
            {
                _latestQuotes[id] = quote;
            }

            ActiveSourceSummary = response.ActiveSourceSummary;
            ConnectionWarning = response.OverallWarning;
            LastRefreshAt = _clock.UtcNow;

            QuotesUpdated?.Invoke(response);

            // Evaluate P1 price / change% alerts against freshly arrived quotes
            var firedAlerts = _priceAlertEvaluator.Evaluate(alertRulesSnapshot, response.Quotes, _clock.UtcNow);
            foreach (var alert in firedAlerts)
            {
                _logger.Info("PriceAlert", $"Triggered price alert for {alert.InstrumentName}: {alert.Metric} {alert.Direction} {alert.Threshold} (actual={alert.ActualValue})");
                try
                {
                    await _historyStore.AppendPriceAlertRecordAsync(alert, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn("History", $"Failed persisting price alert record: {ex.Message}");
                }
                PriceAlertTriggered?.Invoke(alert);
            }

            if (firedAlerts.Count > 0)
            {
                HistoryUpdated?.Invoke();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            ConnectionWarning = $"刷新异常：{ex.Message}";
            _logger.Error("Quotes", "RefreshQuotesAsync failed", ex);
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
        }
    }

    public Quote? GetLatestQuote(InstrumentId id) =>
        _latestQuotes.TryGetValue(id, out var q) ? q : null;

    public IReadOnlyDictionary<InstrumentId, Quote> GetAllLatestQuotes() =>
        new Dictionary<InstrumentId, Quote>(_latestQuotes);

    public NextReminderInfo? GetNextUpcomingReminder()
    {
        List<ReminderRule> rulesSnapshot;
        lock (_settingsLock)
        {
            rulesSnapshot = _settings.ReminderRules.Select(r => r.Clone()).ToList();
        }
        return _scheduler.GetNextUpcomingReminder(rulesSnapshot);
    }

    public CalendarDayEvaluation EvaluateTodayCalendar()
    {
        var today = ChinaTimeHelper.GetChinaDate(_clock.UtcNow);
        RepeatMode mode;
        lock (_settingsLock)
        {
            mode = _settings.DefaultCalendarMode;
        }
        return _calendar.EvaluateFundTradingDay(today, mode);
    }

    public void ScheduleSnooze(ReminderRule rule, DateOnly businessDate, TimeSpan delay)
    {
        _scheduler.ScheduleSnooze(rule, businessDate, delay);
    }

    public ScheduledReminderEvent CreateTestReminderEvent(ReminderStrength strength)
    {
        var chinaNow = ChinaTimeHelper.ToChinaTime(_clock.UtcNow);
        var today = DateOnly.FromDateTime(chinaNow.DateTime);
        var nowTime = TimeOnly.FromDateTime(chinaNow.DateTime);

        ReminderRule baseRule;
        lock (_settingsLock)
        {
            baseRule = _settings.ReminderRules.FirstOrDefault(r => r.Strength == strength)?.Clone()
                       ?? _settings.ReminderRules.FirstOrDefault()?.Clone()
                       ?? DefaultPresets.CreateDefaultReminderRules()[strength == ReminderStrength.Strong ? 2 : 0];
        }

        baseRule.Strength = strength;

        return new ScheduledReminderEvent
        {
            Rule = baseRule,
            TriggerKey = $"TEST|{strength}|{chinaNow.ToUnixTimeSeconds()}",
            BusinessDate = today,
            ScheduledTime = nowTime,
            IsCatchUp = false,
            IsSnoozedFollowUp = false,
            IsTest = true,
            IsAfterCutoff = nowTime >= baseRule.CutoffTime
        };
    }

    public async Task UpdateSettingsAsync(Action<AppSettings> mutator, bool triggerImmediateRefresh = false)
    {
        AppSettings updated;
        lock (_settingsLock)
        {
            mutator(_settings);
            _settings.Normalize();
            updated = _settings;
        }

        ApplySettingsSideEffects(updated);
        await _settingsStore.SaveAsync(updated).ConfigureAwait(false);
        SettingsUpdated?.Invoke(updated);

        if (triggerImmediateRefresh)
        {
            _nextQuotePollUtc = DateTimeOffset.MinValue;
            _ = RefreshQuotesAsync(forceResetBackoff: false);
        }
    }

    public async Task ReplaceSettingsAsync(AppSettings imported)
    {
        imported.Normalize();
        lock (_settingsLock)
        {
            _settings = imported;
        }
        ApplySettingsSideEffects(imported);
        await _settingsStore.SaveAsync(imported).ConfigureAwait(false);
        SettingsUpdated?.Invoke(imported);
        _nextQuotePollUtc = DateTimeOffset.MinValue;
        _ = RefreshQuotesAsync(forceResetBackoff: true);
    }

    public void ResetPriceAlertState(string ruleId) => _priceAlertEvaluator.ResetRuleState(ruleId);

    public Task<IReadOnlyList<SearchResultItem>> SearchInstrumentsAsync(string query, CancellationToken ct = default) =>
        _aggregator.SearchAsync(query, ct);

    public Task<IntradaySeries> GetIntradayAsync(Instrument instrument, CancellationToken ct = default) =>
        _aggregator.GetIntradayAsync(instrument, ct);

    public IReadOnlyList<ProviderHealthSnapshot> GetProviderHealth() =>
        _aggregator.GetProviderHealthSnapshots();

    public Task<AlertHistoryData> LoadHistoryAsync(CancellationToken ct = default) =>
        _historyStore.LoadAsync(ct);

    public async Task ClearHistoryAsync(CancellationToken ct = default)
    {
        await _historyStore.ClearAsync(ct).ConfigureAwait(false);
        HistoryUpdated?.Invoke();
    }

    public Task ExportSettingsAsync(string targetPath, CancellationToken ct = default) =>
        _settingsStore.ExportAsync(Settings, targetPath, ct);

    public async Task<AppSettings> ImportSettingsAsync(string sourcePath, CancellationToken ct = default)
    {
        var imported = await _settingsStore.ImportAsync(sourcePath, ct).ConfigureAwait(false);
        await ReplaceSettingsAsync(imported).ConfigureAwait(false);
        return imported;
    }

    public string SettingsFilePath => _settingsStore.SettingsFilePath;
    public CalendarCoverageInfo CalendarCoverage => _calendar.CoverageInfo;

    private void ApplySettingsSideEffects(AppSettings s)
    {
        _aggregator.PrimaryProviderName = s.PrimaryProvider;
        _calendar.UpdateCustomOverrides(s.CustomHolidayDates, s.CustomOpenDates);
    }

    private List<Instrument> BuildRequiredInstrumentsLocked()
    {
        var byId = new Dictionary<InstrumentId, Instrument>();
        foreach (var w in _settings.Watchlist)
        {
            byId[w.Id] = w.Clone();
        }

        var catalog = DefaultPresets.CreateFullCatalog();

        // Keep polling instruments referenced by enabled reminder rules even if removed from Watchlist
        foreach (var rule in _settings.ReminderRules.Where(r => r.IsEnabled && r.TargetInstrumentId.HasValue))
        {
            var id = rule.TargetInstrumentId!.Value;
            if (!byId.ContainsKey(id))
            {
                var known = catalog.FirstOrDefault(c => c.Id == id);
                byId[id] = known?.Clone() ?? CreateFallbackInstrument(id, id.Code);
            }
        }

        // Keep polling instruments referenced by enabled price alert rules even if removed from Watchlist
        foreach (var alert in _settings.PriceAlerts.Where(a => a.IsEnabled))
        {
            if (!byId.ContainsKey(alert.InstrumentId))
            {
                var known = catalog.FirstOrDefault(c => c.Id == alert.InstrumentId);
                byId[alert.InstrumentId] = known?.Clone() ?? CreateFallbackInstrument(alert.InstrumentId, alert.InstrumentName);
            }
        }

        return byId.Values.ToList();
    }

    private static Instrument CreateFallbackInstrument(InstrumentId id, string? name) => new()
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(name) ? id.Code : name,
        DisplayCode = id.Code,
        Currency = ChinaTimeHelper.GetDefaultCurrency(id.Market)
    };

    public void Dispose()
    {
        _sessionState.SessionUnlocked -= OnSessionUnlocked;
        try
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
        }
        catch { }
        _refreshGate.Dispose();
        _schedulerGate.Dispose();
    }
}
