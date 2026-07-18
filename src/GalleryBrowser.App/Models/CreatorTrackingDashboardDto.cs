namespace GalleryBrowser.Models;

public sealed record CreatorTrackingDashboardContextDto(
    string Category,
    IReadOnlyList<string> Creators,
    int CreatorCount,
    int FileCount,
    int FileRank,
    int TotalImageCount,
    int TotalImageCountRank,
    int TotalRating,
    int TotalRatingRank);

public sealed record CreatorTrackingArchiveMonthDto(
    string Month,
    int FileCount,
    int ImageCount);

public sealed record CreatorTrackingArchiveSnapshotDto(
    string Date,
    int FileCount,
    int ImageCount);

public sealed record CreatorTrackingDashboardDto(
    string Category,
    int CreatorCount,
    int FileCount,
    int FileRank,
    int TotalImageCount,
    int TotalImageCountRank,
    int TotalRating,
    int TotalRatingRank,
    int TrackingDays,
    int TrackingDaysRank,
    double TotalSpend,
    int TotalSpendRank,
    double RecentThreeMonthSpend,
    int RecentThreeMonthSpendRank,
    string Currency,
    bool HasMissingExchangeRates,
    IReadOnlyList<CreatorTrackingArchiveMonthDto> ArchiveMonths,
    IReadOnlyList<CreatorTrackingArchiveSnapshotDto> ArchiveSnapshots);
