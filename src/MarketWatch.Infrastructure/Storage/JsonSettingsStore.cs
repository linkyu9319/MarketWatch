using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketWatch.Core.Interfaces;
using MarketWatch.Core.Models;
using MarketWatch.Core.Services;

namespace MarketWatch.Infrastructure.Storage;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string SettingsFilePath { get; }

    public JsonSettingsStore(IAppLogger logger, string? baseDirectory = null)
    {
        _logger = logger;
        var dir = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MarketWatch");
        SettingsFilePath = Path.Combine(dir, "settings.json");
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = DefaultPresets.CreateDefaultSettings();
                await WriteAtomicCoreAsync(SettingsFilePath, defaults, cancellationToken).ConfigureAwait(false);
                return new SettingsLoadResult(defaults, false, null, null);
            }

            try
            {
                var json = await File.ReadAllTextAsync(SettingsFilePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings is null)
                {
                    throw new JsonException("Deserialized settings object was null.");
                }

                settings.Normalize();
                return new SettingsLoadResult(settings, false, null, null);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidDataException)
            {
                _logger.Error("Settings", "Settings file corrupted; backing up and restoring defaults.", ex);
                var backupPath = BackupCorruptedFile(SettingsFilePath);
                var defaults = DefaultPresets.CreateDefaultSettings();
                await WriteAtomicCoreAsync(SettingsFilePath, defaults, cancellationToken).ConfigureAwait(false);
                return new SettingsLoadResult(
                    defaults,
                    WasCorrupted: true,
                    BackupFilePath: backupPath,
                    Notice: $"配置文件已损坏，已备份至 {Path.GetFileName(backupPath)} 并恢复默认配置。");
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicCoreAsync(SettingsFilePath, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task ExportAsync(AppSettings settings, string targetPath, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        await WriteAtomicCoreAsync(targetPath, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppSettings> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var json = await File.ReadAllTextAsync(sourcePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var imported = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("导入的配置文件内容为空或格式无效。");
        imported.Normalize();
        if (!ReminderRuleValidator.ValidateRules(imported.ReminderRules, out var ruleError))
        {
            throw new InvalidDataException(ruleError ?? "导入的配置文件包含无效提醒时间。");
        }
        await SaveAsync(imported, cancellationToken).ConfigureAwait(false);
        return imported;
    }

    private static string BackupCorruptedFile(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var backup = Path.Combine(dir, $"settings.corrupt.{DateTime.Now:yyyyMMddHHmmss}.json");
        try
        {
            File.Copy(path, backup, overwrite: true);
        }
        catch
        {
            // Ignore backup copy failure if file is unreadable
        }
        return backup;
    }

    internal static async Task WriteAtomicCoreAsync<T>(string filePath, T payload, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(filePath))
        {
            File.Replace(tempPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, filePath);
        }
    }
}
