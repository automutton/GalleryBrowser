namespace GalleryBrowser.Models;

public sealed record CreatorTrackingIndexItemDto(
    string Creator,
    string DisplayName,
    string AlternateName,
    IReadOnlyList<string> Categories,
    string LastCheckedOn,
    int SinceLastCheckDays,
    bool FollowWarnFlg,
    bool FollowAlertFlg,
    bool IsNewAfterScheduledScan,
    bool Wishlist);
