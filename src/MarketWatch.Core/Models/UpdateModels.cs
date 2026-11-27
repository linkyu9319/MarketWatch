namespace MarketWatch.Core.Models;

public enum UpdateCheckState
{
    Idle,
    Current,
    Available,
    Unavailable
}

public enum UpdateInstallState
{
    Idle,
    Downloading,
    Verifying,
    ReadyToRestart,
    Cancelled,
    Failed
}

public sealed record UpdateAsset(
    long Id,
    string Version,
    string Name,
    long Size,
    string Sha256);

public sealed record UpdateStatus
{
    public required UpdateCheckState State { get; init; }
    public required string CurrentVersion { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseUrl { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? ReleaseNotes { get; init; }
    public required string Message { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
    public UpdateAsset? Asset { get; init; }
}

public sealed record UpdateInstallResult
{
    public required UpdateInstallState State { get; init; }
    public string? Version { get; init; }
    public string? StagedFilePath { get; init; }
    public required string Message { get; init; }
}

public sealed record ReleaseApiResponse(
    int StatusCode,
    string Body,
    long? RateLimitRemaining = null,
    long? RateLimitReset = null,
    long? RetryAfterSeconds = null);
