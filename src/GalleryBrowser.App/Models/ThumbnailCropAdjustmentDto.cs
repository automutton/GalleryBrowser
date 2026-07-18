namespace GalleryBrowser.Models;

public sealed record ThumbnailCropAdjustmentDto(
    string Category,
    double HorizontalOffsetPercent,
    double VerticalOffsetPercent,
    double ScalePercent);
