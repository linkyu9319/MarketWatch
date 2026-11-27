using System.IO;
using System.Security.Cryptography;
using System.Text;
using MarketWatch.Core.Interfaces;

namespace MarketWatch.App.Services;

/// <summary>
/// Stores optional GitHub Personal Access Token encrypted with Windows DPAPI (CurrentUser scope).
/// Never writes plaintext tokens, never included in config export, never logged.
/// </summary>
public sealed class DpapiGitHubTokenStore : IGitHubTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MarketWatch.GitHubToken.v1");
    private readonly string _tokenFilePath;
    private readonly IAppLogger _logger;

    public DpapiGitHubTokenStore(IAppLogger logger, string? baseDirectory = null)
    {
        _logger = logger;
        var dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketWatch");
        _tokenFilePath = Path.Combine(dir, "github-token.dpapi");
    }

    public Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(_tokenFilePath))
                return Task.FromResult<string?>(null);

            var protectedBytes = File.ReadAllBytes(_tokenFilePath);
            if (protectedBytes.Length == 0)
                return Task.FromResult<string?>(null);

            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var token = Encoding.UTF8.GetString(plainBytes).Trim();
            return Task.FromResult<string?>(string.IsNullOrWhiteSpace(token) ? null : token);
        }
        catch (Exception ex)
        {
            _logger.Warn("Updates", $"Failed decrypting saved GitHub token ({ex.GetType().Name}); ignoring.");
            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveTokenAsync(string token, CancellationToken ct = default)
    {
        var trimmed = token?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > 2048 || trimmed.Any(char.IsWhiteSpace))
            throw new ArgumentException("请输入有效的 GitHub 访问令牌（不能包含空白字符）。");

        var dir = Path.GetDirectoryName(_tokenFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var plainBytes = Encoding.UTF8.GetBytes(trimmed);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        var tempPath = $"{_tokenFilePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, _tokenFilePath, overwrite: true);
        return Task.CompletedTask;
    }

    public Task ClearTokenAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_tokenFilePath))
                File.Delete(_tokenFilePath);
        }
        catch (Exception ex)
        {
            _logger.Warn("Updates", $"Failed clearing GitHub token file: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public async Task<bool> HasTokenAsync(CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(await GetTokenAsync(ct).ConfigureAwait(false));
}
