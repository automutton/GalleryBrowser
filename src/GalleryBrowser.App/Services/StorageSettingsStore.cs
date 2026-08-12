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
    private const string DiscordWebhookCredentialTarget = "GalleryBrowser/DiscordWebhookUrl";
    private const string LineChannelAccessTokenCredentialTarget = "GalleryBrowser/LineChannelAccessToken";
    private const string LineRecipientUserIdCredentialTarget = "GalleryBrowser/LineRecipientUserId";
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

    public AiConciergeSettingsDto GetAiConciergeSettings(string defaultDataDirectory)
    {
        lock (_sync)
        {
            var stored = ReadUnsafe().AiConcierge ?? new AiConciergeStorageSettings();
            return new AiConciergeSettingsDto(
                ResolveAiConciergeDataDirectory(stored.DataDirectory, defaultDataDirectory),
                NormalizeAiConciergeOption(stored.Model),
                NormalizeAiConciergeOption(stored.ReasoningEffort),
                NormalizeAiConciergeOption(stored.ServiceTier));
        }
    }

    public void SaveAiConciergeRuntimeSettings(
        string? model,
        string? reasoningEffort,
        string? serviceTier)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var current = settings.AiConcierge ?? new AiConciergeStorageSettings();
            WriteUnsafe(settings with
            {
                AiConcierge = current with
                {
                    Model = NormalizeAiConciergeOption(model),
                    ReasoningEffort = NormalizeAiConciergeOption(reasoningEffort),
                    ServiceTier = NormalizeAiConciergeOption(serviceTier)
                }
            });
        }
    }

    public void SaveAiConciergeDataDirectory(string? dataDirectory)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var current = settings.AiConcierge ?? new AiConciergeStorageSettings();
            var normalized = string.IsNullOrWhiteSpace(dataDirectory)
                ? string.Empty
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataDirectory.Trim()));
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                Directory.CreateDirectory(normalized);
            }
            WriteUnsafe(settings with
            {
                AiConcierge = current with { DataDirectory = normalized }
            });
        }
    }

    private static string ResolveAiConciergeDataDirectory(
        string? configuredDirectory,
        string defaultDataDirectory)
    {
        var directory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(defaultDataDirectory, "ai-concierge")
            : Environment.ExpandEnvironmentVariables(configuredDirectory.Trim());
        return Path.GetFullPath(directory);
    }

    private static string NormalizeAiConciergeOption(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > 120 ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException("AIコンシェルジュのモデル設定が正しくありません。");
        }
        return normalized;
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

    public IReadOnlyList<NotificationScheduleDto> GetNotificationSchedules()
    {
        lock (_sync)
        {
            return NormalizeNotificationSchedules(ReadUnsafe().NotificationSchedules)
                .Select(ToNotificationScheduleDto)
                .ToArray();
        }
    }

    public void SaveNotificationSchedules(IEnumerable<NotificationScheduleDto> schedules)
    {
        lock (_sync)
        {
            var settings = ReadUnsafe();
            var existingSchedules = NormalizeNotificationSchedules(settings.NotificationSchedules)
                .GroupBy(schedule => schedule.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var stored = NormalizeNotificationSchedules(schedules.Select(schedule => new NotificationScheduleStorage
            {
                Id = schedule.Id,
                Time = schedule.Time,
                LastStartedAt = existingSchedules.TryGetValue(schedule.Id, out var existing)
                    ? existing.LastStartedAt
                    : schedule.LastStartedAt
            }));
            WriteUnsafe(settings with { NotificationSchedules = stored });
        }
    }

    public void MarkNotificationSchedulesStarted(
        IEnumerable<string> scheduleIds,
        DateTimeOffset startedAt)
    {
        var requestedIds = scheduleIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (requestedIds.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var settings = ReadUnsafe();
            var schedules = NormalizeNotificationSchedules(settings.NotificationSchedules);
            var changed = false;
            for (var index = 0; index < schedules.Length; index++)
            {
                if (!requestedIds.Contains(schedules[index].Id))
                {
                    continue;
                }

                schedules[index] = schedules[index] with { LastStartedAt = startedAt };
                changed = true;
            }

            if (changed)
            {
                WriteUnsafe(settings with { NotificationSchedules = schedules });
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

    public DiscordNotificationSettingsDto GetDiscordNotificationSettings()
    {
        lock (_sync)
        {
            var settings = ReadUnsafe().DiscordNotifications ?? new DiscordNotificationStorageSettings();
            return new DiscordNotificationSettingsDto(
                settings.Enabled,
                !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(DiscordWebhookCredentialTarget)),
                settings.NotifyCreatorFollowAlert,
                settings.NotifySubscriptionEnding,
                settings.NotifySubscriptionReminder,
                settings.NotifyCreatorTasks,
                settings.NotifyScheduledScanStarted,
                settings.NotifyScheduledScanCompleted);
        }
    }

    public string GetDiscordWebhookUrl()
    {
        lock (_sync)
        {
            return WindowsCredentialStore.Read(DiscordWebhookCredentialTarget)?.Trim() ?? string.Empty;
        }
    }

    public void SaveDiscordNotificationConfiguration(
        bool enabled,
        string? webhookUrl,
        bool notifyCreatorFollowAlert,
        bool notifySubscriptionEnding,
        bool notifySubscriptionReminder,
        bool notifyCreatorTasks,
        bool notifyScheduledScanStarted,
        bool notifyScheduledScanCompleted)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(webhookUrl))
            {
                WindowsCredentialStore.Write(DiscordWebhookCredentialTarget, webhookUrl.Trim());
            }

            if (enabled && string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(DiscordWebhookCredentialTarget)))
            {
                throw new InvalidOperationException("Discord通知を有効にするにはWebhook URLを登録してください。");
            }

            var settings = ReadUnsafe();
            WriteUnsafe(settings with
            {
                DiscordNotifications = new DiscordNotificationStorageSettings
                {
                    Enabled = enabled,
                    NotifyCreatorFollowAlert = notifyCreatorFollowAlert,
                    NotifySubscriptionEnding = notifySubscriptionEnding,
                    NotifySubscriptionReminder = notifySubscriptionReminder,
                    NotifyCreatorTasks = notifyCreatorTasks,
                    NotifyScheduledScanStarted = notifyScheduledScanStarted,
                    NotifyScheduledScanCompleted = notifyScheduledScanCompleted
                }
            });
        }
    }

    public void DisconnectDiscordNotifications()
    {
        lock (_sync)
        {
            WindowsCredentialStore.Delete(DiscordWebhookCredentialTarget);
            var settings = ReadUnsafe();
            var current = settings.DiscordNotifications ?? new DiscordNotificationStorageSettings();
            WriteUnsafe(settings with
            {
                DiscordNotifications = current with { Enabled = false }
            });
        }
    }

    public LineNotificationSettingsDto GetLineNotificationSettings()
    {
        lock (_sync)
        {
            var settings = ReadUnsafe().LineNotifications ?? new LineNotificationStorageSettings();
            return new LineNotificationSettingsDto(
                settings.Enabled,
                !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(LineChannelAccessTokenCredentialTarget)),
                !string.IsNullOrWhiteSpace(WindowsCredentialStore.Read(LineRecipientUserIdCredentialTarget)),
                settings.NotifyCreatorFollowAlert,
                settings.NotifySubscriptionEnding,
                settings.NotifySubscriptionReminder,
                settings.NotifyCreatorTasks,
                settings.NotifyScheduledScanStarted,
                settings.NotifyScheduledScanCompleted);
        }
    }

    public (string ChannelAccessToken, string RecipientUserId) GetLineCredentials()
    {
        lock (_sync)
        {
            return (
                WindowsCredentialStore.Read(LineChannelAccessTokenCredentialTarget)?.Trim() ?? string.Empty,
                WindowsCredentialStore.Read(LineRecipientUserIdCredentialTarget)?.Trim() ?? string.Empty);
        }
    }

    public void SaveLineNotificationConfiguration(
        bool enabled,
        string? channelAccessToken,
        string? recipientUserId,
        bool notifyCreatorFollowAlert,
        bool notifySubscriptionEnding,
        bool notifySubscriptionReminder,
        bool notifyCreatorTasks,
        bool notifyScheduledScanStarted,
        bool notifyScheduledScanCompleted)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(channelAccessToken))
            {
                WindowsCredentialStore.Write(LineChannelAccessTokenCredentialTarget, channelAccessToken.Trim());
            }
            if (!string.IsNullOrWhiteSpace(recipientUserId))
            {
                WindowsCredentialStore.Write(LineRecipientUserIdCredentialTarget, recipientUserId.Trim());
            }

            if (enabled)
            {
                var credentials = GetLineCredentials();
                if (string.IsNullOrWhiteSpace(credentials.ChannelAccessToken))
                {
                    throw new InvalidOperationException("LINE通知を有効にするにはチャネルアクセストークンを登録してください。");
                }
                if (string.IsNullOrWhiteSpace(credentials.RecipientUserId))
                {
                    throw new InvalidOperationException("LINE通知を有効にするには受信先User IDを登録してください。");
                }
            }

            var settings = ReadUnsafe();
            WriteUnsafe(settings with
            {
                LineNotifications = new LineNotificationStorageSettings
                {
                    Enabled = enabled,
                    NotifyCreatorFollowAlert = notifyCreatorFollowAlert,
                    NotifySubscriptionEnding = notifySubscriptionEnding,
                    NotifySubscriptionReminder = notifySubscriptionReminder,
                    NotifyCreatorTasks = notifyCreatorTasks,
                    NotifyScheduledScanStarted = notifyScheduledScanStarted,
                    NotifyScheduledScanCompleted = notifyScheduledScanCompleted
                }
            });
        }
    }

    public void DisconnectLineNotifications()
    {
        lock (_sync)
        {
            WindowsCredentialStore.Delete(LineChannelAccessTokenCredentialTarget);
            WindowsCredentialStore.Delete(LineRecipientUserIdCredentialTarget);
            var settings = ReadUnsafe();
            var current = settings.LineNotifications ?? new LineNotificationStorageSettings();
            WriteUnsafe(settings with
            {
                LineNotifications = current with { Enabled = false }
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

    private static NotificationScheduleStorage[] NormalizeNotificationSchedules(
        IEnumerable<NotificationScheduleStorage>? schedules)
    {
        var normalized = new List<NotificationScheduleStorage>();
        var registeredTimes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in schedules ?? [])
        {
            if (normalized.Count >= 32)
            {
                break;
            }

            if (!TimeOnly.TryParseExact(
                    schedule.Time?.Trim(),
                    "HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedTime))
            {
                throw new ArgumentException("通知スケジュールの時刻はHH:mm形式で指定してください。");
            }

            var time = parsedTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (!registeredTimes.Add(time))
            {
                continue;
            }

            normalized.Add(schedule with
            {
                Id = string.IsNullOrWhiteSpace(schedule.Id)
                    ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                    : schedule.Id.Trim(),
                Time = time
            });
        }
        return normalized.ToArray();
    }

    private static NotificationScheduleDto ToNotificationScheduleDto(NotificationScheduleStorage schedule) =>
        new(schedule.Id, schedule.Time, schedule.LastStartedAt);

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
        public DiscordNotificationStorageSettings? DiscordNotifications { get; init; }
        public LineNotificationStorageSettings? LineNotifications { get; init; }
        public AiConciergeStorageSettings? AiConcierge { get; init; }
        public NotificationScheduleStorage[] NotificationSchedules { get; init; } =
        [
            new() { Id = "default-0900", Time = "09:00" }
        ];
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

    private sealed record DiscordNotificationStorageSettings
    {
        public bool Enabled { get; init; }
        public bool NotifyCreatorFollowAlert { get; init; } = true;
        public bool NotifySubscriptionEnding { get; init; } = true;
        public bool NotifySubscriptionReminder { get; init; } = true;
        public bool NotifyCreatorTasks { get; init; } = true;
        public bool NotifyScheduledScanStarted { get; init; } = true;
        public bool NotifyScheduledScanCompleted { get; init; } = true;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record LineNotificationStorageSettings
    {
        public bool Enabled { get; init; }
        public bool NotifyCreatorFollowAlert { get; init; } = true;
        public bool NotifySubscriptionEnding { get; init; } = true;
        public bool NotifySubscriptionReminder { get; init; } = true;
        public bool NotifyCreatorTasks { get; init; } = true;
        public bool NotifyScheduledScanStarted { get; init; } = true;
        public bool NotifyScheduledScanCompleted { get; init; } = true;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record AiConciergeStorageSettings
    {
        public string DataDirectory { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string ReasoningEffort { get; init; } = string.Empty;
        public string ServiceTier { get; init; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed record NotificationScheduleStorage
    {
        public string Id { get; init; } = string.Empty;
        public string Time { get; init; } = "09:00";
        public DateTimeOffset? LastStartedAt { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }
}
