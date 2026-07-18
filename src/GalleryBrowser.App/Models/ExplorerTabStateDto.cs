namespace GalleryBrowser.Models;

public sealed record ExplorerTabStateDto(int Position, string Path, string Label, bool IsActive);
