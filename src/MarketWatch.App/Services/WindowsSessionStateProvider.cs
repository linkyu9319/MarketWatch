using System.Runtime.InteropServices;
using Microsoft.Win32;
using MarketWatch.Core.Interfaces;

namespace MarketWatch.App.Services;

/// <summary>
/// Tracks Windows desktop lock/unlock state via BOTH:
/// 1. Live Win32 WTSQuerySessionInformation(WTSSessionInfoEx) + OpenInputDesktop probe
///    (detects already-locked sessions at startup, RDP disconnects, and UAC secure desktops).
/// 2. SystemEvents.SessionSwitch for instant unlock notifications.
/// </summary>
public sealed class WindowsSessionStateProvider : ISessionStateProvider, IDisposable
{
    private const int WtsCurrentSession = -1;
    private const int WtsInfoClassSessionInfoEx = 25;
    private const int WtsStateActive = 0;
    private const int WtsSessionStateLock = 0;
    private const uint DesktopSwitchDesktop = 0x0100;

    private readonly IAppLogger _logger;

    // Returning from the UAC desktop does not raise SessionUnlock. Recheck on every scheduler tick.
    public bool IsSessionLocked => QueryCurrentDesktopLocked();
    public event Action? SessionUnlocked;

    public WindowsSessionStateProvider(IAppLogger logger)
    {
        _logger = logger;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        if (IsSessionLocked)
        {
            _logger.Info("Session", "Application started while Windows session is already locked; deferring reminders until unlock.");
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _logger.Info("Session", $"Desktop session locked ({e.Reason}); deferring reminder triggers until unlock.");
                break;

            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _logger.Info("Session", $"Desktop session unlocked ({e.Reason}); resuming reminder evaluation.");
                SessionUnlocked?.Invoke();
                break;
        }
    }

    /// <summary>
    /// Queries Windows directly for current session lock state (verified on x64 Windows 10/11).
    /// </summary>
    private static bool QueryCurrentDesktopLocked()
    {
        // Check 1: WTSQuerySessionInformation(WTSSessionInfoEx)
        // x64 WTSINFOEXW layout: Level (offset 0), padding (4), SessionId (8), SessionState (12), SessionFlags (16)
        if (WTSQuerySessionInformation(IntPtr.Zero, WtsCurrentSession, WtsInfoClassSessionInfoEx, out var buffer, out var bytes)
            && buffer != IntPtr.Zero)
        {
            try
            {
                if (bytes >= 20)
                {
                    var level = Marshal.ReadInt32(buffer, 0);
                    if (level == 1)
                    {
                        var sessionState = Marshal.ReadInt32(buffer, 12);
                        var sessionFlags = Marshal.ReadInt32(buffer, 16);
                        if (sessionState != WtsStateActive || sessionFlags == WtsSessionStateLock)
                        {
                            return true;
                        }
                    }
                }
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        // Check 2: OpenInputDesktop fails (IntPtr.Zero) when Winlogon lock screen or UAC secure desktop is active
        var hDesktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (hDesktop == IntPtr.Zero)
        {
            return true;
        }
        CloseDesktop(hDesktop);
        return false;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int sessionId,
        int wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pointer);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
