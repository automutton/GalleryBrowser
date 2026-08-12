namespace GalleryBrowser.Models;

public sealed record GalleryFilterDefinitionDto(
    long Id,
    string FilterType,
    string CanonicalName,
    string CategoryName,
    long? ParentTitleId,
    string ParentTitleName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> VisibleCategories,
    int ItemCount);

public sealed record GalleryFilterCategoryDto(
    long Id,
    string Name,
    int Position,
    int TitleCount,
    int CharacterCount);

public sealed record GalleryFilterCategoryImportRowDto(
    int RowNumber,
    long? FilterCategoryId,
    string CategoryName,
    string NewCategoryName,
    bool Add,
    long? MergeFilterCategoryId);

public sealed record GalleryFilterCategoryExportRowDto(
    long FilterCategoryId,
    string CategoryName);

public sealed record GalleryCharacterCsvRowDto(
    int RowNumber,
    string CategoryName,
    string TitleName,
    string CharacterName);

public sealed record GalleryFilterCombinationImportRowDto(
    int RowNumber,
    long? CombinationId,
    long? FilterCategoryId,
    string CategoryName,
    long? TitleFilterId,
    string TitleName,
    long? CharacterFilterId,
    string CharacterName,
    string NewCategoryName,
    string NewTitleName,
    string NewCharacterName,
    int? UseFlag,
    bool Add,
    long? MergeCombinationId,
    bool IsLegacyRow);

public sealed record GalleryFilterCombinationExportRowDto(
    long CombinationId,
    long? FilterCategoryId,
    string CategoryName,
    long TitleFilterId,
    string TitleName,
    long? CharacterFilterId,
    string CharacterName,
    int UseFlag);

public sealed record GalleryFilterCombinationImportPreviewDto(
    int TotalRows,
    int AddCount,
    int UpdateCount,
    int ExplicitMergeCount,
    int AutomaticTitleMergeCount,
    int AutomaticCharacterMoveCount,
    int DuplicateAddSkipCount,
    IReadOnlyList<GalleryFilterCombinationImportPreviewRowDto> Rows,
    IReadOnlyDictionary<string, IReadOnlyList<GalleryFilterCombinationImportPreviewRowDto>> Cases);

public sealed record GalleryFilterCombinationImportPreviewRowDto(
    int RowNumber,
    string Action,
    string SourceCategory,
    string SourceTitle,
    string SourceCharacter,
    string TargetCategory,
    string TargetTitle,
    string TargetCharacter,
    string Detail);

public sealed record GalleryFilterEditorSnapshotDto(
    IReadOnlyList<GalleryFilterCategoryDto> Categories,
    IReadOnlyList<GalleryFilterDefinitionDto> Titles,
    IReadOnlyList<GalleryFilterDefinitionDto> Characters);

public sealed record GalleryTitleAssignmentOptionDto(
    long Id,
    string Title,
    string CategoryName,
    int WorkCount,
    string SearchText);

public sealed record GalleryTitleAssignmentOptionsDto(
    IReadOnlyList<GalleryTitleAssignmentOptionDto> CreatorTitles,
    IReadOnlyList<GalleryTitleAssignmentOptionDto> AvailableTitles,
    IReadOnlyList<GalleryTitleAssignmentOptionDto> CommonAssignedTitles,
    IReadOnlyList<string> Creators);

public sealed record GalleryTitleAssignmentResultDto(
    int AddedCount,
    int SkippedCount,
    int RemovedCount);

public sealed record GalleryCharacterAssignmentOptionDto(
    long Id,
    string Character,
    string Title,
    int WorkCount,
    string SearchText);

public sealed record GalleryCharacterAssignmentOptionsDto(
    IReadOnlyList<GalleryCharacterAssignmentOptionDto> CreatorTitleCharacters,
    IReadOnlyList<GalleryCharacterAssignmentOptionDto> AvailableCharacters,
    IReadOnlyList<GalleryCharacterAssignmentOptionDto> CommonAssignedCharacters,
    IReadOnlyList<string> Creators,
    IReadOnlyList<string> Titles);

public sealed record GalleryCharacterAssignmentResultDto(
    int AddedCount,
    int SkippedCount,
    int RemovedCount);

public sealed record GalleryCreatorReassignmentResultDto(
    int ItemCount,
    bool TrackingRenamed,
    bool TrackingConflict,
    string Message);

public sealed record GalleryCreatorFolderUpdateResultDto(
    int ItemCount,
    int UpdatedCount);

public sealed record GalleryReverseCharacterFilterDto(
    string Value,
    string Label,
    string Title);

public sealed record GalleryReverseFilterSelectionDto(
    IReadOnlyList<string> Titles,
    IReadOnlyList<GalleryReverseCharacterFilterDto> Characters);
