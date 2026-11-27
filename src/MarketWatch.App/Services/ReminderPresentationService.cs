using MarketWatch.App.ViewModels;
using MarketWatch.App.Views;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;

namespace MarketWatch.App.Services;

/// <summary>
/// Presents independent silent Topmost visual alert popups on the UI dispatcher,
/// deduplicating active popups by trigger key and honoring animation/overlay/sound preferences.
/// </summary>
public sealed class ReminderPresentationService
{
    private readonly IClock _clock;
    private readonly AppStateCoordinator _coordinator;
    private readonly ThemeManager _themeManager;
    private readonly Dictionary<string, ReminderPopupWindow> _activeWindows = new(StringComparer.Ordinal);

    public ReminderPresentationService(
        IClock clock,
        AppStateCoordinator coordinator,
        ThemeManager themeManager)
    {
        _clock = clock;
        _coordinator = coordinator;
        _themeManager = themeManager;
    }

    public void ShowScheduledReminder(ScheduledReminderEvent ev)
    {
        RunOnUi(() =>
        {
            if (_activeWindows.TryGetValue(ev.TriggerKey, out var existing))
            {
                existing.Topmost = true;
                return;
            }

            var vm = new ReminderPopupViewModel(ev, _clock, _coordinator, _themeManager);
            var settings = _coordinator.Settings;
            var useOverlay = ev.Rule.Strength == ReminderStrength.Strong &&
                             (settings.EnableFullScreenOverlayForStrong || ev.Rule.AllowFullScreenOverlay);
            var useSound = settings.EnableReminderSound || ev.Rule.AllowSound;

            var win = new ReminderPopupWindow(vm, useOverlay, useSound);
            _activeWindows[ev.TriggerKey] = win;
            win.Closed += (_, _) => _activeWindows.Remove(ev.TriggerKey);
            win.Show();
        });
    }

    public void ShowPriceAlert(PriceAlertRecord alert)
    {
        RunOnUi(() =>
        {
            var key = $"PRICE|{alert.RuleId}|{alert.TriggeredAt.ToUnixTimeSeconds()}";
            var vm = new ReminderPopupViewModel(alert, _clock, _coordinator, _themeManager);
            var win = new ReminderPopupWindow(vm, useFullScreenOverlay: false, playSound: _coordinator.Settings.EnableReminderSound);
            _activeWindows[key] = win;
            win.Closed += (_, _) => _activeWindows.Remove(key);
            win.Show();
        });
    }

    public void CloseAll()
    {
        RunOnUi(() =>
        {
            foreach (var win in _activeWindows.Values.ToList())
            {
                try { win.Close(); } catch { }
            }
            _activeWindows.Clear();
        });
    }

    private static void RunOnUi(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
