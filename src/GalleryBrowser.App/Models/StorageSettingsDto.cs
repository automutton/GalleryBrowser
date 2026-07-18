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
