namespace GalleryBrowser.Models;

public sealed record CalendarSettingsDto(
    int WeekStartDay);

public sealed record CalendarSubscriptionEventDto(
    string Id,
    string Creator,
    string DisplayName,
    string Platform,
    string Plan,
    string Currency,
    double Amount,
    string RenewalOn,
    bool EndingPlanned,
    bool Reminder);
