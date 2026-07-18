using GalleryBrowser.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GalleryBrowser.Services;

internal sealed class StorageSettingsStore
{
    private const int CurrentStorageSettingsVersion = 1;
    private const string PCloudCredentialTarget = "GalleryBrowser/pCloudOAuthAccessToken";
    private const string DefaultPCloudApiHost = "eapi.pcloud.com";
    private const string DefaultPCloudTargetFolder = "";
    private const int DefaultPCloudCheckIntervalMinutes = 60;
    private const int DefaultPCloudBackupIntervalDays = 1;
    private const int DefaultPCloudMaximumSnapshots = 7;
    private const int DefaultPCloudIdleThresholdMinutes = 10;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _settingsPath;

    public StorageSettingsStore(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _settingsPath = Path.Combine(dataFolder, "storage.json");
    }

    public string ResolveCacheDatabasePath(string defaultPath)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            return string.IsNullOrWhiteSpace(settings.CacheDatabasePath)
                ? Path.GetFullPath(defaultPath)
                : Path.GetFullPath(settings.CacheDatabasePath);
        }
    }

    public string GetConfiguredCacheDatabasePath(string defaultPath) => ResolveCacheDatabasePath(defaultPath);

    public void SaveCacheDatabasePath(string databasePath)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe() with { CacheDatabasePath = Path.GetFullPath(databasePath) };
            WriteUnsafe(settings);
        }
    }

    public PCloudBackupSettingsDto GetPCloudSettings()
    {
        lock (_sync)
        {
            var storedSettings = ReadUnsafe().PCloud;
            var settings = storedSettings ?? new PCloudStorageSettings();
            var hasConfiguration = HasPCloudConfiguration(storedSettings);
            return new PCloudBackupSettingsDto(
                NormalizeApiHost(settings.ApiHost),
                NormalizeTargetFolder(settings.TargetFolder),
                settings.ClientId?.Trim() ?? string.Empty,
                PCloudBackupService.OAuthRedirectUri,
                hasConfiguration && !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(PCloudCredentialTarget)),
                settings.AccountEmail?.Trim() ?? string.Empty,
                settings.UsedQuotaBytes,
                settings.QuotaBytes,
                settings.LastBackupAt,
                settings.LastBackupFileName?.Trim() ?? string.Empty,
                settings.AutoBackupEnabled ?? (settings.LastBackupAt is not null),
                NormalizeIntegerSetting(settings.CheckIntervalMinutes, DefaultPCloudCheckIntervalMinutes, 1, 1_440),
                NormalizeIntegerSetting(settings.BackupIntervalDays, DefaultPCloudBackupIntervalDays, 1, 365),
                NormalizeIntegerSetting(settings.MaximumSnapshots, DefaultPCloudMaximumSnapshots, 1, 999),
                NormalizeIntegerSetting(settings.IdleThresholdMinutes, DefaultPCloudIdleThresholdMinutes, 1, 1_440));
        }
    }

    public string GetPCloudAccessToken()
    {
        string? token;
        lock (_sync)
        {
            if (!HasPCloudConfiguration(ReadUnsafe().PCloud))
            {
                throw new InvalidOperationException("pCloudと連携されていません。OAuth連携またはアクセストークンの登録を行ってください。");
            }
            token = WindowsCredentialStore.Read(PCloudCredentialTarget);
        }
        return string.IsNullOrWhiteSpace(token)
            ? throw new InvalidOperationException("pCloudと連携されていません。OAuth連携またはアクセストークンの登録を行ってください。")
            : token;
    }

    public void SavePCloudConfiguration(
        string apiHost,
        string targetFolder,
        string clientId,
        string? accessToken = null,
        bool? autoBackupEnabled = null,
        int? checkIntervalMinutes = null,
        int? backupIntervalDays = null,
        int? maximumSnapshots = null,
        int? idleThresholdMinutes = null)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var current = settings.PCloud ?? new PCloudStorageSettings();
            var pCloud = current with
            {
                ApiHost = NormalizeApiHost(apiHost),
                TargetFolder = NormalizeTargetFolder(targetFolder),
                ClientId = clientId.Trim(),
                AutoBackupEnabled = autoBackupEnabled ?? current.AutoBackupEnabled,
                CheckIntervalMinutes = checkIntervalMinutes is null
                    ? current.CheckIntervalMinutes
                    : NormalizeIntegerSetting(checkIntervalMinutes, DefaultPCloudCheckIntervalMinutes, 1, 1_440),
                BackupIntervalDays = backupIntervalDays is null
                    ? current.BackupIntervalDays
                    : NormalizeIntegerSetting(backupIntervalDays, DefaultPCloudBackupIntervalDays, 1, 365),
                MaximumSnapshots = maximumSnapshots is null
                    ? current.MaximumSnapshots
                    : NormalizeIntegerSetting(maximumSnapshots, DefaultPCloudMaximumSnapshots, 1, 999),
                IdleThresholdMinutes = idleThresholdMinutes is null
                    ? current.IdleThresholdMinutes
                    : NormalizeIntegerSetting(idleThresholdMinutes, DefaultPCloudIdleThresholdMinutes, 1, 1_440)
            };
            WriteUnsafe(settings with { PCloud = pCloud });
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                WindowsCredentialStore.Write(PCloudCredentialTarget, accessToken.Trim());
            }
        }
    }

    public void SavePCloudConnection(string apiHost, string accessToken, PCloudConnectionResult connection)
    {
        lock (_sync)
        {
            WindowsCredentialStore.Write(PCloudCredentialTarget, accessToken);
            var settings = ReadUnsafe();
            var pCloud = (settings.PCloud ?? new PCloudStorageSettings()) with
            {
                ApiHost = NormalizeApiHost(apiHost),
                AccountEmail = connection.AccountEmail,
                UsedQuotaBytes = connection.UsedQuotaBytes,
                QuotaBytes = connection.QuotaBytes
            };
            WriteUnsafe(settings with { PCloud = pCloud });
        }
    }

    public void SavePCloudConnectionStatus(PCloudConnectionResult connection)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var pCloud = (settings.PCloud ?? new PCloudStorageSettings()) with
            {
                AccountEmail = connection.AccountEmail,
                UsedQuotaBytes = connection.UsedQuotaBytes,
                QuotaBytes = connection.QuotaBytes
            };
            WriteUnsafe(settings with { PCloud = pCloud });
        }
    }

    public void SavePCloudBackupResult(PCloudBackupResult result)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var pCloud = (settings.PCloud ?? new PCloudStorageSettings()) with
            {
                LastBackupAt = result.UploadedAt,
                LastBackupFileName = result.FileName
            };
            WriteUnsafe(settings with { PCloud = pCloud });
        }
    }

    public void DisconnectPCloud()
    {
        lock (_sync)
        {
            WindowsCredentialStore.Delete(PCloudCredentialTarget);
            var settings = ReadUnsafe();
            var pCloud = (settings.PCloud ?? new PCloudStorageSettings()) with
            {
                AccountEmail = string.Empty,
                UsedQuotaBytes = null,
                QuotaBytes = null,
                AutoBackupEnabled = false
            };
            WriteUnsafe(settings with { PCloud = pCloud });
        }
    }

    public static string NormalizeApiHost(string? apiHost)
    {
        var host = string.IsNullOrWhiteSpace(apiHost) ? DefaultPCloudApiHost : apiHost.Trim().ToLowerInvariant();
        return host is "api.pcloud.com" or "eapi.pcloud.com"
            ? host
            : throw new ArgumentException("pCloud APIホストは api.pcloud.com または eapi.pcloud.com を指定してください。", nameof(apiHost));
    }

    public static string NormalizeTargetFolder(string? targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder))
        {
            return string.Empty;
        }
        var normalized = targetFolder.Trim().Replace('\\', '/');
        normalized = "/" + normalized.Trim('/');
        if (normalized == "/")
        {
            return normalized;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Any(segment => segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new ArgumentException("pCloud保存先フォルダの形式が正しくありません。", nameof(targetFolder));
        }
        return "/" + string.Join('/', segments);
    }

    private static int NormalizeIntegerSetting(int? value, int defaultValue, int minimum, int maximum)
    {
        var normalized = value ?? defaultValue;
        if (normalized < minimum || normalized > maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"設定値は{minimum:N0}～{maximum:N0}の範囲で指定してください。");
        }
        return normalized;
    }

    private static bool HasPCloudConfiguration(PCloudStorageSettings? settings) =>
        settings is not null &&
        (!string.IsNullOrWhiteSpace(settings.ClientId) ||
         !string.IsNullOrWhiteSpace(settings.TargetFolder) ||
         !string.IsNullOrWhiteSpace(settings.AccountEmail));

    private StorageBootstrapSettings ReadUnsafe()
    {
        if (!File.Exists(_settingsPath))
        {
            return new StorageBootstrapSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<StorageBootstrapSettings>(File.ReadAllText(_settingsPath), JsonOptions)
                ?? new StorageBootstrapSettings();
        }
        catch (JsonException)
        {
            var corruptPath = _settingsPath + ".corrupt-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
            File.Move(_settingsPath, corruptPath, overwrite: false);
            var defaults = new StorageBootstrapSettings();
            WriteUnsafe(defaults);
            return defaults;
        }
    }

    private void WriteUnsafe(StorageBootstrapSettings settings)
    {
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
    }

    // Property initializers supply defaults when an older settings file does not
    // contain a newly added property. Extension data keeps properties introduced by
    // a newer version intact if this version reads and writes the same file.
    private sealed record StorageBootstrapSettings
    {
        public int Version { get; init; } = CurrentStorageSettingsVersion;
        public string CacheDatabasePath { get; init; } = string.Empty;
        public PCloudStorageSettings? PCloud { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record PCloudStorageSettings
    {
        public string ApiHost { get; init; } = DefaultPCloudApiHost;
        public string TargetFolder { get; init; } = DefaultPCloudTargetFolder;
        public string ClientId { get; init; } = string.Empty;
        public string AccountEmail { get; init; } = string.Empty;
        public long? UsedQuotaBytes { get; init; }
        public long? QuotaBytes { get; init; }
        public DateTimeOffset? LastBackupAt { get; init; }
        public string LastBackupFileName { get; init; } = string.Empty;
        public bool? AutoBackupEnabled { get; init; }
        public int CheckIntervalMinutes { get; init; } = DefaultPCloudCheckIntervalMinutes;
        public int BackupIntervalDays { get; init; } = DefaultPCloudBackupIntervalDays;
        public int MaximumSnapshots { get; init; } = DefaultPCloudMaximumSnapshots;
        public int IdleThresholdMinutes { get; init; } = DefaultPCloudIdleThresholdMinutes;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
}
