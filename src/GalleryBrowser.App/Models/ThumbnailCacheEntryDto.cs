namespace GalleryBrowser.Models;

public sealed record ThumbnailCacheEntryDto(
    string CachePath,
    string SourcePath,
    string SourceStamp);
