using System.Runtime.Versioning;

namespace MarketWatch.Infrastructure.Updates;

[SupportedOSPlatform("windows")]
public static class UpdateStartupSignal
{
    public static EventWaitHandle? OpenForLaunch(IEnumerable<string> args)
    {
        const string prefix = "--update-attempt=";
        var argument = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.Ordinal));
        if (argument is null || !Guid.TryParseExact(argument[prefix.Length..], "N", out var attempt))
            return null;

        return EventWaitHandle.TryOpenExisting($@"Local\MarketWatch.UpdateReady.{attempt:N}", out var ready)
            ? ready : null;
    }
}
