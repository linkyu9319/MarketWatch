#if WINDOWS
using MarketWatch.App.Services;
using MarketWatch.App.ViewModels;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using MarketWatch.Infrastructure.Providers;
using MarketWatch.Infrastructure.Storage;
using Xunit;

namespace MarketWatch.Tests;

[Collection("WpfUi")]
public sealed class WatchlistGroupingAndMiniWindowTests
{
    [Fact]
    public async Task MarketGroupTabs_FilterWatchlist_ComputeCounts_AndReorderWithinActiveGroup()
    {
        var dir = Directory.CreateTempSubdirectory("MarketWatch_GroupTest_");
        try
        {
            var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.FromHours(8)));
            var logger = new NullLogger();
            var calendar = new TradingCalendarService();
            var settings = new JsonSettingsStore(logger, dir.FullName);
            var history = new JsonHistoryStore(logger, dir.FullName);
            var desktop = new FakeDesktop();
            var quotes = new QuoteAggregatorService([], clock, logger);

            var initial = DefaultPresets.CreateDefaultSettings();
            // Default: HSTECH (HK), HSI (HK), SH000001 (CN), NDX (US), QQQ (US)
            initial.Watchlist[1].ShowInMiniWindow = false; // HSI unchecked
            initial.Watchlist[4].ShowInMiniWindow = false; // QQQ unchecked
            await settings.SaveAsync(initial);

            using var coordinator = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
            await coordinator.InitializeAsync();

            var themeManager = new ThemeManager();
            var mainVm = new MainViewModel(coordinator, clock, themeManager, new StubUpdateService());
            var miniVm = new MiniWindowViewModel(coordinator, clock, themeManager);

            // Verify initial counts and mini-window filtering
            Assert.Equal("全部 (5)", mainVm.AllTabLabel);
            Assert.Equal("港股 (2)", mainVm.HkTabLabel);
            Assert.Equal("沪深 (1)", mainVm.CnTabLabel);
            Assert.Equal("美股 (2)", mainVm.UsTabLabel);
            Assert.Equal("小窗 (3)", mainVm.MiniTabLabel);
            Assert.Equal(5, mainVm.WatchlistItems.Count);
            Assert.Equal(3, miniVm.CompactItems.Count);
            Assert.True(miniVm.HasCompactItems);
            Assert.DoesNotContain(miniVm.CompactItems, x => x.Id == DefaultPresets.HsiId);
            Assert.DoesNotContain(miniVm.CompactItems, x => x.Id == DefaultPresets.QqqId);

            // Switch to HK tab
            mainVm.SelectGroupTabCommand.Execute("HK");
            Assert.Equal(2, mainVm.WatchlistItems.Count);
            Assert.All(mainVm.WatchlistItems, i => Assert.Equal(Market.HK, i.Id.Market));

            // Switch to CN tab
            mainVm.SelectGroupTabCommand.Execute("CN");
            Assert.Single(mainVm.WatchlistItems);
            Assert.Equal(DefaultPresets.ShCompositeId, mainVm.WatchlistItems[0].Id);

            // Switch to US tab and move QQQ up above NDX
            mainVm.SelectGroupTabCommand.Execute("US");
            Assert.Equal(2, mainVm.WatchlistItems.Count);
            Assert.Equal(DefaultPresets.NdxId, mainVm.WatchlistItems[0].Id);
            Assert.Equal(DefaultPresets.QqqId, mainVm.WatchlistItems[1].Id);

            await mainVm.MoveItemUpCommand.ExecuteAsync(mainVm.WatchlistItems[1]);
            Assert.Equal(DefaultPresets.QqqId, mainVm.WatchlistItems[0].Id);
            Assert.Equal(DefaultPresets.NdxId, mainVm.WatchlistItems[1].Id);

            // Switch to MiniWindow tab and uncheck all items
            mainVm.SelectGroupTabCommand.Execute("MiniWindow");
            Assert.Equal(3, mainVm.WatchlistItems.Count);

            foreach (var item in mainVm.WatchlistItems.ToList())
            {
                await mainVm.ToggleMiniWindowMembershipCommand.ExecuteAsync(item);
            }

            Assert.Equal("小窗 (0)", mainVm.MiniTabLabel);
            Assert.Empty(mainVm.WatchlistItems);
            Assert.True(mainVm.IsCurrentGroupEmpty);
            Assert.Empty(miniVm.CompactItems);
            Assert.False(miniVm.HasCompactItems);

            // Reload from disk and verify ShowInMiniWindow=false persisted cleanly
            var reloaded = await settings.LoadAsync();
            Assert.All(reloaded.Settings.Watchlist, w => Assert.False(w.ShowInMiniWindow));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LegacySettingsWithoutShowInMiniWindow_DefaultsToTrueForBackwardCompatibility()
    {
        var dir = Directory.CreateTempSubdirectory("MarketWatch_LegacyMini_");
        try
        {
            var logger = new NullLogger();
            var store = new JsonSettingsStore(logger, dir.FullName);
            await File.WriteAllTextAsync(store.SettingsFilePath, """
            {
              "schemaVersion": 1,
              "watchlist": [
                { "id": "HK:INDEX:HSTECH", "name": "恒生科技指数", "displayCode": "HSTECH" }
              ]
            }
            """);

            var loaded = await store.LoadAsync();
            var inst = Assert.Single(loaded.Settings.Watchlist);
            Assert.True(inst.ShowInMiniWindow);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private sealed class FakeDesktop : IAutoStartService, ISessionStateProvider
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) { }
        public bool IsSessionLocked => false;
        public event Action? SessionUnlocked { add { } remove { } }
    }

    private sealed class StubUpdateService : IAppUpdateService
    {
        public string CurrentVersion => "1.0.7";
        public string ReleasesPageUrl => "https://github.com/linkyu9319/MarketWatch/releases";
        public Task<UpdateStatus> CheckForUpdatesAsync(CancellationToken ct = default) =>
            Task.FromResult(new UpdateStatus
            {
                State = UpdateCheckState.Current,
                CurrentVersion = CurrentVersion,
                Message = "当前已是最新版本"
            });
        public Task<UpdateInstallResult> DownloadAndStageUpdateAsync(
            IProgress<(long Received, long Total, string Stage)>? progress = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public bool CanApplySelfUpdate(out string reason)
        {
            reason = string.Empty;
            return false;
        }
        public void LaunchSelfUpdateScript(string stagedExePath) => throw new NotSupportedException();
    }
}
#endif
