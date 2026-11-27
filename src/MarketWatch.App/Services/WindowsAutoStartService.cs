using Microsoft.Win32;
using MarketWatch.Core.Interfaces;

namespace MarketWatch.App.Services;

public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "MarketWatchDesktop";
    private readonly IAppLogger _logger;

    public WindowsAutoStartService(IAppLogger logger)
    {
        _logger = logger;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch (Exception ex)
        {
            _logger.Warn("AutoStart", $"Failed reading auto-start registry key: {ex.Message}");
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
                return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(AppValueName, $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(AppValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("AutoStart", $"Failed updating auto-start registry key (enabled={enabled})", ex);
            throw;
        }
    }
}
