using MarketWatch.Core.Models;

namespace MarketWatch.Core.Interfaces;

public interface IGitHubTokenStore
{
    Task<string?> GetTokenAsync(CancellationToken ct = default);
    Task SaveTokenAsync(string token, CancellationToken ct = default);
    Task ClearTokenAsync(CancellationToken ct = default);
    Task<bool> HasTokenAsync(CancellationToken ct = default);
}

public interface IAppUpdateService
{
    string CurrentVersion { get; }
    string ReleasesPageUrl { get; }

    Task<UpdateStatus> CheckForUpdatesAsync(CancellationToken ct = default);

    Task<UpdateInstallResult> DownloadAndStageUpdateAsync(
        IProgress<(long Received, long Total, string Stage)>? progress = null,
        CancellationToken ct = default);

    bool CanApplySelfUpdate(out string reason);

    void LaunchSelfUpdateScript(string stagedExePath);
}
