namespace GalleryBrowser.Models;

public sealed record GalleryWorkDto(
    string Id,
    string Path,
    string Name,
    string Category,
    string TopFolder,
    string Creator,
    string Title,
    string Character,
    int Rating,
    int ImageCount,
    double? DurationSeconds,
    string LastAccessTime,
    string LastWriteTime,
    IReadOnlyList<string> Tags);

// Value is the stable query key. Label is optional display text for values such as scoped Character filters.
public sealed record GalleryFilterOptionDto(string Value, int Count, string? Label = null);

public sealed record GalleryWorksPageDto(
    IReadOnlyList<GalleryWorkDto> Items,
    int Total,
    IReadOnlyList<GalleryFilterOptionDto> Ratings,
    IReadOnlyList<GalleryFilterOptionDto> Tags,
    IReadOnlyList<GalleryFilterOptionDto> Creators,
    IReadOnlyList<GalleryFilterOptionDto> Titles,
    IReadOnlyList<GalleryFilterOptionDto> Characters);

public sealed record GalleryWorkFiltersDto(
    IReadOnlyList<GalleryFilterOptionDto> Ratings,
    IReadOnlyList<GalleryFilterOptionDto> Tags,
    IReadOnlyList<GalleryFilterOptionDto> Creators,
    IReadOnlyList<GalleryFilterOptionDto> Titles,
    IReadOnlyList<GalleryFilterOptionDto> Characters);

public sealed record GalleryRandomPickResultDto(
    IReadOnlyList<GalleryWorkDto> Items,
    int PopulationCount,
    int DistinctTitleCount);
