using GalleryBrowser.Models;

namespace GalleryBrowser.Services;

internal sealed record ScheduledNotificationSnapshot(
    DateTimeOffset RefreshedAt,
    IReadOnlyList<CreatorTrackingIndexItemDto> Creators,
    IReadOnlyList<CalendarSubscriptionEventDto> Subscriptions)
{
    public static ScheduledNotificationSnapshot Load(
        GalleryDatabase database,
        DateTimeOffset refreshedAt)
    {
        ArgumentNullException.ThrowIfNull(database);

        // These queries intentionally bypass derived dashboard caches. Scheduled
        // notifications must reflect the latest committed Creator Tracking data.
        return new ScheduledNotificationSnapshot(
            refreshedAt,
            database.ListCreatorTrackingIndex(),
            database.ListCalendarSubscriptionEvents());
    }
}
