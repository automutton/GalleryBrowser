namespace GalleryBrowser.Models;

public sealed record UiNavigationStateDto(
    string ActiveView,
    bool ExplorerBookmarksExpanded,
    string ExplorerDetailColumns,
    bool ExplorerDetailOnly,
    string ExplorerSplitState,
    string MouseGestureSettings,
    string GalleryCardColumns,
    int ExplorerCardColumns,
    double WindowWidth,
    double WindowHeight,
    bool WindowIsMaximized,
    string KeyboardShortcutSettings,
    string GalleryFilterSorts,
    string GalleryThumbnailSorts,
    string GalleryRandomPickSettings,
    string CreatorTrackingTabs);
