#if WINDOWS
using MarketWatch.App.Services;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using MarketWatch.Infrastructure.Providers;
using MarketWatch.Infrastructure.Storage;
using Xunit;

namespace MarketWatch.Tests;

public sealed class AppStateCoordinatorTests
{
    [Fact]
    public async Task NewReminder_PersistsKeysImmediatelyDuringIdleRetryBackoff()
    {
        var directory = Directory.CreateTempSubdirectory("MarketWatch_Test_");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.FromHours(8)));
            var logger = new NullLogger();
            var calendar = new TradingCalendarService();
            var history = new JsonHistoryStore(logger, directory.FullName);
            var settings = new JsonSettingsStore(logger, directory.FullName);
            var rules = DefaultPresets.CreateDefaultReminderRules();
            var legacy = Assert.Single(new ReminderScheduler(clock, calendar).EvaluateTick(rules).RecordsToPersist);
            await history.AppendReminderRecordAsync(legacy);
            await File.WriteAllTextAsync(history.DedupStateFilePath, """{"entries":[]}""");

            var desktop = new TestDesktop();
            var quotes = new QuoteAggregatorService([], clock, logger);
            using var app = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
            await using (var locked = new FileStream(history.DedupStateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                await app.InitializeAsync();
            }

            await app.TriggerImmediateSchedulerCheckAsync();
            Assert.Empty(await history.LoadDedupKeysAsync());

            clock.Advance(TimeSpan.FromMinutes(15));
            await app.TriggerImmediateSchedulerCheckAsync();

            var keys = await history.LoadDedupKeysAsync();
            Assert.Contains(legacy.TriggerKey, keys);
            Assert.Contains(ReminderScheduler.BuildTriggerKey(rules[1].Id, legacy.BusinessDate, rules[1].TriggerTime), keys);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UnreadableLegacyHistory_DoesNotStopStartupAndIsMigratedBeforeClearing()
    {
        var directory = Directory.CreateTempSubdirectory("MarketWatch_Test_");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.FromHours(8)));
            var logger = new NullLogger();
            var calendar = new TradingCalendarService();
            var history = new JsonHistoryStore(logger, directory.FullName);
            var settings = new JsonSettingsStore(logger, directory.FullName);
            var rules = DefaultPresets.CreateDefaultReminderRules();
            var legacy = Assert.Single(new ReminderScheduler(clock, calendar).EvaluateTick(rules).RecordsToPersist);
            await history.AppendReminderRecordAsync(legacy);

            var desktop = new TestDesktop();
            var quotes = new QuoteAggregatorService([], clock, logger);
            using var app = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
            await using (var locked = new FileStream(history.HistoryFilePath, FileMode.Open, FileAccess.Read, FileShare.Delete))
            {
                await Assert.ThrowsAsync<IOException>(() => history.LoadAsync());
                await app.InitializeAsync();
                Assert.False(await app.ClearHistoryAsync());
            }

            Assert.Equal(legacy, Assert.Single((await history.LoadAsync()).ReminderRecords));
            Assert.True(await app.ClearHistoryAsync());
            Assert.Empty((await history.LoadAsync()).ReminderRecords);
            Assert.Contains(legacy.TriggerKey, await history.LoadDedupKeysAsync());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LockedDedupFile_KeepsHistoryUntilKeysAreSavedAndLeavesNoTempFiles()
    {
        var directory = Directory.CreateTempSubdirectory("MarketWatch_Test_");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 14, 30, 0, TimeSpan.FromHours(8)));
            var logger = new NullLogger();
            var calendar = new TradingCalendarService();
            var history = new JsonHistoryStore(logger, directory.FullName);
            var settings = new JsonSettingsStore(logger, directory.FullName);
            var rules = DefaultPresets.CreateDefaultReminderRules();
            var legacy = Assert.Single(new ReminderScheduler(clock, calendar).EvaluateTick(rules).RecordsToPersist);
            await history.AppendReminderRecordAsync(legacy);
            await File.WriteAllTextAsync(history.DedupStateFilePath, """{"entries":[]}""");

            var desktop = new TestDesktop();
            var quotes = new QuoteAggregatorService([], clock, logger);
            using (var app = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger))
            {
                await using (var locked = new FileStream(history.DedupStateFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Reading still works. Windows rejects File.Replace without FileShare.Delete.
                    await app.InitializeAsync();
                    await app.TriggerImmediateSchedulerCheckAsync();
                    Assert.False(await app.ClearHistoryAsync());
                    Assert.Equal(legacy, Assert.Single((await history.LoadAsync()).ReminderRecords));
                    Assert.Empty(directory.GetFiles("*.tmp"));
                }

                Assert.True(await app.ClearHistoryAsync());
            }

            using var restarted = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
            await restarted.InitializeAsync();
            await restarted.TriggerImmediateSchedulerCheckAsync();
            Assert.Empty((await history.LoadAsync()).ReminderRecords);
            Assert.Contains(legacy.TriggerKey, await history.LoadDedupKeysAsync());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class TestDesktop : IAutoStartService, ISessionStateProvider
    {
        public bool IsSessionLocked => false;
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
        public event Action? SessionUnlocked { add { } remove { } }
    }
}
#endif
