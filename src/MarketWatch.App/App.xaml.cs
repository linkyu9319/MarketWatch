using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MarketWatch.App.Services;
using MarketWatch.App.ViewModels;
using MarketWatch.App.Views;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using MarketWatch.Infrastructure.Calendar;
using MarketWatch.Infrastructure.Logging;
using MarketWatch.Infrastructure.Providers;
using MarketWatch.Infrastructure.Storage;
using MarketWatch.Infrastructure.Time;
using MarketWatch.Infrastructure.Updates;

namespace MarketWatch.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;
    private SingleInstanceService? _singleInstance;
    private TrayIconService? _trayService;
    private MainWindow? _mainWindow;
    private MiniFloatingWindow? _miniWindow;
    private AppStateCoordinator? _coordinator;
    private ReminderPresentationService? _reminderPresenter;
    private IAppLogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logger = new RollingFileLogger();
        RegisterGlobalExceptionHandlers();

        // Single-instance lock: prevent duplicate background alarms
        _singleInstance = new SingleInstanceService(_logger);
        if (!_singleInstance.TryAcquirePrimary())
        {
            SingleInstanceService.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        try
        {
            _services = BuildServiceProvider(_logger);

            _coordinator = _services.GetRequiredService<AppStateCoordinator>();
            var themeManager = _services.GetRequiredService<ThemeManager>();
            _reminderPresenter = _services.GetRequiredService<ReminderPresentationService>();

            await _coordinator.InitializeAsync();
            themeManager.Apply(_coordinator.Settings.ThemeMode, _coordinator.Settings.ColorScheme);

            var mainVm = _services.GetRequiredService<MainViewModel>();
            _mainWindow = new MainWindow(mainVm, _coordinator, themeManager);
            MainWindow = _mainWindow;

            WireUiEvents(mainVm);
            SetupTrayIcon();

            _singleInstance.ActivationRequested += () =>
            {
                Dispatcher.BeginInvoke(() => _mainWindow?.RestoreAndFocus());
            };

            _coordinator.ReminderDue += ev => _reminderPresenter.ShowScheduledReminder(ev);
            _coordinator.PriceAlertTriggered += alert => _reminderPresenter.ShowPriceAlert(alert);
            _coordinator.QuotesUpdated += resp => UpdateTraySummary(resp);
            _coordinator.SettingsUpdated += _ => UpdateTraySummary(null);

            var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (!startMinimized)
            {
                _mainWindow.Show();
            }

            if (_coordinator.Settings.MiniWindowVisible)
            {
                EnsureMiniWindowShown();
            }

            _coordinator.StartBackgroundLoops();
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.TimeChanged += OnSystemTimeChanged;
            _logger.Info("App", $"MarketWatch started (startMinimized={startMinimized}).");
        }
        catch (Exception ex)
        {
            _logger.Error("App", "Fatal startup failure", ex);
            System.Windows.MessageBox.Show(
                $"MarketWatch 启动失败：{ex.Message}",
                "MarketWatch",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static ServiceProvider BuildServiceProvider(IAppLogger logger)
    {
        var sc = new ServiceCollection();

        sc.AddSingleton(logger);
        sc.AddSingleton<IClock, SystemClock>();
        sc.AddSingleton<ITradingCalendar, TradingCalendarService>();
        sc.AddSingleton<ISettingsStore, JsonSettingsStore>();
        sc.AddSingleton<IAlertHistoryStore, JsonHistoryStore>();
        sc.AddSingleton<IAutoStartService, WindowsAutoStartService>();
        sc.AddSingleton<IGitHubTokenStore, DpapiGitHubTokenStore>();
        sc.AddSingleton<IAppUpdateService, GitHubUpdateService>();

        sc.AddSingleton(_ =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) MarketWatch/1.0");
            return client;
        });

        sc.AddSingleton<IQuoteProvider, TencentQuoteProvider>();
        sc.AddSingleton<IQuoteProvider, EastmoneyQuoteProvider>();
        sc.AddSingleton<IQuoteAggregator, QuoteAggregatorService>();

        sc.AddSingleton<ThemeManager>();
        sc.AddSingleton<AppStateCoordinator>();
        sc.AddSingleton<ReminderPresentationService>();
        sc.AddSingleton<MainViewModel>();
        sc.AddSingleton<MiniWindowViewModel>();

        return sc.BuildServiceProvider();
    }

    private void WireUiEvents(MainViewModel mainVm)
    {
        mainVm.OpenSearchRequested += OpenSearchDialog;
        mainVm.OpenSettingsRequested += OpenSettingsDialog;
        mainVm.OpenHistoryRequested += OpenHistoryDialog;
        mainVm.OpenUpdatesRequested += OpenUpdatesDialog;
        mainVm.ToggleMiniWindowRequested += ToggleMiniWindow;
        mainVm.TestReminderRequested += TriggerTestReminder;
        mainVm.AddPriceAlertForInstrumentRequested += OpenQuickPriceAlertDialog;
    }

    private void SetupTrayIcon()
    {
        _trayService = new TrayIconService();
        _trayService.OpenMainRequested += () => Dispatcher.BeginInvoke(() => _mainWindow?.RestoreAndFocus());
        _trayService.ToggleMiniWindowRequested += () => Dispatcher.BeginInvoke(ToggleMiniWindow);
        _trayService.TestNormalReminderRequested += () => Dispatcher.BeginInvoke(() => TriggerTestReminder(ReminderStrength.Normal));
        _trayService.TestStrongReminderRequested += () => Dispatcher.BeginInvoke(() => TriggerTestReminder(ReminderStrength.Strong));
        _trayService.OpenUpdatesRequested += () => Dispatcher.BeginInvoke(OpenUpdatesDialog);
        _trayService.OpenSettingsRequested += () => Dispatcher.BeginInvoke(OpenSettingsDialog);
        _trayService.ExitRequested += () => Dispatcher.BeginInvoke(ExitApplication);
    }

    private void UpdateTraySummary(BatchQuoteResponse? resp)
    {
        if (_trayService is null || _coordinator is null)
            return;

        if (!_coordinator.Settings.ShowQuoteInTrayTooltip)
        {
            _trayService.UpdateTooltip("MarketWatch 桌面盯盘助手");
            return;
        }

        var first = _coordinator.Settings.Watchlist.FirstOrDefault();
        Quote? quote = null;
        if (first is not null)
        {
            if (resp is null || !resp.Quotes.TryGetValue(first.Id, out quote))
                quote = _coordinator.GetLatestQuote(first.Id);
        }

        if (quote is not null && quote.HasPrice)
        {
            var pct = quote.ChangePercent.HasValue ? $"{(quote.ChangePercent > 0 ? "+" : "")}{quote.ChangePercent:0.00}%" : "--";
            var price = quote.LastPrice!.Value < 10m ? quote.LastPrice.Value.ToString("0.000") : quote.LastPrice.Value.ToString("N2");
            _trayService.UpdateTooltip($"MarketWatch | {quote.Name} {price} ({pct})");
        }
        else
        {
            _trayService.UpdateTooltip("MarketWatch 桌面盯盘助手");
        }
    }

    private void TriggerTestReminder(ReminderStrength strength)
    {
        if (_coordinator is null || _reminderPresenter is null)
            return;
        var ev = _coordinator.CreateTestReminderEvent(strength);
        _reminderPresenter.ShowScheduledReminder(ev);
    }

    private void OpenSearchDialog()
    {
        if (_coordinator is null) return;
        var vm = new SearchDialogViewModel(_coordinator);
        var dlg = new SearchWindow(vm) { Owner = _mainWindow?.IsVisible == true ? _mainWindow : null };
        dlg.ShowDialog();
    }

    private void OpenSettingsDialog()
    {
        if (_services is null || _coordinator is null) return;
        var vm = new SettingsViewModel(
            _coordinator,
            _services.GetRequiredService<ThemeManager>(),
            _services.GetRequiredService<IAutoStartService>());
        vm.TestReminderRequested += TriggerTestReminder;
        vm.OpenUpdatesRequested += OpenUpdatesDialog;

        var dlg = new SettingsWindow(vm) { Owner = _mainWindow?.IsVisible == true ? _mainWindow : null };
        dlg.ShowDialog();
    }

    private void OpenUpdatesDialog()
    {
        if (_services is null) return;
        var updateSvc = _services.GetRequiredService<IAppUpdateService>();
        var tokenStore = _services.GetRequiredService<IGitHubTokenStore>();
        var vm = new UpdateDialogViewModel(updateSvc, tokenStore);
        var dlg = new UpdateWindow(vm) { Owner = _mainWindow?.IsVisible == true ? _mainWindow : null };

        vm.ApplyUpdateAndRestartRequested += stagedPath =>
        {
            try
            {
                updateSvc.LaunchSelfUpdateScript(stagedPath);
                dlg.Close();
                ExitApplication();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"启动更新替换脚本失败：{ex.Message}",
                    "MarketWatch 更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };

        dlg.ShowDialog();
    }

    private void OpenHistoryDialog()
    {
        if (_coordinator is null) return;
        var vm = new HistoryViewModel(_coordinator);
        var dlg = new HistoryWindow(vm) { Owner = _mainWindow?.IsVisible == true ? _mainWindow : null };
        dlg.ShowDialog();
    }

    private void OpenQuickPriceAlertDialog(Instrument instrument)
    {
        if (_coordinator is null) return;
        var quote = _coordinator.GetLatestQuote(instrument.Id);
        var dlg = new PriceAlertDialog(instrument, quote) { Owner = _mainWindow?.IsVisible == true ? _mainWindow : null };
        if (dlg.ShowDialog() == true && dlg.CreatedRule is not null)
        {
            var rule = dlg.CreatedRule;
            _ = _coordinator.UpdateSettingsAsync(s => s.PriceAlerts.Add(rule));
        }
    }

    private void ToggleMiniWindow()
    {
        if (_coordinator is null) return;
        if (_miniWindow is not null && _miniWindow.IsVisible)
        {
            _miniWindow.Hide();
            _ = _coordinator.UpdateSettingsAsync(s => s.MiniWindowVisible = false);
        }
        else
        {
            EnsureMiniWindowShown();
            _ = _coordinator.UpdateSettingsAsync(s => s.MiniWindowVisible = true);
        }
    }

    private void EnsureMiniWindowShown()
    {
        if (_services is null || _coordinator is null) return;
        if (_miniWindow is null)
        {
            var miniVm = _services.GetRequiredService<MiniWindowViewModel>();
            _miniWindow = new MiniFloatingWindow(miniVm, _coordinator);
            _miniWindow.OpenMainRequested += () => _mainWindow?.RestoreAndFocus();
            _miniWindow.HideRequested += () =>
            {
                _miniWindow.Hide();
                _ = _coordinator.UpdateSettingsAsync(s => s.MiniWindowVisible = false);
            };
        }
        _miniWindow.Show();
    }

    private void ExitApplication()
    {
        _logger?.Info("App", "User initiated application exit.");
        _reminderPresenter?.CloseAll();

        if (_miniWindow is not null)
        {
            try { _miniWindow.Close(); } catch { }
        }

        if (_mainWindow is not null)
        {
            _mainWindow.AllowTrueClose = true;
            try { _mainWindow.Close(); } catch { }
        }

        Shutdown(0);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _logger?.Info("Power", "System resumed from sleep; triggering immediate quote refresh.");
            _ = _coordinator?.RefreshQuotesAsync(forceResetBackoff: true);
        }
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        _logger?.Info("Clock", "System time change detected; scheduler will re-evaluate on next tick.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        _coordinator?.Dispose();
        _trayService?.Dispose();
        _singleInstance?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            _logger?.Error("UnhandledUI", "Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _logger?.Error("UnhandledDomain", "Unhandled domain exception", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("UnobservedTask", "Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
