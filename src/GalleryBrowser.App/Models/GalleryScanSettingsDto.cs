namespace GalleryBrowser.Models;

public sealed record GalleryScanSettingsDto(
    string Category,
    string SupportedExtensions,
    string CardAspect,
    IReadOnlyList<string> EnabledFilters,
    int FileNameLines,
    string CreatorLabel = "Creator",
    string TitleLabel = "Title",
    string CharacterLabel = "Character",
    string TagLabel = "Tag",
    string CoreTitleLabel = "Core title",
    string CoreTagsLabel = "Core tags");
