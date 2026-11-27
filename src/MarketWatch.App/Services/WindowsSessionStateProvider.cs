using Microsoft.Win32;
using MarketWatch.Core.Interfaces;

namespace MarketWatch.App.Services;

/// <summary>
/// Tracks Windows desktop lock/unlock state via SystemEvents.SessionSwitch
/// so reminders are deferred during lock screen and caught up upon unlock.
/// </summary>
public sealed class WindowsSessionStateProvider : ISessionStateProvider, IDisposable
{
    private volatile bool _isSessionLocked;
    private readonly IAppLogger _logger;

    public bool IsSessionLocked => _isSessionLocked;
    public event Action? SessionUnlocked;

    public WindowsSessionStateProvider(IAppLogger logger)
    {
        _logger = logger;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _isSessionLocked = true;
                _logger.Info("Session", $"Desktop session locked ({e.Reason}); deferring reminder triggers until unlock.");
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _isSessionLocked = false;
                _logger.Info("Session", $"Desktop session unlocked ({e.Reason}); resuming reminder evaluation.");
                SessionUnlocked?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
