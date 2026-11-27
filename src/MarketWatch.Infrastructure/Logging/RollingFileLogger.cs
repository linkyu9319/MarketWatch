using System.Text;
using MarketWatch.Core.Interfaces;

namespace MarketWatch.Infrastructure.Logging;

/// <summary>
/// Thread-safe daily rolling file logger with size cap and automatic retention cleanup.
/// Never logs secrets or collects telemetry.
/// </summary>
public sealed class RollingFileLogger : IAppLogger
{
    private readonly string _logDir;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly object _sync = new();
    private DateOnly _lastCleanupDate;

    public RollingFileLogger(string? logDirectory = null, long maxFileBytes = 2 * 1024 * 1024, int retentionDays = 7)
    {
        _logDir = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketWatch",
            "logs");
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
    }

    public string LogDirectory => _logDir;

    public void Info(string category, string message) => Write("INFO", category, message, null);
    public void Warn(string category, string message) => Write("WARN", category, message, null);
    public void Error(string category, string message, Exception? ex = null) => Write("ERROR", category, message, ex);

    private void Write(string level, string category, string message, Exception? ex)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logDir);
                var now = DateTimeOffset.Now;
                var today = DateOnly.FromDateTime(now.DateTime);
                var filePath = Path.Combine(_logDir, $"marketwatch-{today:yyyyMMdd}.log");

                if (File.Exists(filePath) && new FileInfo(filePath).Length >= _maxFileBytes)
                {
                    var rotated = Path.Combine(_logDir, $"marketwatch-{today:yyyyMMdd}-{now:HHmmss}.log");
                    File.Move(filePath, rotated, overwrite: true);
                }

                var sb = new StringBuilder();
                sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                  .Append(" [").Append(level).Append("] [").Append(category).Append("] ")
                  .AppendLine(message);
                if (ex is not null)
                {
                    sb.AppendLine(ex.ToString());
                }

                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);

                if (_lastCleanupDate != today)
                {
                    _lastCleanupDate = today;
                    CleanupOldLogs(today);
                }
            }
        }
        catch
        {
            // Logging must never crash the application
        }
    }

    private void CleanupOldLogs(DateOnly today)
    {
        try
        {
            var cutoff = today.AddDays(-_retentionDays).ToDateTime(TimeOnly.MinValue);
            foreach (var file in Directory.EnumerateFiles(_logDir, "marketwatch-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
