namespace GalleryBrowser.Models;

public sealed record DatabaseScanScheduleDto(
    string Id,
    int[] Weekdays,
    string Time,
    string[] Categories,
    DateTimeOffset? LastStartedAt);
