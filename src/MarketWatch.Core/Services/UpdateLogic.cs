using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketWatch.Core.Models;

namespace MarketWatch.Core.Services;

/// <summary>
/// Pure, testable GitHub Releases update evaluation, SemVer comparison,
/// strict asset selection, host allowlisting, and SHA-256 + PE binary verification.
/// Security model mirrors VSRead's built-in updater.
/// </summary>
public static partial class UpdateLogic
{
    public const string RepoOwner = "linkyu9319";
    public const string RepoName = "MarketWatch";
    public const string ReleasesUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases";
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    public const string AssetApiPrefix = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/assets/";

    public const int MaxReleaseResponseBytes = 512 * 1024;
    public const long MaxPackageBytes = 200L * 1024 * 1024; // 200 MB cap for self-contained WPF single-file EXE

    public static readonly IReadOnlySet<string> AllowedDownloadHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    private sealed record ParsedSemVer(BigInteger Major, BigInteger Minor, BigInteger Patch, string[] Prerelease);

    public static string ExpectedAssetName(string version) => $"MarketWatch-{version}-win-x64.exe";

    public static bool IsAllowedDownloadUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (!uri.IsDefaultPort && uri.Port != 443)
            return false;
        return AllowedDownloadHosts.Contains(uri.Host);
    }

    /// <summary>
    /// Strict SemVer 2.0 comparison: numeric core comparison, prerelease &lt; release, build metadata ignored.
    /// </summary>
    public static int? CompareSemVer(string? left, string? right)
    {
        var a = ParseSemVer(left);
        var b = ParseSemVer(right);
        if (a is null || b is null)
            return null;

        var c = a.Major.CompareTo(b.Major);
        if (c != 0) return c;
        c = a.Minor.CompareTo(b.Minor);
        if (c != 0) return c;
        c = a.Patch.CompareTo(b.Patch);
        if (c != 0) return c;

        if (a.Prerelease.Length == 0 || b.Prerelease.Length == 0)
        {
            if (a.Prerelease.Length == b.Prerelease.Length) return 0;
            return a.Prerelease.Length > 0 ? -1 : 1;
        }

        var len = Math.Max(a.Prerelease.Length, b.Prerelease.Length);
        for (var i = 0; i < len; i++)
        {
            if (i >= a.Prerelease.Length) return -1;
            if (i >= b.Prerelease.Length) return 1;
            var x = a.Prerelease[i];
            var y = b.Prerelease[i];
            if (x == y) continue;

            var xNum = AllDigitsRegex().IsMatch(x);
            var yNum = AllDigitsRegex().IsMatch(y);
            if (xNum && yNum)
                return BigInteger.Parse(x, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(y, CultureInfo.InvariantCulture));
            if (xNum != yNum)
                return xNum ? -1 : 1;
            return string.CompareOrdinal(x, y) > 0 ? 1 : -1;
        }

        return 0;
    }

    public static bool IsStableReleaseVersion(string? raw, out string normalizedVersion)
    {
        normalizedVersion = string.Empty;
        var parsed = ParseSemVer(raw);
        if (parsed is null || parsed.Prerelease.Length > 0)
            return false;
        normalizedVersion = $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}";
        return true;
    }

    public static UpdateStatus EvaluateReleaseResponse(
        string currentVersion,
        ReleaseApiResponse response,
        bool authenticated,
        DateTimeOffset utcNow)
    {
        if (response.StatusCode == 404)
        {
            return Unavailable(currentVersion, authenticated
                ? "未找到可访问的正式发布版本，请确认令牌有仓库读取权限，或稍后再试。"
                : "尚无正式发布版本，或仓库为私有。可配置 GitHub 只读令牌后重试。", "not-found");
        }

        if (response.StatusCode == 401)
        {
            return Unavailable(currentVersion, "GitHub 令牌无效或已过期，请重新配置。", "unauthorized");
        }

        if (IsRateLimited(response))
        {
            return BuildRateLimitedStatus(currentVersion, response, authenticated, utcNow);
        }

        if (response.StatusCode == 403)
        {
            return Unavailable(currentVersion, authenticated
                ? "GitHub 拒绝访问（HTTP 403），未返回限流信息。请检查令牌的仓库读取权限，或清除令牌后重试。"
                : "GitHub 拒绝访问（HTTP 403），未返回限流信息。请检查网络访问限制或配置只读令牌。", "forbidden");
        }

        if (response.StatusCode != 200)
        {
            return Unavailable(currentVersion, $"无法检查更新（HTTP {response.StatusCode}），请稍后重试。", "http-error");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(response.Body);
        }
        catch
        {
            return Unavailable(currentVersion, "GitHub 返回的发布信息格式无效。", "invalid-json");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Unavailable(currentVersion, "GitHub 返回的发布信息格式无效。", "invalid-json");

            var isDraft = root.TryGetProperty("draft", out var dEl) && dEl.ValueKind == JsonValueKind.True;
            var isPre = root.TryGetProperty("prerelease", out var pEl) && pEl.ValueKind == JsonValueKind.True;
            var tag = root.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String
                ? tagEl.GetString()?.Trim() ?? ""
                : "";

            if (isDraft || isPre || !IsStableReleaseVersion(tag, out var latestVersion))
                return Unavailable(currentVersion, "暂无有效的正式发布版本。", "no-stable-release");

            var cmp = CompareSemVer(tag, currentVersion);
            if (!cmp.HasValue)
                return Unavailable(currentVersion, "当前应用版本格式无效，无法比较版本。", "invalid-current-version");

            // Always construct releaseUrl from fixed repo URL + validated tag (never trust arbitrary html_url)
            var releaseUrl = $"{ReleasesUrl}/tag/{Uri.EscapeDataString(tag)}";

            DateTimeOffset? publishedAt = null;
            if (root.TryGetProperty("published_at", out var pubEl) &&
                pubEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(pubEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedPub))
            {
                publishedAt = parsedPub;
            }

            string? releaseNotes = null;
            if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String)
            {
                var text = bodyEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    releaseNotes = text.Length > 2000 ? text[..2000] + "…" : text;
            }

            TrySelectUpdateAsset(latestVersion, root, out var asset, out _);

            return new UpdateStatus
            {
                State = cmp.Value > 0 ? UpdateCheckState.Available : UpdateCheckState.Current,
                CurrentVersion = currentVersion.TrimStart('v', 'V'),
                LatestVersion = latestVersion,
                ReleaseUrl = releaseUrl,
                PublishedAt = publishedAt,
                ReleaseNotes = releaseNotes,
                Asset = asset,
                Message = cmp.Value > 0
                    ? $"发现新版本 v{latestVersion}"
                    : $"当前版本 (v{currentVersion.TrimStart('v', 'V')}) 已是最新正式版本。"
            };
        }
    }

    public static bool TrySelectUpdateAsset(
        string version,
        JsonElement releaseRoot,
        out UpdateAsset? asset,
        out string error)
    {
        asset = null;
        error = string.Empty;

        if (!StableCoreVersionRegex().IsMatch(version))
        {
            error = "发布版本号格式无效，请重新检查更新。";
            return false;
        }

        var tag = releaseRoot.TryGetProperty("tag_name", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString()?.Trim() ?? ""
            : "";
        var isDraft = releaseRoot.TryGetProperty("draft", out var dEl) && dEl.ValueKind == JsonValueKind.True;
        var isPre = releaseRoot.TryGetProperty("prerelease", out var pEl) && pEl.ValueKind == JsonValueKind.True;

        if (isDraft || isPre || (tag != version && tag != $"v{version}"))
        {
            error = "发布信息无效，请重新检查更新。";
            return false;
        }

        if (!releaseRoot.TryGetProperty("assets", out var assetsEl) || assetsEl.ValueKind != JsonValueKind.Array)
        {
            error = "该发布版本未包含可下载安装包，请前往发布页手动下载。";
            return false;
        }

        var expectedName = ExpectedAssetName(version);
        foreach (var item in assetsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : null;
            if (!string.Equals(name, expectedName, StringComparison.Ordinal))
                continue;

            var state = item.TryGetProperty("state", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
            long id = 0;
            var idOk = item.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out id) && id > 0;
            long size = 0;
            var sizeOk = item.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out size) && size > 0 && size <= MaxPackageBytes;
            var digest = item.TryGetProperty("digest", out var dgEl) && dgEl.ValueKind == JsonValueKind.String ? dgEl.GetString() : null;

            if (state != "uploaded" || !idOk || !sizeOk || string.IsNullOrWhiteSpace(digest) || !Sha256DigestRegex().IsMatch(digest))
            {
                error = "发布版本缺少可校验 SHA-256 的安装包，请前往发布页手动下载。";
                return false;
            }

            asset = new UpdateAsset(id, version, expectedName, size, digest[7..].ToLowerInvariant());
            return true;
        }

        error = $"发布版本中未找到 {expectedName} 安装包，请前往发布页手动下载。";
        return false;
    }

    public static void VerifyUpdateBinary(byte[] bytes, UpdateAsset asset)
    {
        if (!StableCoreVersionRegex().IsMatch(asset.Version) || asset.Name != ExpectedAssetName(asset.Version))
            throw new InvalidDataException("更新安装包名称无效。");

        if (bytes.LongLength != asset.Size || bytes.LongLength > MaxPackageBytes)
            throw new InvalidDataException("安装包大小不匹配，请重试下载。");

        // Must be a valid Windows PE executable ("MZ" header)
        if (bytes.Length < 64 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException("下载的文件不是有效的 Windows 可执行程序，已中止更新。");

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualHash),
                System.Text.Encoding.ASCII.GetBytes(asset.Sha256.ToLowerInvariant())))
        {
            throw new InvalidDataException("安装包 SHA-256 完整性校验失败，请重试下载。");
        }
    }

    private static bool IsRateLimited(ReleaseApiResponse response)
    {
        if (response.StatusCode is not (403 or 429))
            return false;
        if (response.RateLimitRemaining == 0)
            return true;

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            {
                var msg = msgEl.GetString() ?? "";
                if (msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase) ||
                    msg.StartsWith("API rate limit exceeded", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch { }

        return response.StatusCode == 429;
    }

    private static UpdateStatus BuildRateLimitedStatus(
        string currentVersion,
        ReleaseApiResponse response,
        bool authenticated,
        DateTimeOffset utcNow)
    {
        DateTimeOffset? retryAfterAt = response.RetryAfterSeconds is > 0 and <= 7 * 24 * 3600
            ? utcNow.AddSeconds(response.RetryAfterSeconds.Value)
            : null;
        DateTimeOffset? resetAt = response.RateLimitReset is > 0 &&
                                  (response.RateLimitRemaining == 0 || !retryAfterAt.HasValue)
            ? DateTimeOffset.FromUnixTimeSeconds(response.RateLimitReset.Value)
            : null;

        DateTimeOffset? retryAt = retryAfterAt.HasValue && resetAt.HasValue
            ? (retryAfterAt.Value > resetAt.Value ? retryAfterAt : resetAt)
            : retryAfterAt ?? resetAt;

        if (retryAt.HasValue && retryAt.Value <= utcNow)
            retryAt = null;

        var waitMsg = retryAt.HasValue
            ? $"请约 {Math.Ceiling((retryAt.Value - utcNow).TotalSeconds)} 秒后重试。"
            : "请等待额度恢复后重试。";
        var authMsg = authenticated
            ? "可检查所配置令牌的请求额度。"
            : "也可配置 GitHub 只读访问令牌，使用认证请求额度。";

        return new UpdateStatus
        {
            State = UpdateCheckState.Unavailable,
            CurrentVersion = currentVersion.TrimStart('v', 'V'),
            Message = $"GitHub API 请求已被限流。{waitMsg}{authMsg}",
            ErrorCode = "rate-limited",
            RetryAt = retryAt
        };
    }

    private static UpdateStatus Unavailable(string currentVersion, string message, string? errorCode = null) => new()
    {
        State = UpdateCheckState.Unavailable,
        CurrentVersion = currentVersion.TrimStart('v', 'V'),
        Message = message,
        ErrorCode = errorCode
    };

    private static ParsedSemVer? ParseSemVer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 128)
            return null;

        var m = SemVerRegex().Match(raw.Trim());
        if (!m.Success)
            return null;

        var prerelease = m.Groups[4].Success
            ? m.Groups[4].Value.Split('.')
            : Array.Empty<string>();

        foreach (var part in prerelease)
        {
            if (AllDigitsRegex().IsMatch(part) && part.Length > 1 && part.StartsWith('0'))
                return null;
        }

        return new ParsedSemVer(
            BigInteger.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            BigInteger.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            BigInteger.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            prerelease);
    }

    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex AllDigitsRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex StableCoreVersionRegex();

    [GeneratedRegex(@"^sha256:[a-f0-9]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256DigestRegex();
}
