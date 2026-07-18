namespace GalleryBrowser.Models;

public sealed record GalleryItemDto(
    long Id,
    string Title,
    string Kind,
    string Path,
    int Count,
    string[] Tags,
    string Accent,
    string? ThumbnailUri);
