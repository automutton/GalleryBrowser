namespace GalleryBrowser.Models;

public sealed record ExternalAppRuleDto(
    long Id,
    string Name,
    string ExecutablePath,
    string LaunchOptions,
    bool AllowMultiple,
    string ClickExtensions,
    string DoubleClickExtensions,
    string ContextMenuExtensions,
    bool ShowInGalleryContextMenu,
    bool ShowInExplorerContextMenu);
