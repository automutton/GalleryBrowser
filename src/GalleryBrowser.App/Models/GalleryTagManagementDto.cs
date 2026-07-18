namespace GalleryBrowser.Models;

public sealed record GalleryTagDefinitionDto(
    long Id,
    string Tag,
    int UseFlag,
    int Position,
    IReadOnlyList<string> Categories,
    int ItemCount);

public sealed record GalleryTagManagementSnapshotDto(
    IReadOnlyList<GalleryTagDefinitionDto> Tags);

public sealed record GalleryTagImportRowDto(
    int RowNumber,
    long? TagId,
    string Tag,
    string NewTag,
    int? UseFlag);

public sealed record GalleryTagExportRowDto(
    long TagId,
    string Tag,
    int UseFlag);

public sealed record GalleryTagAssignmentOptionDto(
    long Id,
    string Tag,
    int WorkCount,
    string SearchText);

public sealed record GalleryTagAssignmentOptionsDto(
    IReadOnlyList<GalleryTagAssignmentOptionDto> CreatorTitleTags,
    IReadOnlyList<GalleryTagAssignmentOptionDto> AvailableTags,
    IReadOnlyList<GalleryTagAssignmentOptionDto> CommonAssignedTags,
    string Creator,
    string Title);

public sealed record GalleryTagAssignmentResultDto(
    int AddedCount,
    int SkippedCount,
    int RemovedCount);
