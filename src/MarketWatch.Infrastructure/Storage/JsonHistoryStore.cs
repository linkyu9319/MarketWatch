using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;

namespace MarketWatch.Infrastructure.Storage;

public sealed class JsonHistoryStore : IAlertHistoryStore
{
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private const int MaxRecordsPerCategory = 500;
    private const int DedupRetentionDays = 14;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class TriggerDedupState
    {
        public List<TriggerDedupEntry> Entries { get; set; } = new();
    }

    private sealed record TriggerDedupEntry(string TriggerKey, DateOnly BusinessDate);

    public string HistoryFilePath { get; }
    public string DedupStateFilePath { get; }

    public JsonHistoryStore(IAppLogger logger, string? baseDirectory = null)
    {
        _logger = logger;
        var dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketWatch");
        HistoryFilePath = Path.Combine(dir, "history.json");
        DedupStateFilePath = Path.Combine(dir, "trigger-state.json");
    }

    public async Task<AlertHistoryData> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task AppendReminderRecordAsync(ReminderTriggerRecord record, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            data.ReminderRecords.Insert(0, record);
            if (data.ReminderRecords.Count > MaxRecordsPerCategory)
                data.ReminderRecords.RemoveRange(MaxRecordsPerCategory, data.ReminderRecords.Count - MaxRecordsPerCategory);
            await JsonSettingsStore.WriteAtomicCoreAsync(HistoryFilePath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task AppendPriceAlertRecordAsync(PriceAlertRecord record, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            data.PriceAlertRecords.Insert(0, record);
            if (data.PriceAlertRecords.Count > MaxRecordsPerCategory)
                data.PriceAlertRecords.RemoveRange(MaxRecordsPerCategory, data.PriceAlertRecords.Count - MaxRecordsPerCategory);
            await JsonSettingsStore.WriteAtomicCoreAsync(HistoryFilePath, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>
    /// Clears user-visible history records ONLY. Never erases trigger-state.json so restart deduplication stays intact.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonSettingsStore.WriteAtomicCoreAsync(HistoryFilePath, new AlertHistoryData(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<IReadOnlySet<string>> LoadDedupKeysAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadDedupStateCoreAsync(cancellationToken).ConfigureAwait(false);
            return state.Entries
                .Select(e => e.TriggerKey)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("History", $"Dedup state unavailable at startup; using visible history: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task RecordDedupKeysAsync(IEnumerable<ReminderTriggerRecord> records, CancellationToken cancellationToken = default)
    {
        var list = records.Where(r => !string.IsNullOrWhiteSpace(r.TriggerKey)).ToList();
        if (list.Count == 0)
            return;

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadDedupStateCoreAsync(cancellationToken).ConfigureAwait(false);
            var existing = state.Entries.Select(e => e.TriggerKey).ToHashSet(StringComparer.Ordinal);

            var latestDate = list.Max(r => r.BusinessDate);
            foreach (var rec in list)
            {
                if (existing.Add(rec.TriggerKey))
                {
                    state.Entries.Add(new TriggerDedupEntry(rec.TriggerKey, rec.BusinessDate));
                }
            }

            // Retain recent 14 days so state file stays small without affecting active dedup
            var cutoffDate = latestDate.AddDays(-DedupRetentionDays);
            state.Entries.RemoveAll(e => e.BusinessDate < cutoffDate);

            await JsonSettingsStore.WriteAtomicCoreAsync(DedupStateFilePath, state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<AlertHistoryData> LoadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(HistoryFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<AlertHistoryData>(json, JsonOptions) ?? new AlertHistoryData();
            data.ReminderRecords ??= new();
            data.PriceAlertRecords ??= new();
            return data;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AlertHistoryData();
        }
        catch (JsonException ex)
        {
            _logger.Warn("History", $"History file corrupted; resetting. Error: {ex.Message}");
            return new AlertHistoryData();
        }
    }

    private async Task<TriggerDedupState> LoadDedupStateCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(DedupStateFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<TriggerDedupState>(json, JsonOptions) ?? new TriggerDedupState();
            state.Entries ??= new();
            return state;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new TriggerDedupState();
        }
        catch (JsonException ex)
        {
            _logger.Warn("History", $"Trigger dedup state corrupted; resetting. Error: {ex.Message}");
            return new TriggerDedupState();
        }
    }
}
