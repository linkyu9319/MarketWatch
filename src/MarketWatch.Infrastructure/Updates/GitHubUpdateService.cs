using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.Infrastructure.Updates;

/// <summary>
/// GitHub Releases built-in updater matching VSRead's security design:
/// - Fixed GitHub API host with manual redirect verification against an HTTPS allowlist
/// - Never forwards Bearer tokens to CDN redirect hosts
/// - Strict asset naming + exact size + Windows MZ PE header + SHA-256 verification
/// - Atomic staged download + clean self-replace helper script for single-file Windows EXE
/// </summary>
public sealed class GitHubUpdateService : IAppUpdateService, IDisposable
{
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IClock _clock;
    private readonly IAppLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _updatesDirectory;

    private readonly object _sync = new();
    private Task<UpdateStatus>? _pendingCheck;
    private Task<UpdateInstallResult>? _pendingInstall;
    private UpdateInstallResult? _stagedResult;

    public string CurrentVersion { get; }
    public string ReleasesPageUrl => UpdateLogic.ReleasesUrl;

    public GitHubUpdateService(
        IGitHubTokenStore tokenStore,
        IClock clock,
        IAppLogger logger,
        string? currentVersionOverride = null,
        string? updatesDirectory = null)
    {
        _tokenStore = tokenStore;
        _clock = clock;
        _logger = logger;
        CurrentVersion = currentVersionOverride ?? ResolveCurrentVersion();
        _updatesDirectory = updatesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketWatch",
            "updates");

        // Disable automatic redirects so we strictly validate every hop and never leak Bearer tokens
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(4) };
    }

    private static string ResolveCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(GitHubUpdateService).Assembly;
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var plusIdx = infoVer.IndexOf('+');
            var clean = plusIdx >= 0 ? infoVer[..plusIdx] : infoVer;
            if (UpdateLogic.IsStableReleaseVersion(clean, out var norm))
                return norm;
        }
        var v = asm.GetName().Version;
        return v is not null ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}" : "1.0.0";
    }

    public Task<UpdateStatus> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_pendingCheck is not null)
                return _pendingCheck;

            var task = CheckOnceAsync(ct);
            _pendingCheck = task;
            _ = task.ContinueWith(_ => { lock (_sync) { if (_pendingCheck == task) _pendingCheck = null; } }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<UpdateStatus> CheckOnceAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var authenticated = false;
        try
        {
            var token = await _tokenStore.GetTokenAsync(ct).ConfigureAwait(false);
            authenticated = !string.IsNullOrWhiteSpace(token);
            var response = await FetchLatestReleaseAsync(token, ct).ConfigureAwait(false);
            var status = UpdateLogic.EvaluateReleaseResponse(CurrentVersion, response, authenticated, _clock.UtcNow);
            _logger.Info("Updates", $"Check completed: state={status.State}, current={CurrentVersion}, latest={status.LatestVersion ?? "-"}, http={response.StatusCode}, auth={authenticated}, ms={sw.ElapsedMilliseconds}");
            return status;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn("Updates", $"Check failed ({ex.GetType().Name}, auth={authenticated}, ms={sw.ElapsedMilliseconds}).");
            return new UpdateStatus
            {
                State = UpdateCheckState.Unavailable,
                CurrentVersion = CurrentVersion,
                Message = "无法连接 GitHub，请检查网络连接（或代理设置）后重试。",
                ErrorCode = "network-error"
            };
        }
    }

    public Task<UpdateInstallResult> DownloadAndStageUpdateAsync(
        IProgress<(long Received, long Total, string Stage)>? progress = null,
        CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_stagedResult is not null && File.Exists(_stagedResult.StagedFilePath))
                return Task.FromResult(_stagedResult);
            if (_pendingInstall is not null)
                return _pendingInstall;

            var task = DownloadAndStageCoreAsync(progress, ct);
            _pendingInstall = task;
            _ = task.ContinueWith(t =>
            {
                lock (_sync)
                {
                    if (t.IsCompletedSuccessfully && t.Result.State == UpdateInstallState.ReadyToRestart)
                        _stagedResult = t.Result;
                    if (_pendingInstall == task)
                        _pendingInstall = null;
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<UpdateInstallResult> DownloadAndStageCoreAsync(
        IProgress<(long Received, long Total, string Stage)>? progress,
        CancellationToken ct)
    {
        try
        {
            progress?.Report((0, 100, "正在获取最新正式版本信息..."));
            var token = await _tokenStore.GetTokenAsync(ct).ConfigureAwait(false);
            var authenticated = !string.IsNullOrWhiteSpace(token);

            ReleaseApiResponse releaseResp;
            try
            {
                releaseResp = await FetchLatestReleaseAsync(token, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new UpdateInstallResult { State = UpdateInstallState.Cancelled, Message = "已取消更新，当前版本保持不变。" };
            }
            catch
            {
                return new UpdateInstallResult { State = UpdateInstallState.Failed, Message = "无法连接 GitHub，请稍后重试或从发布页手动下载。" };
            }

            var status = UpdateLogic.EvaluateReleaseResponse(CurrentVersion, releaseResp, authenticated, _clock.UtcNow);
            if (status.State != UpdateCheckState.Available || string.IsNullOrWhiteSpace(status.LatestVersion))
            {
                return new UpdateInstallResult { State = UpdateInstallState.Failed, Message = status.Message };
            }

            UpdateAsset? asset;
            string assetError;
            using (var doc = JsonDocument.Parse(releaseResp.Body))
            {
                if (!UpdateLogic.TrySelectUpdateAsset(status.LatestVersion, doc.RootElement, out asset, out assetError) || asset is null)
                {
                    return new UpdateInstallResult { State = UpdateInstallState.Failed, Message = assetError };
                }
            }

            progress?.Report((0, asset.Size, $"正在下载 {asset.Name}..."));
            var bytes = await DownloadAssetWithVerifiedRedirectsAsync(asset, token, progress, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            progress?.Report((asset.Size, asset.Size, "正在校验安装包完整性 (SHA-256)..."));
            UpdateLogic.VerifyUpdateBinary(bytes, asset);

            Directory.CreateDirectory(_updatesDirectory);
            var stagedPath = Path.Combine(_updatesDirectory, $"{asset.Name}.staged");
            var tempPath = $"{stagedPath}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
            File.Move(tempPath, stagedPath, overwrite: true);

            _logger.Info("Updates", $"Staged verified update v{asset.Version} ({asset.Size} bytes, sha256={asset.Sha256[..12]}...).");

            return new UpdateInstallResult
            {
                State = UpdateInstallState.ReadyToRestart,
                Version = asset.Version,
                StagedFilePath = stagedPath,
                Message = $"版本 v{asset.Version} 已下载并完成 SHA-256 校验，点击“立即重启更新”即可替换生效。"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new UpdateInstallResult { State = UpdateInstallState.Cancelled, Message = "已取消更新，当前版本保持不变。" };
        }
        catch (InvalidDataException ex)
        {
            _logger.Warn("Updates", $"Update verification failed: {ex.Message}");
            return new UpdateInstallResult { State = UpdateInstallState.Failed, Message = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.Warn("Updates", $"Update download failed ({ex.GetType().Name}).");
            return new UpdateInstallResult { State = UpdateInstallState.Failed, Message = "下载更新失败，请检查网络连接或前往发布页手动下载。" };
        }
    }

    public bool CanApplySelfUpdate(out string reason)
    {
        var procPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(procPath) || !File.Exists(procPath))
        {
            reason = "无法定位当前运行程序路径，请前往发布页手动替换。";
            return false;
        }

        var fileName = Path.GetFileName(procPath);
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("testhost.exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = "当前为源码开发模式（通过 dotnet 宿主运行），请前往发布页下载或直接运行发布版 MarketWatch.exe 后使用一键自更新。";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public void LaunchSelfUpdateScript(string stagedExePath)
    {
        if (!File.Exists(stagedExePath))
            throw new FileNotFoundException("找不到已校验的更新暂存文件。", stagedExePath);

        if (!CanApplySelfUpdate(out var reason))
            throw new InvalidOperationException(reason);

        var targetExePath = Environment.ProcessPath!;
        var currentPid = Environment.ProcessId;

        Directory.CreateDirectory(_updatesDirectory);
        var scriptPath = Path.Combine(_updatesDirectory, "apply-update.ps1");

        using (var script = typeof(GitHubUpdateService).Assembly.GetManifestResourceStream("MarketWatch.Updates.ApplyUpdate.ps1")
               ?? throw new InvalidOperationException("更新助手未包含在应用程序中。"))
        using (var file = File.Create(scriptPath))
        {
            script.CopyTo(file);
        }

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-TargetPid");
        psi.ArgumentList.Add(currentPid.ToString());
        psi.ArgumentList.Add("-TargetExe");
        psi.ArgumentList.Add(targetExePath);
        psi.ArgumentList.Add("-StagedExe");
        psi.ArgumentList.Add(stagedExePath);

        using var helper = Process.Start(psi) ?? throw new InvalidOperationException("未能启动更新助手。");
        _logger.Info("Updates", $"Launched self-update helper for PID={currentPid} -> {targetExePath}");
    }

    private async Task<ReleaseApiResponse> FetchLatestReleaseAsync(string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateLogic.LatestReleaseApiUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("MarketWatch-version-check");
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

        long? remaining = TryParseHeaderLong(resp.Headers, "X-RateLimit-Remaining");
        long? reset = TryParseHeaderLong(resp.Headers, "X-RateLimit-Reset");
        long? retryAfter = TryParseHeaderLong(resp.Headers, "Retry-After");

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > UpdateLogic.MaxReleaseResponseBytes)
                throw new InvalidDataException("Release response exceeded safe size limit.");
            ms.Write(buffer, 0, read);
        }

        var body = Encoding.UTF8.GetString(ms.ToArray());
        return new ReleaseApiResponse((int)resp.StatusCode, body, remaining, reset, retryAfter);
    }

    private async Task<byte[]> DownloadAssetWithVerifiedRedirectsAsync(
        UpdateAsset asset,
        string? token,
        IProgress<(long Received, long Total, string Stage)>? progress,
        CancellationToken ct)
    {
        var initialUrl = new Uri(UpdateLogic.AssetApiPrefix + asset.Id);
        var currentUri = initialUrl;

        for (var redirects = 0; redirects <= 3; redirects++)
        {
            ct.ThrowIfCancellationRequested();
            if (!UpdateLogic.IsAllowedDownloadUri(currentUri))
                throw new InvalidDataException("更新下载跳转地址不在允许的 GitHub 域名白名单内。");

            var isInitialAssetApi = string.Equals(currentUri.AbsoluteUri, initialUrl.AbsoluteUri, StringComparison.Ordinal);

            using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            req.Headers.UserAgent.ParseAdd("MarketWatch-update");
            if (isInitialAssetApi && !string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var loc = resp.Headers.Location
                    ?? throw new InvalidDataException("更新下载缺少跳转地址。");
                currentUri = loc.IsAbsoluteUri ? loc : new Uri(currentUri, loc);
                continue;
            }

            if (resp.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"下载更新失败（HTTP {(int)resp.StatusCode}）。");

            if (resp.Content.Headers.ContentLength.HasValue && resp.Content.Headers.ContentLength.Value != asset.Size)
                throw new InvalidDataException("安装包大小不匹配，请重试下载。");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream(capacity: (int)Math.Min(asset.Size, int.MaxValue));
            var buffer = new byte[65536];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > asset.Size || received > UpdateLogic.MaxPackageBytes)
                    throw new InvalidDataException("安装包超过预期大小，已中止下载。");
                ms.Write(buffer, 0, read);
                var pct = asset.Size > 0 ? received * 100.0 / asset.Size : 0;
                progress?.Report((received, asset.Size, $"下载中 {pct:0}% ({received / 1048576.0:0.1} / {asset.Size / 1048576.0:0.1} MB)"));
            }

            return ms.ToArray();
        }

        throw new InvalidDataException("更新下载跳转次数过多。");
    }

    private static long? TryParseHeaderLong(HttpResponseHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values))
        {
            var first = values.FirstOrDefault();
            if (long.TryParse(first, out var v) && v >= 0)
                return v;
        }
        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
