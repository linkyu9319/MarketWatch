using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.App.ViewModels;

public sealed partial class UpdateDialogViewModel : ObservableObject
{
    private readonly IAppUpdateService _updateService;
    private readonly IGitHubTokenStore _tokenStore;
    private CancellationTokenSource? _downloadCts;

    [ObservableProperty] private string _currentVersionText;
    [ObservableProperty] private string _statusHeadline = "点击“检查更新”从 GitHub 获取最新正式版本。";
    [ObservableProperty] private string _versionMetaText = "";
    [ObservableProperty] private string? _releaseNotesText;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _downloadProgressPercent;
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isReadyToRestart;
    [ObservableProperty] private bool _hasSavedToken;
    [ObservableProperty] private string _tokenStatusText = "当前未配置访问令牌（公开仓库通常无需令牌）";

    private string? _stagedExePath;
    private string _latestReleaseUrl;

    public event Action<string>? ApplyUpdateAndRestartRequested;
    public event Action? CloseRequested;

    public UpdateDialogViewModel(IAppUpdateService updateService, IGitHubTokenStore tokenStore)
    {
        _updateService = updateService;
        _tokenStore = tokenStore;
        _currentVersionText = $"v{updateService.CurrentVersion}";
        _latestReleaseUrl = updateService.ReleasesPageUrl;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await RefreshTokenStatusAsync();
        await CheckForUpdatesAsync();
    }

    private async Task RefreshTokenStatusAsync()
    {
        HasSavedToken = await _tokenStore.HasTokenAsync();
        TokenStatusText = HasSavedToken
            ? "已配置 GitHub 访问令牌（Windows DPAPI 本机用户加密保存）"
            : "当前未配置访问令牌（公开仓库无需令牌；访问私有仓库或提升额度时可配置）";
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        if (IsChecking || IsDownloading || IsReadyToRestart)
            return;

        IsChecking = true;
        ErrorMessage = null;
        StatusHeadline = "正在从 GitHub 获取最新版本，请稍候…";
        VersionMetaText = $"当前版本：v{_updateService.CurrentVersion}";
        ReleaseNotesText = null;

        try
        {
            var status = await _updateService.CheckForUpdatesAsync();
            StatusHeadline = status.Message;
            IsUpdateAvailable = status.State == UpdateCheckState.Available;
            _latestReleaseUrl = status.ReleaseUrl ?? _updateService.ReleasesPageUrl;

            var parts = new List<string> { $"当前版本 v{status.CurrentVersion}" };
            if (!string.IsNullOrWhiteSpace(status.LatestVersion))
                parts.Add($"最新版本 v{status.LatestVersion}");
            if (status.PublishedAt.HasValue)
                parts.Add($"发布于 {ChinaTimeHelper.ToChinaTime(status.PublishedAt.Value):yyyy-MM-dd HH:mm}");
            if (status.Asset is not null)
                parts.Add($"安装包 {status.Asset.Size / 1048576.0:0.1} MB");

            VersionMetaText = string.Join("  ·  ", parts);
            ReleaseNotesText = status.ReleaseNotes;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"检查更新异常：{ex.Message}";
            StatusHeadline = "检查更新失败";
            IsUpdateAvailable = false;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (IsDownloading || IsChecking || IsReadyToRestart)
            return;

        if (!_updateService.CanApplySelfUpdate(out var reason))
        {
            ErrorMessage = reason;
            return;
        }

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        IsDownloading = true;
        ErrorMessage = null;
        DownloadProgressPercent = 0;
        ProgressText = "正在连接 GitHub Releases...";

        var progress = new Progress<(long Received, long Total, string Stage)>(p =>
        {
            ProgressText = p.Stage;
            DownloadProgressPercent = p.Total > 0 ? Math.Clamp(p.Received * 100.0 / p.Total, 0, 100) : 0;
        });

        try
        {
            var result = await _updateService.DownloadAndStageUpdateAsync(progress, _downloadCts.Token);
            if (result.State == UpdateInstallState.ReadyToRestart && !string.IsNullOrWhiteSpace(result.StagedFilePath))
            {
                _stagedExePath = result.StagedFilePath;
                IsReadyToRestart = true;
                IsUpdateAvailable = false;
                DownloadProgressPercent = 100;
                ProgressText = result.Message;
                StatusHeadline = $"新版本 v{result.Version} 已就绪";
            }
            else if (result.State == UpdateInstallState.Cancelled)
            {
                ProgressText = result.Message;
            }
            else
            {
                ErrorMessage = result.Message;
                ProgressText = "";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"更新失败：{ex.Message}";
        }
        finally
        {
            IsDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    [RelayCommand]
    private void ApplyAndRestart()
    {
        if (!IsReadyToRestart || string.IsNullOrWhiteSpace(_stagedExePath))
            return;

        ApplyUpdateAndRestartRequested?.Invoke(_stagedExePath);
    }

    [RelayCommand]
    private void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_latestReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法打开浏览器：{ex.Message}";
        }
    }

    public async Task SaveTokenAsync(string rawToken)
    {
        try
        {
            ErrorMessage = null;
            await _tokenStore.SaveTokenAsync(rawToken);
            await RefreshTokenStatusAsync();
            await CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ClearTokenAsync()
    {
        await _tokenStore.ClearTokenAsync();
        await RefreshTokenStatusAsync();
        ErrorMessage = null;
    }

    [RelayCommand]
    private void Close()
    {
        _downloadCts?.Cancel();
        CloseRequested?.Invoke();
    }
}
