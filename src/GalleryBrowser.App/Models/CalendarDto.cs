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
    bool Reminder)
{
    public string EventType { get; init; } = "subscription";

    public string Severity { get; init; } = "normal";

    public string StartOn { get; init; } = RenewalOn;

    public string EndOn { get; init; } = RenewalOn;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string TaskCategory { get; init; } = string.Empty;

    public string AlertFrequency { get; init; } = "none";
}
