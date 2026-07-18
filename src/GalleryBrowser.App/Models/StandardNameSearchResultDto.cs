namespace GalleryBrowser.Models;

public sealed record StandardNameSearchResultDto(
    string Name,
    string Source,
    string? Url,
    string? Detail);
