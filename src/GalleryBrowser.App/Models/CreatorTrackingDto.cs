namespace GalleryBrowser.Models;

public sealed record CreatorTrackingActivityLinkDto(
    string Label,
    string Url,
    string Status,
    string Note,
    bool FollowUpEnabled,
    int FollowUpDays);

public sealed record CreatorTrackingActivityPlaceSettingDto(
    string Label,
    string Placeholder,
    string IconDataUri);

public sealed record CreatorTrackingMetricSettingDto(
    string Key,
    string Label,
    double? WeightPercent);

public sealed record CreatorTrackingTemplateContextDto(
    string DisplayName,
    string MainStoragePath);

public sealed record CreatorTrackingStorageLocationDto(
    string Id,
    string Usage,
    string Path);

public sealed record CreatorTrackingSubscriptionDto(
    string Id,
    string Platform,
    string Plan,
    string Currency,
    double Amount,
    string BillingFrequency,
    string StartedOn,
    string RenewalOn,
    bool EndingPlanned,
    bool Reminder)
{
    public bool Wishlist { get; init; }

    public bool IsActive { get; init; } = true;
}

public sealed record CreatorTrackingPurchaseDto(
    string Id,
    string Platform,
    string ProductName,
    string Currency,
    double Amount,
    string PurchasedOn)
{
    public bool Wishlist { get; init; }
}

public sealed record CreatorTrackingDto(
    string Creator,
    string DisplayName,
    string AlternateName,
    string TrackingStatus,
    string ActivityStatus,
    string FollowUpStatus,
    string LastCheckedOn,
    string LastActivityOn,
    string ActivitySummary,
    IReadOnlyList<CreatorTrackingActivityLinkDto> ActivityLinks,
    string MainStoragePath,
    string WorkStoragePath,
    string UnsortedStoragePath,
    IReadOnlyDictionary<string, int> EvaluationMetrics,
    double PersonalRating,
    string EvaluationMemo,
    IReadOnlyList<CreatorTrackingSubscriptionDto> SubscriptionHistory,
    IReadOnlyList<CreatorTrackingPurchaseDto> PurchaseHistory,
    double MonthlySupportAmount,
    double LifetimeSpend,
    string Currency,
    string SupportStartedOn,
    string SupportEndedOn,
    string SupportMemo,
    string UpdatedAt)
{
    public IReadOnlyList<CreatorTrackingStorageLocationDto> StorageLocations { get; init; } = [];
}
