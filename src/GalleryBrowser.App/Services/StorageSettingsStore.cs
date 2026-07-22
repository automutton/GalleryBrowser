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
    private const string GoogleCalendarClientSecretCredentialTarget = "GalleryBrowser/GoogleCalendarClientSecret";
    private const string GoogleCalendarRefreshTokenCredentialTarget = "GalleryBrowser/GoogleCalendarRefreshToken";
    private const string DefaultPCloudApiHost = "eapi.pcloud.com";
    private const string DefaultPCloudTargetFolder = "";
    private const string DefaultPCloudArchiveRootFolder = "";
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

    public IReadOnlyList<DatabaseScanScheduleDto> GetDatabaseScanSchedules()
    {
        lock (_sync)
        {
            return NormalizeDatabaseScanSchedules(ReadUnsafe().DatabaseScanSchedules)
                .Select(ToDatabaseScanScheduleDto)
                .ToArray();
        }
    }

    public void SaveDatabaseScanSchedules(IEnumerable<DatabaseScanScheduleDto> schedules)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var existingSchedules = NormalizeDatabaseScanSchedules(settings.DatabaseScanSchedules)
                .GroupBy(schedule => schedule.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var stored = NormalizeDatabaseScanSchedules(schedules.Select(schedule => new DatabaseScanScheduleStorage
            {
                Id = schedule.Id,
                Weekdays = schedule.Weekdays,
                Time = schedule.Time,
                Categories = schedule.Categories,
                LastStartedAt = existingSchedules.TryGetValue(schedule.Id, out var existing)
                    ? existing.LastStartedAt
                    : schedule.LastStartedAt
            }).ToArray());
            WriteUnsafe(settings with { DatabaseScanSchedules = stored });
        }
    }

    public void MarkDatabaseScanScheduleStarted(string scheduleId, DateTimeOffset startedAt)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var schedules = NormalizeDatabaseScanSchedules(settings.DatabaseScanSchedules);
            var changed = false;
            for (var index = 0; index < schedules.Length; index++)
            {
                if (!string.Equals(schedules[index].Id, scheduleId, StringComparison.Ordinal))
                {
                    continue;
                }

                schedules[index] = schedules[index] with { LastStartedAt = startedAt };
                changed = true;
                break;
            }

            if (changed)
            {
                WriteUnsafe(settings with { DatabaseScanSchedules = schedules });
            }
        }
    }

    public TimeSpan? GetRecentDatabaseScanAverageDuration(
        IEnumerable<string> categories,
        DateTimeOffset? now = null)
    {
        lock (_sync)
        {
            var current = now ?? DateTimeOffset.UtcNow;
            var cutoff = current.ToUniversalTime().AddDays(-3);
            var requestedCategories = NormalizeCategorySet(categories);
            var recent = NormalizeDatabaseScanRunHistory(ReadUnsafe().DatabaseScanRunHistory)
                .Where(run => run.CompletedAt.ToUniversalTime() >= cutoff)
                .ToArray();
            if (recent.Length == 0)
            {
                return null;
            }

            var matching = recent
                .Where(run => NormalizeCategorySet(run.Categories)
                    .SequenceEqual(requestedCategories, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var samples = matching.Length > 0 ? matching : recent;
            return TimeSpan.FromSeconds(samples.Average(run => run.DurationSeconds));
        }
    }

    public void RecordDatabaseScanDuration(
        IEnumerable<string> categories,
        DateTimeOffset completedAt,
        TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_sync)
        {
            var settings = ReadUnsafe();
            var cutoff = completedAt.ToUniversalTime().AddDays(-30);
            var history = NormalizeDatabaseScanRunHistory(settings.DatabaseScanRunHistory)
                .Where(run => run.CompletedAt.ToUniversalTime() >= cutoff)
                .Append(new DatabaseScanRunHistoryStorage
                {
                    CompletedAt = completedAt,
                    DurationSeconds = Math.Max(1, duration.TotalSeconds),
                    Categories = NormalizeCategorySet(categories)
                })
                .OrderBy(run => run.CompletedAt)
                .TakeLast(256)
                .ToArray();
            WriteUnsafe(settings with { DatabaseScanRunHistory = history });
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
                NormalizeTargetFolder(settings.ArchiveRootFolder),
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
        string archiveRootFolder,
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
                ArchiveRootFolder = NormalizeTargetFolder(archiveRootFolder),
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

    public GoogleCalendarSyncSettingsDto GetGoogleCalendarSettings()
    {
        lock (_sync)
        {
            var settings = ReadUnsafe().GoogleCalendar ?? new GoogleCalendarStorageSettings();
            return new GoogleCalendarSyncSettingsDto(
                settings.AutoSyncEnabled,
                settings.ClientId?.Trim() ?? string.Empty,
                !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(GoogleCalendarClientSecretCredentialTarget)),
                !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(GoogleCalendarRefreshTokenCredentialTarget)),
                NormalizeGoogleCalendarId(settings.CalendarId),
                GoogleCalendarSyncService.OAuthRedirectUri,
                settings.LastSyncedAt,
                settings.LastSyncError?.Trim() ?? string.Empty);
        }
    }

    public void SaveGoogleCalendarConfiguration(
        bool autoSyncEnabled,
        string clientId,
        string? clientSecret,
        string calendarId)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var current = settings.GoogleCalendar ?? new GoogleCalendarStorageSettings();
            var normalizedClientId = clientId.Trim();
            var clientChanged = !string.Equals(current.ClientId?.Trim(), normalizedClientId, StringComparison.Ordinal);
            if (clientChanged)
            {
                WindowsCredentialStore.Delete(GoogleCalendarRefreshTokenCredentialTarget);
                if (string.IsNullOrWhiteSpace(clientSecret))
                {
                    WindowsCredentialStore.Delete(GoogleCalendarClientSecretCredentialTarget);
                }
            }
            if (!string.IsNullOrWhiteSpace(clientSecret))
            {
                WindowsCredentialStore.Write(GoogleCalendarClientSecretCredentialTarget, clientSecret.Trim());
            }

            WriteUnsafe(settings with
            {
                GoogleCalendar = current with
                {
                    AutoSyncEnabled = autoSyncEnabled,
                    ClientId = normalizedClientId,
                    CalendarId = NormalizeGoogleCalendarId(calendarId),
                    LastSyncError = clientChanged ? string.Empty : current.LastSyncError
                }
            });
        }
    }

    public string GetGoogleCalendarClientSecret()
    {
        lock (_sync)
        {
            return WindowsCredentialStore.Read(GoogleCalendarClientSecretCredentialTarget)?.Trim() ?? string.Empty;
        }
    }

    public string GetGoogleCalendarRefreshToken()
    {
        lock (_sync)
        {
            var token = WindowsCredentialStore.Read(GoogleCalendarRefreshTokenCredentialTarget)?.Trim();
            return string.IsNullOrWhiteSpace(token)
                ? throw new InvalidOperationException("Google Calendarと連携されていません。先にOAuth連携を実行してください。")
                : token;
        }
    }

    public void SaveGoogleCalendarConnection(string refreshToken)
    {
        lock (_sync)
        {
            WindowsCredentialStore.Write(GoogleCalendarRefreshTokenCredentialTarget, refreshToken.Trim());
            var settings = ReadUnsafe();
            var current = settings.GoogleCalendar ?? new GoogleCalendarStorageSettings();
            WriteUnsafe(settings with
            {
                GoogleCalendar = current with { LastSyncError = string.Empty }
            });
        }
    }

    public void SaveGoogleCalendarSyncResult(GoogleCalendarSyncResult result)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var current = settings.GoogleCalendar ?? new GoogleCalendarStorageSettings();
            WriteUnsafe(settings with
            {
                GoogleCalendar = current with
                {
                    LastSyncedAt = result.SyncedAt,
                    LastSyncError = string.Empty
                }
            });
        }
    }

    public void SaveGoogleCalendarSyncError(string message)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var current = settings.GoogleCalendar ?? new GoogleCalendarStorageSettings();
            WriteUnsafe(settings with
            {
                GoogleCalendar = current with { LastSyncError = message.Trim() }
            });
        }
    }

    public void DisconnectGoogleCalendar()
    {
        lock (_sync)
        {
            WindowsCredentialStore.Delete(GoogleCalendarRefreshTokenCredentialTarget);
            var settings = ReadUnsafe();
            var current = settings.GoogleCalendar ?? new GoogleCalendarStorageSettings();
            WriteUnsafe(settings with
            {
                GoogleCalendar = current with
                {
                    AutoSyncEnabled = false,
                    LastSyncError = string.Empty
                }
            });
        }
    }

    private static string NormalizeGoogleCalendarId(string? calendarId) =>
        string.IsNullOrWhiteSpace(calendarId) ? "primary" : calendarId.Trim();

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

    private static DatabaseScanScheduleStorage[] NormalizeDatabaseScanSchedules(
        IEnumerable<DatabaseScanScheduleStorage>? schedules)
    {
        var normalized = new List<DatabaseScanScheduleStorage>();
        foreach (var schedule in schedules ?? [])
        {
            if (normalized.Count >= 64)
            {
                break;
            }

            var id = string.IsNullOrWhiteSpace(schedule.Id)
                ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                : schedule.Id.Trim();
            var weekdays = (schedule.Weekdays ?? [])
                .Where(day => day is >= 0 and <= 6)
                .Distinct()
                .Order()
                .ToArray();
            if (weekdays.Length == 0)
            {
                throw new ArgumentException("フォルダ走査スケジュールには曜日を1つ以上指定してください。");
            }

            if (!TimeOnly.TryParseExact(
                    schedule.Time?.Trim(),
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedTime))
            {
                throw new ArgumentException("フォルダ走査スケジュールの時刻はHH:mm形式で指定してください。");
            }

            var categories = (schedule.Categories ?? [])
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Select(category => category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (categories.Length == 0)
            {
                throw new ArgumentException("フォルダ走査スケジュールには対象区分を1つ以上指定してください。");
            }

            normalized.Add(schedule with
            {
                Id = id,
                Weekdays = weekdays,
                Time = parsedTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                Categories = categories
            });
        }
        return normalized.ToArray();
    }

    private static DatabaseScanScheduleDto ToDatabaseScanScheduleDto(DatabaseScanScheduleStorage schedule) =>
        new(schedule.Id, schedule.Weekdays, schedule.Time, schedule.Categories, schedule.LastStartedAt);

    private static DatabaseScanRunHistoryStorage[] NormalizeDatabaseScanRunHistory(
        IEnumerable<DatabaseScanRunHistoryStorage>? history) =>
        (history ?? [])
            .Where(run => run.CompletedAt != default &&
                          run.DurationSeconds is > 0 and <= 604800)
            .Select(run => run with
            {
                Categories = NormalizeCategorySet(run.Categories)
            })
            .OrderBy(run => run.CompletedAt)
            .TakeLast(256)
            .ToArray();

    private static string[] NormalizeCategorySet(IEnumerable<string>? categories) =>
        (categories ?? [])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
        public GoogleCalendarStorageSettings? GoogleCalendar { get; init; }
        public DatabaseScanScheduleStorage[] DatabaseScanSchedules { get; init; } = [];
        public DatabaseScanRunHistoryStorage[] DatabaseScanRunHistory { get; init; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record DatabaseScanScheduleStorage
    {
        public string Id { get; init; } = string.Empty;
        public int[] Weekdays { get; init; } = [];
        public string Time { get; init; } = "03:00";
        public string[] Categories { get; init; } = [];
        public DateTimeOffset? LastStartedAt { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record DatabaseScanRunHistoryStorage
    {
        public DateTimeOffset CompletedAt { get; init; }
        public double DurationSeconds { get; init; }
        public string[] Categories { get; init; } = [];

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record PCloudStorageSettings
    {
        public string ApiHost { get; init; } = DefaultPCloudApiHost;
        public string TargetFolder { get; init; } = DefaultPCloudTargetFolder;
        public string ArchiveRootFolder { get; init; } = DefaultPCloudArchiveRootFolder;
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

    private sealed record GoogleCalendarStorageSettings
    {
        public bool AutoSyncEnabled { get; init; }
        public string ClientId { get; init; } = string.Empty;
        public string CalendarId { get; init; } = "primary";
        public DateTimeOffset? LastSyncedAt { get; init; }
        public string LastSyncError { get; init; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
}
