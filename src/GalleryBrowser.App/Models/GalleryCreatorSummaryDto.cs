namespace GalleryBrowser.Models;

public sealed record GalleryCreatorCompositionSliceDto(
    string Label,
    int Count,
    bool IsCore);

public sealed record GalleryCreatorSummaryDto(
    string Id,
    string Category,
    string Creator,
    string CreatorFolder,
    IReadOnlyList<string> CoreTitles,
    IReadOnlyList<string> CoreTags,
    IReadOnlyList<GalleryCreatorCompositionSliceDto> TitleComposition,
    IReadOnlyList<GalleryCreatorCompositionSliceDto> TagComposition,
    int TotalRating,
    int FileCount,
    int ZipFileCount,
    int TotalImageCount,
    int AverageImageCount,
    int RatedFileCount,
    int MaxRating,
    string LastAccessTime,
    string LastUpdatedTime,
    string TrackingLastActivityOn,
    bool HasCreatorTracking,
    int SinceLastCheckDays,
    bool FollowWarnFlg,
    bool FollowAlertFlg,
    int TrackingDays,
    double TotalSpend,
    string SpendCurrency,
    IReadOnlyList<string> TrackingSites,
    IReadOnlyDictionary<string, int> EvaluationMetrics,
    double PersonalRating,
    string RatingBucket,
    string SearchText)
{
    public int VideoFileCount { get; init; }

    public long TotalDurationSeconds { get; init; }

    public int AverageDurationSeconds { get; init; }
}

public sealed record GalleryCreatorSummarySnapshotDto(
    IReadOnlyList<GalleryCreatorSummaryDto> Items);
