namespace GalleryBrowser.Models;

public sealed record GalleryStorageSettingsDto(
    string DatabasePath,
    string CacheDatabasePath,
    string ConfiguredCacheDatabasePath,
    bool CacheDatabaseRestartRequired);

public sealed record GalleryCacheDatabaseMoveResult(
    string DatabasePath,
    bool RestartRequired);

public sealed record GalleryDatabaseMergeResult(
    string DatabasePath,
    string ImportedDatabasePath,
    string CanonicalSource,
    int ImportedItems,
    int ImportedCreatorTrackingEntries,
    int ImportedBookmarks,
    int ImportedStickyNotes,
    string CurrentDatabaseBackupPath,
    string SelectedDatabaseBackupPath);

public sealed record PCloudBackupSettingsDto(
    string ApiHost,
    string TargetFolder,
    string ArchiveRootFolder,
    string ClientId,
    string RedirectUri,
    bool HasAccessToken,
    string AccountEmail,
    long? UsedQuotaBytes,
    long? QuotaBytes,
    DateTimeOffset? LastBackupAt,
    string LastBackupFileName,
    bool AutoBackupEnabled,
    int CheckIntervalMinutes,
    int BackupIntervalDays,
    int MaximumSnapshots,
    int IdleThresholdMinutes);

public sealed record PCloudConnectionResult(
    string AccountEmail,
    long UsedQuotaBytes,
    long QuotaBytes);

public sealed record PCloudBackupResult(
    string FileName,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    string TargetFolder,
    int RemovedOldSnapshots = 0,
    string RetentionWarning = "");

public sealed record PCloudArchiveResult(
    string LocalPath,
    string RemotePath,
    long SizeBytes,
    DateTimeOffset UploadedAt,
    int FileCount = 1);

public sealed record PCloudArchiveProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentPath);

public sealed record PCloudSnapshotDto(
    long FileId,
    string FileName,
    long SizeBytes,
    DateTimeOffset CreatedAt);

public sealed record PCloudRestorePreparationResult(
    string FileName,
    string CurrentDatabaseBackupPath);

public sealed record PCloudStartupRestoreResult(
    bool Succeeded,
    string Message,
    string BackupPath = "");

public sealed record GoogleCalendarSyncSettingsDto(
    bool AutoSyncEnabled,
    string ClientId,
    bool HasClientSecret,
    bool HasRefreshToken,
    string CalendarId,
    string RedirectUri,
    DateTimeOffset? LastSyncedAt,
    string LastSyncError);

public sealed record GoogleCalendarSyncResult(
    int Created,
    int Updated,
    int Deleted,
    int Unchanged,
    DateTimeOffset SyncedAt);

public sealed record DiscordNotificationSettingsDto(
    bool Enabled,
    bool HasWebhookUrl,
    bool NotifyCreatorFollowAlert,
    bool NotifySubscriptionEnding,
    bool NotifySubscriptionReminder,
    bool NotifyCreatorTasks,
    bool NotifyScheduledScanStarted,
    bool NotifyScheduledScanCompleted);

public sealed record NotificationScheduleDto(
    string Id,
    string Time,
    DateTimeOffset? LastStartedAt);

public sealed record LineNotificationSettingsDto(
    bool Enabled,
    bool HasChannelAccessToken,
    bool HasRecipientUserId,
    bool NotifyCreatorFollowAlert,
    bool NotifySubscriptionEnding,
    bool NotifySubscriptionReminder,
    bool NotifyCreatorTasks,
    bool NotifyScheduledScanStarted,
    bool NotifyScheduledScanCompleted);

public sealed record LineMessageQuotaDto(
    string Type,
    long? Limit,
    long Consumption,
    DateTimeOffset CheckedAt);
