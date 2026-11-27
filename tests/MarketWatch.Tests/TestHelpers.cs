using MarketWatch.Core.Interfaces;

namespace MarketWatch.Tests;

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
    public TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Utc;

    public FakeClock(DateTimeOffset initialUtc, TimeZoneInfo? localTimeZone = null)
    {
        UtcNow = initialUtc;
        if (localTimeZone is not null)
            LocalTimeZone = localTimeZone;
    }

    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);
}

internal sealed class NullLogger : IAppLogger
{
    public void Info(string category, string message) { }
    public void Warn(string category, string message) { }
    public void Error(string category, string message, Exception? ex = null) { }
}
