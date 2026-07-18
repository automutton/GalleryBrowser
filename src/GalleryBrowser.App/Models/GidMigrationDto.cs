namespace GalleryBrowser.Models;

public sealed record GidMigrationPreviewDto(
    int TargetDigitCount,
    int ItemCount,
    int FileRenameCount,
    int DatabaseOnlyCount,
    int ReadyFileCount,
    int AlreadyRenamedFileCount,
    int MissingFileCount,
    int ConflictingFileCount,
    int MismatchedGidCount,
    bool IsAlreadyMigrated,
    bool CanExecute,
    IReadOnlyList<string> Issues);

public sealed record GidMigrationProgressDto(
    string Phase,
    int Completed,
    int Total,
    string Message);

public sealed record GidMigrationResultDto(
    int TargetDigitCount,
    int MigratedItemCount,
    int RenamedFileCount,
    int DeletedMissingItemCount,
    string BackupPath,
    string MappingPath);
