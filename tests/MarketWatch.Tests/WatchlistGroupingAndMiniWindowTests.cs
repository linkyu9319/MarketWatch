#if WINDOWS
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using MarketWatch.App.Services;
using MarketWatch.App.ViewModels;
using MarketWatch.App.Views;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using MarketWatch.Infrastructure.Providers;
using MarketWatch.Infrastructure.Storage;
using Xunit;

namespace MarketWatch.Tests;

[Collection("WpfUi")]
public sealed class WatchlistGroupingAndMiniWindowTests(WpfApplicationFixture wpf)
{
    [Fact]
    public Task MarketGroupTabs_FilterWatchlist_ComputeCounts_AndReorderWithinActiveGroup() =>
        wpf.RunOnUiAsync(async () =>
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
                await wpf.DrainUiQueueAsync();
                Assert.Equal(DefaultPresets.QqqId, mainVm.WatchlistItems[0].Id);
                Assert.Equal(DefaultPresets.NdxId, mainVm.WatchlistItems[1].Id);

                // Switch to MiniWindow tab and uncheck all items
                mainVm.SelectGroupTabCommand.Execute("MiniWindow");
                Assert.Equal(3, mainVm.WatchlistItems.Count);

                foreach (var item in mainVm.WatchlistItems.ToList())
                {
                    await mainVm.ToggleMiniWindowMembershipCommand.ExecuteAsync(item);
                    await wpf.DrainUiQueueAsync();
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
        });

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

    [Fact]
    public Task SwitchingMarketGroup_ClosesAndStopsRefreshingOutOfGroupIntradayChart() =>
        wpf.RunOnUiAsync(async () =>
        {
            var dir = Directory.CreateTempSubdirectory("MarketWatch_IntradayGroup_");
            try
            {
                var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 10, 30, 0, TimeSpan.FromHours(8)));
                var logger = new NullLogger();
                var calendar = new TradingCalendarService();
                var settings = new JsonSettingsStore(logger, dir.FullName);
                var history = new JsonHistoryStore(logger, dir.FullName);
                var desktop = new FakeDesktop();
                var countingProvider = new CountingIntradayProvider();
                var quotes = new QuoteAggregatorService([countingProvider], clock, logger);

                using var coordinator = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
                await coordinator.InitializeAsync();

                var mainVm = new MainViewModel(coordinator, clock, new ThemeManager(), new StubUpdateService());

                // Open intraday chart on HSTECH (HK)
                var hstech = mainVm.WatchlistItems.First(x => x.Id == DefaultPresets.HstechId);
                mainVm.InspectIntradayCommand.Execute(hstech);
                await mainVm.IntradayChart.LoadForInstrumentAsync(hstech.Instrument);

                Assert.True(mainVm.IsIntradayDrawerOpen);
                Assert.Equal(DefaultPresets.HstechId, mainVm.IntradayChart.CurrentInstrument?.Id);
                Assert.True(mainVm.IntradayChart.HasChartData);
                var requestsBeforeGroupSwitch = countingProvider.IntradayRequestCount;

                // Switching from All -> HK keeps HSTECH visible so chart stays open
                mainVm.SelectGroupTabCommand.Execute("HK");
                Assert.True(mainVm.IsIntradayDrawerOpen);
                Assert.Equal(DefaultPresets.HstechId, mainVm.IntradayChart.CurrentInstrument?.Id);

                // Switching to US hides HSTECH -> must close drawer, clear chart, and prevent stale refresh
                mainVm.SelectGroupTabCommand.Execute("US");
                Assert.False(mainVm.IsIntradayDrawerOpen);
                Assert.Null(mainVm.SelectedItem);
                Assert.Null(mainVm.IntradayChart.CurrentInstrument);
                Assert.Null(mainVm.IntradayChart.CurrentSeries);
                Assert.False(mainVm.IntradayChart.HasChartData);

                await mainVm.IntradayChart.RefreshIntradayCommand.ExecuteAsync(null);
                Assert.Equal(requestsBeforeGroupSwitch, countingProvider.IntradayRequestCount);
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        });

    [Fact]
    public Task FailedSave_RollsBackMiniWindowCheckbox_SoMainGridAndMiniWindowStayConsistent() =>
        wpf.RunOnUiAsync(async () =>
        {
            var dir = Directory.CreateTempSubdirectory("MarketWatch_SaveFail_");
            try
            {
                var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 10, 30, 0, TimeSpan.FromHours(8)));
                var logger = new NullLogger();
                var calendar = new TradingCalendarService();
                var realStore = new JsonSettingsStore(logger, dir.FullName);
                var failableStore = new FailableSettingsStore(realStore);
                var history = new JsonHistoryStore(logger, dir.FullName);
                var desktop = new FakeDesktop();
                var quotes = new QuoteAggregatorService([], clock, logger);

                using var coordinator = new AppStateCoordinator(clock, quotes, calendar, failableStore, history, desktop, desktop, logger);
                await coordinator.InitializeAsync();

                var themeManager = new ThemeManager();
                var mainVm = new MainViewModel(coordinator, clock, themeManager, new StubUpdateService());
                var miniVm = new MiniWindowViewModel(coordinator, clock, themeManager);

                Assert.Equal(5, miniVm.CompactItems.Count);
                Assert.Equal("小窗 (5)", mainVm.MiniTabLabel);

                // Simulate disk write failure on SaveAsync via direct checkbox toggle
                failableStore.FailNextSave = true;
                mainVm.WatchlistItems[0].ShowInMiniWindow = false;
                await mainVm.LastPendingMiniToggleTask;
                await wpf.DrainUiQueueAsync();

                // Coordinator settings, main grid checkbox, tab label, and mini window must ALL remain consistent (true / 5)
                Assert.True(coordinator.Settings.Watchlist[0].ShowInMiniWindow);
                Assert.True(mainVm.WatchlistItems[0].ShowInMiniWindow);
                Assert.Equal("小窗 (5)", mainVm.MiniTabLabel);
                Assert.Equal(5, miniVm.CompactItems.Count);
                Assert.Contains("保存悬浮小窗设置失败", mainVm.BannerMessage ?? "");

                // Same consistency guarantee via context-menu command
                failableStore.FailNextSave = true;
                await mainVm.ToggleMiniWindowMembershipCommand.ExecuteAsync(mainVm.WatchlistItems[1]);
                await wpf.DrainUiQueueAsync();
                Assert.True(coordinator.Settings.Watchlist[1].ShowInMiniWindow);
                Assert.True(mainVm.WatchlistItems[1].ShowInMiniWindow);
                Assert.Equal(5, miniVm.CompactItems.Count);
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        });

    [Fact]
    public Task MoveUpAndDown_AfterColumnSort_ReordersByVisualSortedOrderAndClearsSort() =>
        wpf.RunOnUiAsync(async () =>
        {
            var dir = Directory.CreateTempSubdirectory("MarketWatch_SortMove_");
            try
            {
                var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 10, 30, 0, TimeSpan.FromHours(8)));
                var logger = new NullLogger();
                var calendar = new TradingCalendarService();
                var settings = new JsonSettingsStore(logger, dir.FullName);
                var history = new JsonHistoryStore(logger, dir.FullName);
                var desktop = new FakeDesktop();
                var quotes = new QuoteAggregatorService([], clock, logger);

                using var coordinator = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
                await coordinator.InitializeAsync();

                var mainVm = new MainViewModel(coordinator, clock, new ThemeManager(), new StubUpdateService());
                var clearSortRaised = false;
                mainVm.ColumnSortClearRequested += () => clearSortRaised = true;

                // Sort descending by DisplayCode: SH 000001, QQQ, NDX, HSTECH, HSI
                var view = CollectionViewSource.GetDefaultView(mainVm.WatchlistItems);
                view.SortDescriptions.Add(new SortDescription(nameof(WatchlistItemViewModel.DisplayCode), ListSortDirection.Descending));
                var visualBefore = view.Cast<WatchlistItemViewModel>().Select(x => x.Id).ToList();
                Assert.Equal(
                    [DefaultPresets.ShCompositeId, DefaultPresets.QqqId, DefaultPresets.NdxId, DefaultPresets.HstechId, DefaultPresets.HsiId],
                    visualBefore);

                // Move NDX (visual index 2) UP -> must swap with visual neighbor QQQ (visual index 1), NOT underlying neighbor SH000001!
                var ndxItem = view.Cast<WatchlistItemViewModel>().ElementAt(2);
                await mainVm.MoveItemUpCommand.ExecuteAsync(ndxItem);
                await wpf.DrainUiQueueAsync();

                Assert.True(clearSortRaised);
                Assert.Empty(view.SortDescriptions);
                Assert.Equal(
                    [DefaultPresets.ShCompositeId, DefaultPresets.NdxId, DefaultPresets.QqqId, DefaultPresets.HstechId, DefaultPresets.HsiId],
                    mainVm.WatchlistItems.Select(x => x.Id).ToList());
                Assert.Equal(
                    [DefaultPresets.ShCompositeId, DefaultPresets.NdxId, DefaultPresets.QqqId, DefaultPresets.HstechId, DefaultPresets.HsiId],
                    coordinator.Settings.Watchlist.Select(x => x.Id).ToList());

                // MoveDown while sorted inside a filtered market group (HK tab: sort asc -> HSI, HSTECH)
                mainVm.SelectGroupTabCommand.Execute("HK");
                view.SortDescriptions.Add(new SortDescription(nameof(WatchlistItemViewModel.DisplayCode), ListSortDirection.Ascending));
                var hkVisualBefore = view.Cast<WatchlistItemViewModel>().Select(x => x.Id).ToList();
                Assert.Equal([DefaultPresets.HsiId, DefaultPresets.HstechId], hkVisualBefore);

                // Move HSI (visual index 0) DOWN -> becomes [HSTECH, HSI], while non-HK slots stay intact
                var hsiItem = view.Cast<WatchlistItemViewModel>().ElementAt(0);
                await mainVm.MoveItemDownCommand.ExecuteAsync(hsiItem);
                await wpf.DrainUiQueueAsync();

                Assert.Empty(view.SortDescriptions);
                Assert.Equal([DefaultPresets.HstechId, DefaultPresets.HsiId], mainVm.WatchlistItems.Select(x => x.Id).ToList());
                Assert.Equal(
                    [DefaultPresets.ShCompositeId, DefaultPresets.NdxId, DefaultPresets.QqqId, DefaultPresets.HstechId, DefaultPresets.HsiId],
                    coordinator.Settings.Watchlist.Select(x => x.Id).ToList());
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        });

    [Fact]
    public Task RightClickContextMenu_SelectsClickedRowAndSuppressesEmptyAreaClicks() =>
        wpf.RunOnUiAsync(async () =>
        {
            var dir = Directory.CreateTempSubdirectory("MarketWatch_RightClick_");
            try
            {
                var clock = new FakeClock(new DateTimeOffset(2026, 11, 27, 10, 30, 0, TimeSpan.FromHours(8)));
                var logger = new NullLogger();
                var calendar = new TradingCalendarService();
                var settings = new JsonSettingsStore(logger, dir.FullName);
                var history = new JsonHistoryStore(logger, dir.FullName);
                var desktop = new FakeDesktop();
                var quotes = new QuoteAggregatorService([], clock, logger);
                using var coordinator = new AppStateCoordinator(clock, quotes, calendar, settings, history, desktop, desktop, logger);
                await coordinator.InitializeAsync();

                var themeManager = new ThemeManager();
                var mainVm = new MainViewModel(coordinator, clock, themeManager, new StubUpdateService());
                var window = new MainWindow(mainVm, coordinator, themeManager)
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Opacity = 0,
                    Left = -30000,
                    Top = -30000
                };

                try
                {
                    window.Show();
                    window.UpdateLayout();

                    var grid = (DataGrid)window.FindName("WatchlistGrid");
                    Assert.NotNull(grid);

                    // Select row 0 (HSTECH)
                    grid.SelectedItem = mainVm.WatchlistItems[0];
                    Assert.Equal(DefaultPresets.HstechId, mainVm.SelectedItem?.Id);

                    // Simulate right-click on row 1 (HSI) -> must re-select row 1 before context menu command executes
                    grid.UpdateLayout();
                    var row1 = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
                    Assert.NotNull(row1);

                    var allowOnRow1 = window.TryPrepareContextMenuTarget(row1, isKeyboardInvoked: false);
                    Assert.True(allowOnRow1);
                    Assert.Equal(DefaultPresets.HsiId, mainVm.SelectedItem?.Id);

                    // Simulate right-click on empty grid background (not a DataGridRow) -> must suppress menu
                    var allowOnEmptySpace = window.TryPrepareContextMenuTarget(grid, isKeyboardInvoked: false);
                    Assert.False(allowOnEmptySpace);
                }
                finally
                {
                    window.AllowTrueClose = true;
                    window.Close();
                }
            }
            finally
            {
                try { dir.Delete(recursive: true); } catch { }
            }
        });

    private sealed class FailableSettingsStore(ISettingsStore inner) : ISettingsStore
    {
        public bool FailNextSave { get; set; }
        public string SettingsFilePath => inner.SettingsFilePath;
        public Task<SettingsLoadResult> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Simulated settings.json write lock");
            }
            return inner.SaveAsync(settings, ct);
        }
        public Task ExportAsync(AppSettings settings, string targetPath, CancellationToken ct = default) => inner.ExportAsync(settings, targetPath, ct);
        public Task<AppSettings> ImportAsync(string sourcePath, CancellationToken ct = default) => inner.ImportAsync(sourcePath, ct);
    }

    private sealed class CountingIntradayProvider : IQuoteProvider
    {
        public int IntradayRequestCount { get; private set; }
        public string Name => "Eastmoney";
        public ProviderCapabilities Capabilities => ProviderCapabilities.BatchQuotes | ProviderCapabilities.IntradayHk | ProviderCapabilities.IntradayCn | ProviderCapabilities.IntradayUs;
        public Task<IReadOnlyDictionary<InstrumentId, Quote>> GetQuotesAsync(IReadOnlyList<Instrument> instruments, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<InstrumentId, Quote>>(new Dictionary<InstrumentId, Quote>());
        public Task<IReadOnlyList<SearchResultItem>> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SearchResultItem>>([]);
        public Task<IntradaySeries> GetIntradayAsync(Instrument instrument, CancellationToken ct = default)
        {
            IntradayRequestCount++;
            var t1 = new DateTimeOffset(2026, 11, 27, 9, 30, 0, TimeSpan.FromHours(8));
            var t2 = new DateTimeOffset(2026, 11, 27, 9, 31, 0, TimeSpan.FromHours(8));
            return Task.FromResult(new IntradaySeries
            {
                InstrumentId = instrument.Id,
                TradingDate = new DateOnly(2026, 11, 27),
                PreviousClose = 3500m,
                Points = [new IntradayPoint(t1, 3510m), new IntradayPoint(t2, 3525m)],
                Source = Name,
                IsSupported = true
            });
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
        public string CurrentVersion => "1.0.8";
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
