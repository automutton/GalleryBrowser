namespace GalleryBrowser.Models;

public sealed record UserMetricsRankItemDto(
    string Label,
    int FileCount,
    int ImageCount,
    int TotalRating,
    double AverageRating,
    int RecentFileCount,
    double Spend,
    int TrackingDays);

public sealed record UserMetricsRankingSetDto(
    IReadOnlyList<UserMetricsRankItemDto> Creators,
    IReadOnlyList<UserMetricsRankItemDto> Titles,
    IReadOnlyList<UserMetricsRankItemDto> Characters,
    IReadOnlyList<UserMetricsRankItemDto> Tags);

public sealed record UserMetricsTrendPointDto(
    string Period,
    int FileCount,
    int ImageCount,
    double Spend,
    double CumulativeSpend);

public sealed record UserMetricsMetricRankItemDto(
    string Creator,
    double Score);

public sealed record UserMetricsMetricRankingDto(
    string Key,
    IReadOnlyList<UserMetricsMetricRankItemDto> Items);

public sealed record UserMetricsSiteRankItemDto(
    string Label,
    int CreatorCount);

public sealed record UserMetricsBubbleItemDto(
    string Title,
    int TotalRating,
    int TargetTagFileCount,
    int FileCount,
    double AverageRating);

public sealed record UserMetricsInsightDto(
    string Title,
    string Value,
    string Description,
    string Tone);

public sealed record UserMetricsDashboardDto(
    string Category,
    string GeneratedAt,
    int TotalFiles,
    int TotalImages,
    int TotalRating,
    int RatedFiles,
    int CreatorCount,
    int TitleCount,
    int CharacterCount,
    int TagCount,
    UserMetricsRankingSetDto FileRankings,
    UserMetricsRankingSetDto ImageRankings,
    UserMetricsRankingSetDto RatingRankings,
    IReadOnlyList<UserMetricsTrendPointDto> Trends,
    IReadOnlyList<UserMetricsMetricRankingDto> MetricRankings,
    IReadOnlyList<UserMetricsSiteRankItemDto> SiteRankings,
    IReadOnlyList<UserMetricsRankItemDto> SpendRankings,
    IReadOnlyList<UserMetricsRankItemDto> FollowDayRankings,
    IReadOnlyList<UserMetricsBubbleItemDto> TitleBubbles,
    IReadOnlyList<UserMetricsInsightDto> Insights,
    string DisplayCurrency,
    string TargetTag);
