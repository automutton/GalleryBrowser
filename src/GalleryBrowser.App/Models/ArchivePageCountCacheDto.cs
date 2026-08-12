namespace GalleryBrowser.Models;

public sealed record ArchivePageCountCacheDto(
    string Path,
    long FileLength,
    long ModifiedTicks,
    int? PageCount);
