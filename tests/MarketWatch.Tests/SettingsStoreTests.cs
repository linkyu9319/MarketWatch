using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Storage;
using Xunit;

namespace MarketWatch.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "MarketWatch_Test_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndReload_PersistsWatchlistAndReminderRulesAtomically()
    {
        var store = new JsonSettingsStore(new NullLogger(), _tempDir);
        var loaded = await store.LoadAsync();
        Assert.False(loaded.WasCorrupted);
        Assert.Equal(5, loaded.Settings.Watchlist.Count);
        Assert.Equal(3, loaded.Settings.ReminderRules.Count);

        loaded.Settings.ThemeMode = ThemeMode.Light;
        loaded.Settings.RefreshIntervalSeconds = 15;
        loaded.Settings.ReminderRules[0].TriggerTime = new TimeOnly(14, 25);
        await store.SaveAsync(loaded.Settings);

        var reloaded = await store.LoadAsync();
        Assert.Equal(ThemeMode.Light, reloaded.Settings.ThemeMode);
        Assert.Equal(15, reloaded.Settings.RefreshIntervalSeconds);
        Assert.Equal(new TimeOnly(14, 25), reloaded.Settings.ReminderRules[0].TriggerTime);
    }

    [Fact]
    public async Task CorruptConfigFile_BacksUpAndFallsBackToDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        var store = new JsonSettingsStore(new NullLogger(), _tempDir);
        await File.WriteAllTextAsync(store.SettingsFilePath, "{ CORRUPTED JSON :::");

        var result = await store.LoadAsync();
        Assert.True(result.WasCorrupted);
        Assert.NotNull(result.BackupFilePath);
        Assert.True(File.Exists(result.BackupFilePath));
        Assert.Equal(5, result.Settings.Watchlist.Count);
        Assert.Equal(3, result.Settings.ReminderRules.Count);
    }

    [Fact]
    public async Task ExportAndImport_RoundTripsCleanly()
    {
        var store = new JsonSettingsStore(new NullLogger(), _tempDir);
        var settings = DefaultPresets.CreateDefaultSettings();
        settings.ColorScheme = ColorScheme.InternationalGreenUpRedDown;
        settings.CustomHolidayDates.Add(new DateOnly(2026, 12, 25));

        var exportFile = Path.Combine(_tempDir, "exported.json");
        await store.ExportAsync(settings, exportFile);

        var imported = await store.ImportAsync(exportFile);
        Assert.Equal(ColorScheme.InternationalGreenUpRedDown, imported.ColorScheme);
        Assert.Contains(new DateOnly(2026, 12, 25), imported.CustomHolidayDates);
    }

    [Fact]
    public async Task HistoryClear_PreservesRestartDeduplicationState()
    {
        var historyStore = new JsonHistoryStore(new NullLogger(), _tempDir);
        var rec = new ReminderTriggerRecord
        {
            TriggerKey = "hstech-1430|2026-11-27|14:30",
            RuleId = "hstech-1430",
            RuleName = "恒生科技基金交易提醒",
            BusinessDate = new DateOnly(2026, 11, 27),
            ScheduledTime = new TimeOnly(14, 30),
            TriggeredAt = DateTimeOffset.UtcNow,
            Status = ReminderTriggerStatus.Triggered
        };

        await historyStore.RecordDedupKeysAsync([rec]);
        await historyStore.AppendReminderRecordAsync(rec);

        // User clears visible history
        await historyStore.ClearAsync();
        var visible = await historyStore.LoadAsync();
        Assert.Empty(visible.ReminderRecords);

        // Independent dedup keys MUST survive restart!
        var dedupKeys = await historyStore.LoadDedupKeysAsync();
        Assert.Contains(rec.TriggerKey, dedupKeys);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }
}
