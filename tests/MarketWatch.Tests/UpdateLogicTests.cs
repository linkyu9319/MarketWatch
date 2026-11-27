using System.Security.Cryptography;
using System.Text.Json;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;
using Xunit;

namespace MarketWatch.Tests;

public sealed class UpdateLogicTests
{
    [Fact]
    public void CompareSemVer_HandlesNumericPrereleaseAndBuildMetadata()
    {
        Assert.Equal(1, UpdateLogic.CompareSemVer("v1.0.1", "1.0.0"));
        Assert.Equal(1, UpdateLogic.CompareSemVer("1.10.0", "1.2.0"));
        Assert.Equal(0, UpdateLogic.CompareSemVer("v1.0.0+build123", "1.0.0"));
        Assert.Equal(-1, UpdateLogic.CompareSemVer("1.0.0-rc.1", "1.0.0"));
        Assert.Equal(1, UpdateLogic.CompareSemVer("1.0.0-beta.10", "1.0.0-beta.2"));
        Assert.Null(UpdateLogic.CompareSemVer("invalid", "1.0.0"));
    }

    [Fact]
    public void EvaluateReleaseResponse_DetectsAvailableCurrentRateLimitAndRejectsDraftOrPrerelease()
    {
        var now = new DateTimeOffset(2026, 11, 27, 2, 0, 0, TimeSpan.Zero);
        var validSha = "sha256:" + new string('a', 64);

        var bodyNew = $$"""
        {
          "tag_name": "v1.1.0",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-11-27T01:00:00Z",
          "html_url": "https://evil.example/hijack",
          "body": "Bug fixes and updater",
          "assets": [
            {
              "id": 9988,
              "name": "MarketWatch-1.1.0-win-x64.exe",
              "state": "uploaded",
              "size": 1024,
              "digest": "{{validSha}}"
            }
          ]
        }
        """;

        var available = UpdateLogic.EvaluateReleaseResponse("1.0.0", new ReleaseApiResponse(200, bodyNew), false, now);
        Assert.Equal(UpdateCheckState.Available, available.State);
        Assert.Equal("1.1.0", available.LatestVersion);
        Assert.Equal($"{UpdateLogic.ReleasesUrl}/tag/v1.1.0", available.ReleaseUrl); // Ignores malicious html_url
        Assert.NotNull(available.Asset);
        Assert.Equal(9988, available.Asset!.Id);

        var current = UpdateLogic.EvaluateReleaseResponse("1.1.0", new ReleaseApiResponse(200, bodyNew), false, now);
        Assert.Equal(UpdateCheckState.Current, current.State);

        // Prerelease must be rejected
        var bodyPre = """{"tag_name":"v1.2.0-beta.1","draft":false,"prerelease":true,"assets":[]}""";
        var preStatus = UpdateLogic.EvaluateReleaseResponse("1.0.0", new ReleaseApiResponse(200, bodyPre), false, now);
        Assert.Equal(UpdateCheckState.Unavailable, preStatus.State);

        // Rate limit 403 with remaining=0
        var rateLimited = UpdateLogic.EvaluateReleaseResponse(
            "1.0.0",
            new ReleaseApiResponse(403, """{"message":"API rate limit exceeded"}""", RateLimitRemaining: 0, RateLimitReset: now.AddSeconds(120).ToUnixTimeSeconds()),
            authenticated: false,
            now);
        Assert.Equal(UpdateCheckState.Unavailable, rateLimited.State);
        Assert.Equal("rate-limited", rateLimited.ErrorCode);
        Assert.NotNull(rateLimited.RetryAt);

        // 404 & 401
        Assert.Equal("not-found", UpdateLogic.EvaluateReleaseResponse("1.0.0", new ReleaseApiResponse(404, "{}"), false, now).ErrorCode);
        Assert.Equal("unauthorized", UpdateLogic.EvaluateReleaseResponse("1.0.0", new ReleaseApiResponse(401, "{}"), true, now).ErrorCode);
    }

    [Fact]
    public void VerifyUpdateBinary_ValidatesSizePeHeaderAndSha256()
    {
        var validPe = new byte[128];
        validPe[0] = (byte)'M';
        validPe[1] = (byte)'Z';
        var hash = Convert.ToHexString(SHA256.HashData(validPe)).ToLowerInvariant();
        var asset = new UpdateAsset(1, "1.1.0", "MarketWatch-1.1.0-win-x64.exe", validPe.Length, hash);

        // Valid passes
        UpdateLogic.VerifyUpdateBinary(validPe, asset);

        // Tampered byte fails SHA-256
        var tampered = (byte[])validPe.Clone();
        tampered[50] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => UpdateLogic.VerifyUpdateBinary(tampered, asset));

        // Non-PE payload fails even if hash matches
        var nonPe = new byte[128];
        var nonPeHash = Convert.ToHexString(SHA256.HashData(nonPe)).ToLowerInvariant();
        var nonPeAsset = new UpdateAsset(2, "1.1.0", "MarketWatch-1.1.0-win-x64.exe", nonPe.Length, nonPeHash);
        Assert.Throws<InvalidDataException>(() => UpdateLogic.VerifyUpdateBinary(nonPe, nonPeAsset));
    }

    [Fact]
    public void IsAllowedDownloadUri_EnforcesHttpsAndGitHubAllowlist()
    {
        Assert.True(UpdateLogic.IsAllowedDownloadUri(new Uri("https://api.github.com/repos/linkyu9319/MarketWatch/releases/assets/1")));
        Assert.True(UpdateLogic.IsAllowedDownloadUri(new Uri("https://objects.githubusercontent.com/github-production-release-asset-123/file")));
        Assert.False(UpdateLogic.IsAllowedDownloadUri(new Uri("http://api.github.com/repos/linkyu9319/MarketWatch/releases/assets/1")));
        Assert.False(UpdateLogic.IsAllowedDownloadUri(new Uri("https://evil.example.com/file.exe")));
        Assert.False(UpdateLogic.IsAllowedDownloadUri(new Uri("https://user:pass@api.github.com/file")));
    }
}
