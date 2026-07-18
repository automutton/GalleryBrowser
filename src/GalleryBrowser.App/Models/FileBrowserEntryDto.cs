namespace GalleryBrowser.Models;

public sealed record FileBrowserEntryDto(
    string Name,
    string RomanizedName,
    string Path,
    bool IsDirectory,
    string Extension,
    long? Size,
    int? PageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset AccessedAt,
    DateTimeOffset ModifiedAt);

public sealed record FileBrowserDirectoryDto(
    string Path,
    string? ParentPath,
    IReadOnlyList<string> Roots,
    IReadOnlyList<FileBrowserEntryDto> Entries,
    bool IsTruncated);
