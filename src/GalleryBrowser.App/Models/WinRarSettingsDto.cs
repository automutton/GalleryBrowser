namespace GalleryBrowser.Models;

public sealed record WinRarSettingsDto(
    string ExecutablePath,
    string SupportedExtensions,
    bool ShowOpenInContextMenu);
