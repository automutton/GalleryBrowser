namespace GalleryBrowser.Models;

public sealed record StickyNoteDto(
    long Id,
    string ViewType,
    string ContextKey,
    string ContextLabel,
    string Content,
    double X,
    double Y,
    double Width,
    double Height,
    string ColorKey,
    string ContentMode,
    string CreatedAt,
    string UpdatedAt);

public sealed record StickyNoteBoardItemDto(
    StickyNoteDto Note,
    bool HasLiveNote,
    int BookmarkCount);
