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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string HistoryFilePath { get; }

    public JsonHistoryStore(IAppLogger logger, string? baseDirectory = null)
    {
        _logger = logger;
        var dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketWatch");
        HistoryFilePath = Path.Combine(dir, "history.json");
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

    private async Task<AlertHistoryData> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(HistoryFilePath))
            return new AlertHistoryData();

        try
        {
            var json = await File.ReadAllTextAsync(HistoryFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<AlertHistoryData>(json, JsonOptions) ?? new AlertHistoryData();
            data.ReminderRecords ??= new();
            data.PriceAlertRecords ??= new();
            return data;
        }
        catch (Exception ex)
        {
            _logger.Warn("History", $"History file corrupted; resetting. Error: {ex.Message}");
            return new AlertHistoryData();
        }
    }
}
