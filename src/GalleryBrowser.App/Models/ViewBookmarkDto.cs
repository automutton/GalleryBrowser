namespace GalleryBrowser.Models;

public sealed record ViewBookmarkDto(
    long Id,
    string Name,
    string ViewType,
    string StateJson,
    string ThumbnailDataUrl,
    double WindowWidth,
    double WindowHeight,
    bool WindowIsMaximized,
    int Position,
    string CreatedAt,
    string UpdatedAt);
