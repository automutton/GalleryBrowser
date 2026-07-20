namespace GalleryBrowser.Models;

public sealed record UiNavigationStateDto(
    string ActiveView,
    bool ExplorerBookmarksExpanded,
    string ExplorerDetailColumns,
    string MouseGestureSettings,
    string GalleryCardColumns,
    int ExplorerCardColumns,
    double WindowWidth,
    double WindowHeight,
    bool WindowIsMaximized,
    string KeyboardShortcutSettings,
    string GalleryFilterSorts,
    string GalleryThumbnailSorts,
    string CreatorTrackingTabs);
