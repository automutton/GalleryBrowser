namespace GalleryBrowser.Models;

public sealed record CreatorBlacklistItemDto(
    string Creator,
    string Reason,
    string RegisteredOn);
