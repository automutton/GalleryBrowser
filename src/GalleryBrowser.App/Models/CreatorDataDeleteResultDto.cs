namespace GalleryBrowser.Models;

public sealed record CreatorDataDeleteResultDto(
    string Creator,
    int CreatorTrackingRowsDeleted,
    int WorksDeleted,
    int ItemTagsDeleted,
    int ItemEventsDeleted,
    int ArchiveSnapshotsDeleted,
    int StickyNotesDeleted);
