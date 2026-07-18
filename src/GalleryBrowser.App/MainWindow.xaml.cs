using System.IO;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using GalleryBrowser.Models;
using GalleryBrowser.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace GalleryBrowser;

public partial class MainWindow : Window
{
    private sealed record GalleryThumbnailSource(string Id, string Path, string Category);
    private sealed record ExplorerDatabaseSyncResult(string Message, bool HasWarnings);
    private sealed record FileDeletionCleanupResult(int DeletedDatabaseRecords, int DeletedThumbnailEntries);

    private const string PreferredDropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const int SiteIconDownloadMaximumBytes = 12_000_000;
    private const int SiteIconStoredMaximumBytes = 1_000_000;
    private const int SiteIconMaximumDimension = 96;
    private const int BookmarkThumbnailMaximumWidth = 560;
    private const int BookmarkThumbnailMaximumHeight = 315;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient SiteIconHttpClient = CreateSiteIconHttpClient();
    private static readonly Regex HtmlLinkTagRegex = new("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlAttributeRegex = new("(?<name>[^\\s=/>]+)\\s*=\\s*(?<quote>[\\\"'])(?<value>.*?)\\k<quote>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string _dataDirectory;
    private readonly StorageSettingsStore _storageSettingsStore;
    private readonly GalleryDatabase _database;
    private readonly PCloudBackupService _pCloudBackupService;
    private readonly PCloudStartupRestoreResult? _pCloudStartupRestoreResult;
    private readonly FileBrowserService _fileBrowser;
    private readonly ThumbnailService _thumbnailService;
    private readonly WinRarService _winRarService;
    private readonly FfmpegService _ffmpegService;
    private readonly StandardNameSearchService _standardNameSearchService;
    private readonly GalleryDatabaseUpdateService _galleryDatabaseUpdateService;
    private CancellationTokenSource? _galleryDatabaseUpdateCancellation;
    private IReadOnlyDictionary<long, GalleryItemDto> _itemsById = new Dictionary<long, GalleryItemDto>();
    private readonly Dictionary<string, int> _galleryRatingBaselines = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _galleryRatingBaselineLock = new();
    private IReadOnlyList<string> _explorerClipboardPaths = [];
    private bool _explorerClipboardIsCut;
    private bool _thumbnailCacheConfigurationReady;
    private readonly object _thumbnailCropAdjustmentLock = new();
    private readonly Dictionary<string, ThumbnailCropAdjustmentDto> _thumbnailCropAdjustments = new(StringComparer.OrdinalIgnoreCase);
    private bool _restoreWindowMaximized;
    private string? _pendingFilterCsvPath;
    private string? _pendingTagCsvPath;
    private bool _creatorTrackingCloseFlushRequested;
    private bool _allowCloseAfterCreatorTrackingFlush;
    private bool _gidMigrationInProgress;
    private int _explorerDatabaseManagementInProgress;
    private int _rarToZipBatchInProgress;
    private CancellationTokenSource? _pCloudAutoBackupCancellation;
    private int _pCloudAutoBackupInProgress;
    private bool _pCloudStartupRestoreReported;
    private long _lastUserActivityUtcTicks = DateTime.UtcNow.Ticks;

    public MainWindow()
    {
        _dataDirectory = ApplicationPaths.GetDataDirectory();
        _storageSettingsStore = new StorageSettingsStore(_dataDirectory);
        _pCloudStartupRestoreResult = PCloudBackupService.ApplyPendingRestore(_dataDirectory);
        _database = new GalleryDatabase(_dataDirectory, _storageSettingsStore);
        _pCloudBackupService = new PCloudBackupService(_database, _storageSettingsStore, _dataDirectory);
        _fileBrowser = new FileBrowserService(_database);
        _winRarService = new WinRarService();
        _ffmpegService = new FfmpegService();
        _standardNameSearchService = new StandardNameSearchService();
        _thumbnailService = new ThumbnailService(_database, _ffmpegService);
        _galleryDatabaseUpdateService = new GalleryDatabaseUpdateService(_database, _fileBrowser);
        InitializeComponent();
#if DEBUG
        Title = CreateDebugWindowTitle();
#endif
        RestoreWindowLayout();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

#if DEBUG
    private static string CreateDebugWindowTitle()
    {
        var assembly = typeof(MainWindow).Assembly;
        var buildVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(buildVersion)
            ? "GalleryBrowser [DEBUG]"
            : $"GalleryBrowser [DEBUG {buildVersion}]";
    }
#endif

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_restoreWindowMaximized)
        {
            WindowState = System.Windows.WindowState.Maximized;
        }

        var webView2DataDirectory = Path.Combine(_dataDirectory, "webview2");
        Directory.CreateDirectory(webView2DataDirectory);
        var webView2Environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webView2DataDirectory);
        await Browser.EnsureCoreWebView2Async(webView2Environment);
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        EnsureThumbnailCacheConfiguration();

        var uiEntry = GetUiEntryPath();
        if (File.Exists(uiEntry))
        {
            var uiFolder = Path.GetDirectoryName(uiEntry);
            if (uiFolder is not null)
            {
                Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "gallerybrowser.local",
                    uiFolder,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "gallerybrowser-cache.local",
                _thumbnailService.CacheRoot,
                CoreWebView2HostResourceAccessKind.Allow);

            Browser.CoreWebView2.Navigate($"https://gallerybrowser.local/index.html?v={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }
        else
        {
            Browser.NavigateToString(CreateMissingUiHtml(uiEntry));
        }
    }

    private static string GetUiEntryPath()
    {
        var bundledIndex = Path.Combine(AppContext.BaseDirectory, "webui", "index.html");
        if (File.Exists(bundledIndex))
        {
            return bundledIndex;
        }

        var repoRoot = ApplicationPaths.TryGetRepositoryRoot();
        if (repoRoot is null)
        {
            return bundledIndex;
        }
        var distIndex = Path.Combine(repoRoot, "src", "GalleryBrowser.Web", "dist", "index.html");
        var fallbackIndex = Path.Combine(repoRoot, "src", "GalleryBrowser.Web", "fallback", "index.html");

        return File.Exists(distIndex) ? distIndex : fallbackIndex;
    }

    private static string CreateMissingUiHtml(string expectedPath)
    {
        var safePath = System.Net.WebUtility.HtmlEncode(expectedPath);
        return $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>GalleryBrowser</title>
                <style>
                  body {
                    margin: 0;
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                    background: #111318;
                    color: #f4f6fb;
                    font-family: "Segoe UI", sans-serif;
                  }
                  main {
                    max-width: 760px;
                    padding: 28px;
                    border: 1px solid rgba(255,255,255,.12);
                    border-radius: 8px;
                    background: rgba(255,255,255,.07);
                  }
                  code {
                    color: #93c5fd;
                  }
                </style>
              </head>
              <body>
                <main>
                  <h1>GalleryBrowser UI was not found</h1>
                  <p>Expected file:</p>
                  <p><code>{{safePath}}</code></p>
                </main>
              </body>
            </html>
            """;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var document = JsonDocument.Parse(e.WebMessageAsJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("type", out var typeProperty))
        {
            return;
        }

        var type = typeProperty.GetString();
        if (type == "app.userActivity")
        {
            Interlocked.Exchange(ref _lastUserActivityUtcTicks, DateTime.UtcNow.Ticks);
            return;
        }
        if (type == "app.ready")
        {
            PostMessage(new
            {
                type = "app.context",
                appName = "GalleryBrowser",
                version = "1.0"
            });
            if (!_pCloudStartupRestoreReported && _pCloudStartupRestoreResult is not null)
            {
                _pCloudStartupRestoreReported = true;
                PostMessage(new
                {
                    type = _pCloudStartupRestoreResult.Succeeded
                        ? "settings.pcloud.operation.result"
                        : "settings.pcloud.operation.error",
                    action = "restoreCompleted",
                    message = _pCloudStartupRestoreResult.Succeeded && !string.IsNullOrWhiteSpace(_pCloudStartupRestoreResult.BackupPath)
                        ? $"{_pCloudStartupRestoreResult.Message}\n復元前のDB: {_pCloudStartupRestoreResult.BackupPath}"
                        : _pCloudStartupRestoreResult.Message
                });
            }
            StartPCloudAutoBackupScheduler();
        }
        else if (type == "gallery.list.request")
        {
            var items = _database.ListItems();
            _itemsById = items.ToDictionary(item => item.Id);
            PostMessage(new
            {
                type = "gallery.list.result",
                items,
                thumbnailStats = new
                {
                    found = items.Count(item => item.ThumbnailUri is not null),
                    total = items.Count
                }
            });
        }
        else if (type == "gallery.works.list")
        {
            var category = ReadRequiredString(root, "category");
            var ratings = ReadIntArray(root, "ratings");
            var tags = ReadStringArray(root, "tags");
            var creators = ReadStringArray(root, "creators");
            var titles = ReadStringArray(root, "titles");
            var characters = ReadStringArray(root, "characters");
            var filterSort = ReadOptionalString(root, "filterSort") ?? "rating";
            var sort = ReadOptionalString(root, "sort") ?? "rating";
            var offset = root.TryGetProperty("offset", out var offsetProperty) && offsetProperty.TryGetInt32(out var parsedOffset)
                ? parsedOffset
                : 0;
            var pageSize = root.TryGetProperty("pageSize", out var pageSizeProperty) && pageSizeProperty.TryGetInt32(out var parsedPageSize)
                ? parsedPageSize
                : 96;
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var page = await Task.Run(() => _database.ListGalleryWorks(
                    category,
                    ratings,
                    tags,
                    creators,
                    titles,
                    characters,
                    sort,
                    filterSort,
                    offset,
                    pageSize));

                lock (_galleryRatingBaselineLock)
                {
                    foreach (var work in page.Items)
                    {
                        _galleryRatingBaselines.TryAdd(work.Path, work.Rating);
                    }
                }

                EnsureThumbnailCacheConfiguration();
                var thumbnailUris = await Task.Run(() => GetCachedGalleryThumbnailUris(
                    page.Items.Select(work => new GalleryThumbnailSource(work.Id, work.Path, work.Category))));
                PostMessage(new { type = "gallery.works.result", requestId, category, offset, page, thumbnailUris });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.works.error", requestId, category, message = ex.Message });
            }
        }
        else if (type == "gallery.works.filters")
        {
            var category = ReadRequiredString(root, "category");
            var ratings = ReadIntArray(root, "ratings");
            var tags = ReadStringArray(root, "tags");
            var creators = ReadStringArray(root, "creators");
            var titles = ReadStringArray(root, "titles");
            var characters = ReadStringArray(root, "characters");
            var filterParts = ReadStringArray(root, "filterParts");
            var filterSort = ReadOptionalString(root, "filterSort") ?? "rating";
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            var isSnapshot = root.TryGetProperty("isSnapshot", out var snapshotProperty) &&
                             snapshotProperty.ValueKind == JsonValueKind.True;
            try
            {
                var filters = await Task.Run(() => _database.ListGalleryWorkFilters(
                    category,
                    ratings,
                    tags,
                    creators,
                    titles,
                    characters,
                    filterSort,
                    filterParts));
                PostMessage(new { type = "gallery.works.filters.result", requestId, category, filterParts, isSnapshot, filters });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.works.filters.error", requestId, category, message = ex.Message });
            }
        }
        else if (type == "gallery.reverseFilters.get")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var selection = await Task.Run(() => _database.GetGalleryReverseFilterSelection(
                    ReadRequiredString(root, "category"),
                    ReadRequiredString(root, "path")));
                PostMessage(new
                {
                    type = "gallery.reverseFilters.result",
                    requestId,
                    titles = selection.Titles,
                    characters = selection.Characters
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.reverseFilters.error", requestId, message = ex.Message });
            }
        }
        else if (type == "gallery.creatorSummary.list")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            var forceRefresh = root.TryGetProperty("forceRefresh", out var forceRefreshProperty) &&
                               forceRefreshProperty.ValueKind == JsonValueKind.True;
            try
            {
                var snapshot = await Task.Run(() => _database.ListGalleryCreatorSummaries(forceRefresh));
                const int batchSize = 150;
                if (snapshot.Items.Count == 0)
                {
                    PostMessage(new
                    {
                        type = "gallery.creatorSummary.result",
                        requestId,
                        reset = true,
                        isLast = true,
                        items = Array.Empty<GalleryCreatorSummaryDto>()
                    });
                }
                else
                {
                    for (var offset = 0; offset < snapshot.Items.Count; offset += batchSize)
                    {
                        var items = snapshot.Items.Skip(offset).Take(batchSize).ToArray();
                        EnsureThumbnailCacheConfiguration();
                        var thumbnailUris = await Task.Run(() => GetCachedGalleryThumbnailUris(
                            items.Select(item => new GalleryThumbnailSource(item.Id, item.CreatorFolder, item.Category))));
                        PostMessage(new
                        {
                            type = "gallery.creatorSummary.result",
                            requestId,
                            reset = offset == 0,
                            isLast = offset + items.Length >= snapshot.Items.Count,
                            items,
                            thumbnailUris
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.creatorSummary.error", requestId, message = ex.Message });
            }
        }
        else if (type == "user.metrics.get")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            var category = ReadOptionalString(root, "category") ?? _database.GetDefaultGallerySectionId();
            var forceRefresh = root.TryGetProperty("forceRefresh", out var forceRefreshProperty) &&
                               forceRefreshProperty.ValueKind == JsonValueKind.True;
            try
            {
                var dashboard = await Task.Run(() => _database.GetUserMetricsDashboard(category, forceRefresh));
                PostMessage(new { type = "user.metrics.result", requestId, dashboard });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "user.metrics.error", requestId, message = ex.Message });
            }
        }
        else if (type == "creator.tracking.get")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var creator = ReadRequiredString(root, "creator");
                var forceRefresh = root.TryGetProperty("forceRefresh", out var forceRefreshProperty) &&
                                   forceRefreshProperty.ValueKind == JsonValueKind.True;
                var dashboardContext = ReadCreatorTrackingDashboardContext(root, creator);
                var templateContext = ReadCreatorTrackingTemplateContext(root);
                var loadResult = await Task.Run(() => _database.GetOrCreateCreatorTracking(creator, templateContext));
                var scanMessage = string.Empty;
                GalleryCreatorSummaryDto? refreshedSummary = null;
                if (forceRefresh)
                {
                    scanMessage = await RefreshCreatorTrackingGalleryFolderAsync(loadResult.Tracking);
                    if (!string.IsNullOrWhiteSpace(dashboardContext.Category))
                    {
                        var snapshot = await Task.Run(() => _database.ListGalleryCreatorSummaries(forceRefresh: true));
                        dashboardContext = BuildCreatorTrackingDashboardContext(snapshot, dashboardContext, creator);
                        refreshedSummary = FindCreatorTrackingSummary(
                            snapshot,
                            dashboardContext.Category,
                            creator,
                            templateContext?.MainStoragePath);
                    }
                }
                var dashboard = await _database.GetCreatorTrackingDashboardAsync(
                    loadResult.Tracking,
                    dashboardContext,
                    forceRefresh: forceRefresh);
                PostMessage(new
                {
                    type = "creator.tracking.result",
                    requestId,
                    tracking = loadResult.Tracking,
                    dashboard,
                    dashboardContext,
                    summary = refreshedSummary,
                    scanMessage,
                    created = loadResult.Created
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "creator.tracking.error", requestId, message = ex.Message });
            }
        }
        else if (type == "creator.tracking.storage.get")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var tracking = await Task.Run(() => _database.GetCreatorTracking(ReadRequiredString(root, "creator")));
                PostMessage(new
                {
                    type = "creator.tracking.storage.result",
                    requestId,
                    creator = tracking.Creator,
                    storageLocations = tracking.StorageLocations
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "creator.tracking.storage.error", requestId, message = ex.Message });
            }
        }
        else if (type == "creator.tracking.save")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var tracking = root.GetProperty("tracking").Deserialize<CreatorTrackingDto>(JsonOptions)
                    ?? throw new InvalidOperationException("Creator Trackingの保存内容を取得できませんでした。");
                var dashboardContext = ReadCreatorTrackingDashboardContext(root, tracking.Creator);
                var forceRefresh = root.TryGetProperty("forceRefresh", out var forceRefreshProperty) &&
                                   forceRefreshProperty.ValueKind == JsonValueKind.True;
                await Task.Run(() => _database.SaveCreatorTracking(tracking));
                var savedTracking = await Task.Run(() => _database.GetCreatorTracking(tracking.Creator));
                var scanMessage = string.Empty;
                GalleryCreatorSummaryDto? refreshedSummary = null;
                if (forceRefresh)
                {
                    scanMessage = await RefreshCreatorTrackingGalleryFolderAsync(savedTracking);
                    if (!string.IsNullOrWhiteSpace(dashboardContext.Category))
                    {
                        var snapshot = await Task.Run(() => _database.ListGalleryCreatorSummaries(forceRefresh: true));
                        dashboardContext = BuildCreatorTrackingDashboardContext(
                            snapshot,
                            dashboardContext,
                            savedTracking.Creator);
                        refreshedSummary = FindCreatorTrackingSummary(
                            snapshot,
                            dashboardContext.Category,
                            savedTracking.Creator,
                            savedTracking.MainStoragePath);
                    }
                }
                var dashboard = await _database.GetCreatorTrackingDashboardAsync(savedTracking, dashboardContext, forceRefresh: true);
                PostMessage(new
                {
                    type = "creator.tracking.saved",
                    requestId,
                    tracking = savedTracking,
                    dashboard,
                    dashboardContext,
                    summary = refreshedSummary,
                    scanMessage
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "creator.tracking.error", requestId, message = ex.Message });
            }
        }
        else if (type == "creator.tracking.url.open")
        {
            try
            {
                OpenCreatorTrackingUrl(ReadRequiredString(root, "url"));
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "creator.tracking.url.error", message = ex.Message });
            }
        }
        else if (type == "creator.tracking.flush")
        {
            try
            {
                if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("tracking", out var trackingProperty))
                        {
                            continue;
                        }

                        var tracking = trackingProperty.Deserialize<CreatorTrackingDto>(JsonOptions)
                            ?? throw new InvalidOperationException("終了前にCreator Trackingの保存内容を取得できませんでした。");
                        await Task.Run(() => _database.SaveCreatorTracking(tracking));
                    }
                }

                _allowCloseAfterCreatorTrackingFlush = true;
                _ = Dispatcher.BeginInvoke(Close);
            }
            catch (Exception ex)
            {
                _creatorTrackingCloseFlushRequested = false;
                PostMessage(new { type = "creator.tracking.flush.error", message = ex.Message });
            }
        }
        else if (type == "settings.theme.load")
        {
            PostMessage(new { type = "settings.theme.result", settings = _database.GetThemeSettings() });
        }
        else if (type == "settings.theme.save")
        {
            try
            {
                _database.SaveThemeSettings(
                    ReadOptionalString(root, "theme") ?? "dark",
                    ReadOptionalString(root, "mainColor") ?? "#caff19",
                    ReadOptionalString(root, "subColor") ?? "#ffb342");
                PostMessage(new { type = "settings.theme.result", settings = _database.GetThemeSettings() });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.theme.error", message = ex.Message });
            }
        }
        else if (type == "settings.language.load")
        {
            PostMessage(new { type = "settings.language.result", settings = _database.GetLanguageSettings() });
        }
        else if (type == "settings.language.save")
        {
            try
            {
                _database.SaveLanguageSettings(ReadOptionalString(root, "language") ?? "ja");
                PostMessage(new { type = "settings.language.result", settings = _database.GetLanguageSettings(), updated = true });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.language.error", message = ex.Message });
            }
        }
        else if (type == "settings.creatorTracking.list")
        {
            PostMessage(new
            {
                type = "settings.creatorTracking.result",
                activityPlaces = _database.GetCreatorTrackingActivityPlaces(),
                compositionLabelLimit = _database.GetCreatorTrackingCompositionLabelLimit(),
                followPolicyOptions = _database.GetCreatorTrackingFollowPolicyOptions(),
                metrics = _database.GetCreatorTrackingMetricSettings()
            });
        }
        else if (type == "settings.creatorTracking.save")
        {
            try
            {
                var activityPlaces = root.GetProperty("activityPlaces")
                    .Deserialize<CreatorTrackingActivityPlaceSettingDto[]>(JsonOptions) ?? [];
                var compositionLabelLimit = root.TryGetProperty("compositionLabelLimit", out var limitProperty) &&
                                            limitProperty.TryGetInt32(out var parsedLimit)
                    ? parsedLimit
                    : _database.GetCreatorTrackingCompositionLabelLimit();
                var followPolicyOptions = ReadStringArray(root, "followPolicyOptions");
                var metricSettings = root.TryGetProperty("metrics", out var metricsProperty)
                    ? metricsProperty.Deserialize<CreatorTrackingMetricSettingDto[]>(JsonOptions) ?? []
                    : _database.GetCreatorTrackingMetricSettings();
                _database.SaveCreatorTrackingSettings(
                    activityPlaces,
                    compositionLabelLimit,
                    followPolicyOptions,
                    metricSettings);
                PostMessage(new
                {
                    type = "settings.creatorTracking.result",
                    activityPlaces = _database.GetCreatorTrackingActivityPlaces(),
                    compositionLabelLimit = _database.GetCreatorTrackingCompositionLabelLimit(),
                    followPolicyOptions = _database.GetCreatorTrackingFollowPolicyOptions(),
                    metrics = _database.GetCreatorTrackingMetricSettings(),
                    updated = true
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.creatorTracking.error", message = ex.Message });
            }
        }
        else if (type == "settings.creatorTracking.icon.fetch")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var sourceUrl = ReadRequiredString(root, "url");
                var iconDataUri = await FetchSiteIconDataUriAsync(sourceUrl);
                PostMessage(new { type = "settings.creatorTracking.icon.result", requestId, iconDataUri });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.creatorTracking.icon.error", requestId, message = ex.Message });
            }
        }
        else if (type == "gallery.work.thumbnail")
        {
            var path = ReadRequiredString(root, "path");
            var id = ReadOptionalString(root, "id") ?? path;
            var category = ReadOptionalString(root, "category") ?? _database.GetDefaultGallerySectionId();
            var thumbnailUri = await GetGalleryThumbnailUriAsync(path, category);
            PostMessage(new { type = "gallery.work.thumbnail.result", id, thumbnailUri });
        }
        else if (type == "gallery.work.thumbnail.batch")
        {
            var requests = root.TryGetProperty("items", out var itemsProperty) && itemsProperty.ValueKind == JsonValueKind.Array
                ? itemsProperty.EnumerateArray()
                    .Take(32)
                    .Select(item => (
                        Id: ReadOptionalString(item, "id") ?? string.Empty,
                        Path: ReadOptionalString(item, "path") ?? string.Empty,
                        Category: ReadOptionalString(item, "category") ?? _database.GetDefaultGallerySectionId()))
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Path))
                    .ToArray()
                : [];
            await Task.WhenAll(requests.Select(async request =>
            {
                var thumbnailUri = await GetGalleryThumbnailUriAsync(request.Path, request.Category);
                PostMessage(new { type = "gallery.work.thumbnail.result", id = request.Id, thumbnailUri });
            }));
        }
        else if (type == "gallery.tagAssignment.options.request")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var options = await Task.Run(() => _database.ListGalleryTagAssignmentOptions(
                    ReadRequiredString(root, "category"),
                    ReadRequiredString(root, "sourcePath"),
                    ReadStringArray(root, "paths")));
                PostMessage(new
                {
                    type = "gallery.tagAssignment.options.result",
                    requestId,
                    creatorTitleTags = options.CreatorTitleTags,
                    availableTags = options.AvailableTags,
                    commonAssignedTags = options.CommonAssignedTags,
                    creator = options.Creator,
                    title = options.Title
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.tagAssignment.options.error", requestId, message = ex.Message });
            }
        }
        else if (type == "gallery.tagAssignment.apply")
        {
            try
            {
                var result = await Task.Run(() => _database.AssignGalleryWorkTag(
                    ReadStringArray(root, "paths"),
                    ReadRequiredLong(root, "tagId"),
                    ReadLongArray(root, "removeTagIds")));
                PostMessage(new
                {
                    type = "gallery.tagAssignment.applied",
                    addedCount = result.AddedCount,
                    skippedCount = result.SkippedCount,
                    removedCount = result.RemovedCount
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.tagAssignment.error", message = ex.Message });
            }
        }
        else if (type == "gallery.tagAssignment.remove")
        {
            try
            {
                var result = await Task.Run(() => _database.RemoveGalleryWorkTags(
                    ReadStringArray(root, "paths"),
                    ReadLongArray(root, "tagIds")));
                PostMessage(new
                {
                    type = "gallery.tagAssignment.removed",
                    removedCount = result.RemovedCount
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.tagAssignment.error", message = ex.Message });
            }
        }
        else if (type == "gallery.titleAssignment.options.request")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var options = await Task.Run(() => _database.ListGalleryTitleAssignmentOptions(
                    ReadRequiredString(root, "category"),
                    ReadStringArray(root, "creators"),
                    ReadStringArray(root, "paths")));
                PostMessage(new
                {
                    type = "gallery.titleAssignment.options.result",
                    requestId,
                    creatorTitles = options.CreatorTitles,
                    availableTitles = options.AvailableTitles,
                    commonAssignedTitles = options.CommonAssignedTitles,
                    creators = options.Creators
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.titleAssignment.options.error", requestId, message = ex.Message });
            }
        }
        else if (type == "gallery.titleAssignment.apply")
        {
            try
            {
                var result = await Task.Run(() => _database.AssignGalleryWorkTitle(
                    ReadStringArray(root, "paths"),
                    ReadRequiredLong(root, "titleId"),
                    ReadLongArray(root, "removeTitleIds")));
                PostMessage(new
                {
                    type = "gallery.titleAssignment.applied",
                    addedCount = result.AddedCount,
                    skippedCount = result.SkippedCount,
                    removedCount = result.RemovedCount
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.titleAssignment.error", message = ex.Message });
            }
        }
        else if (type == "gallery.titleAssignment.remove")
        {
            try
            {
                var result = await Task.Run(() => _database.RemoveGalleryWorkTitles(
                    ReadStringArray(root, "paths"),
                    ReadLongArray(root, "titleIds")));
                PostMessage(new
                {
                    type = "gallery.titleAssignment.removed",
                    removedCount = result.RemovedCount
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.titleAssignment.error", message = ex.Message });
            }
        }
        else if (type == "gallery.characterAssignment.options.request")
        {
            var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
            try
            {
                var options = await Task.Run(() => _database.ListGalleryCharacterAssignmentOptions(
                    ReadRequiredString(root, "category"),
                    ReadStringArray(root, "creators"),
                    ReadRequiredString(root, "sourcePath"),
                    ReadStringArray(root, "paths")));
                PostMessage(new
                {
                    type = "gallery.characterAssignment.options.result",
                    requestId,
                    creatorTitleCharacters = options.CreatorTitleCharacters,
                    availableCharacters = options.AvailableCharacters,
                    commonAssignedCharacters = options.CommonAssignedCharacters,
                    creators = options.Creators,
                    titles = options.Titles
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.characterAssignment.options.error", requestId, message = ex.Message });
            }
        }
        else if (type == "gallery.characterAssignment.apply")
        {
            try
            {
                var result = await Task.Run(() => _database.AssignGalleryWorkCharacter(
                    ReadStringArray(root, "paths"),
                    ReadRequiredLong(root, "characterId"),
                    ReadLongArray(root, "removeCharacterIds")));
                PostMessage(new
                {
                    type = "gallery.characterAssignment.applied",
                    addedCount = result.AddedCount,
                    skippedCount = result.SkippedCount,
                    removedCount = result.RemovedCount
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.characterAssignment.error", message = ex.Message });
            }
        }
        else if (type == "gallery.characterAssignment.remove")
        {
            try
            {
                var result = await Task.Run(() => _database.RemoveGalleryWorkCharacters(
                    ReadStringArray(root, "paths"),
                    ReadLongArray(root, "characterIds")));
                PostMessage(new
                {
                    type = "gallery.characterAssignment.removed",
                    removedCount = result.RemovedCount
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.characterAssignment.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.list")
        {
            try
            {
                PostGalleryFilterDefinitions();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.category.save")
        {
            try
            {
                long? categoryId = root.TryGetProperty("id", out var categoryIdProperty) &&
                    categoryIdProperty.ValueKind == JsonValueKind.Number &&
                    categoryIdProperty.TryGetInt64(out var parsedCategoryId)
                    ? parsedCategoryId
                    : null;
                var savedCategoryId = await Task.Run(() => _database.SaveGalleryFilterCategory(
                    categoryId,
                    ReadRequiredString(root, "name")));
                PostGalleryFilterDefinitions(selectedCategoryId: savedCategoryId, updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.category.delete")
        {
            try
            {
                var categoryId = root.TryGetProperty("id", out var categoryIdProperty) &&
                    categoryIdProperty.ValueKind == JsonValueKind.Number &&
                    categoryIdProperty.TryGetInt64(out var parsedCategoryId)
                    ? parsedCategoryId
                    : throw new ArgumentException("削除するCategoryを選択してください。", "id");
                await Task.Run(() => _database.DeleteGalleryFilterCategory(categoryId));
                PostGalleryFilterDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.category.reorder")
        {
            try
            {
                await Task.Run(() => _database.SaveGalleryFilterCategoryOrder(ReadLongArray(root, "ids")));
                PostGalleryFilterDefinitions();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.category.list.export")
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Localize("Categoryリストを保存", "Save Category list", "保存Category列表", "儲存Category清單"),
                Filter = CsvTsvSaveDialogFilter(),
                DefaultExt = ".tsv",
                AddExtension = true,
                FileName = $"GalleryBrowser_filterCategoryList_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    await Task.Run(() => ExportGalleryFilterCategoryList(dialog.FileName));
                    PostMessage(new
                    {
                        type = "filters.editor.list.exported",
                        fileName = Path.GetFileName(dialog.FileName)
                    });
                }
                catch (Exception ex)
                {
                    PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
                }
            }
        }
        else if (type == "filters.editor.list.export")
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Localize("アイテムリストを保存", "Save item list", "保存项目列表", "儲存項目清單"),
                Filter = CsvTsvSaveDialogFilter(),
                DefaultExt = ".tsv",
                AddExtension = true,
                FileName = $"GalleryBrowser_firlterItemList_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    await Task.Run(() => ExportGalleryFilterList(dialog.FileName));
                    PostMessage(new
                    {
                        type = "filters.editor.list.exported",
                        fileName = Path.GetFileName(dialog.FileName)
                    });
                }
                catch (Exception ex)
                {
                    PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
                }
            }
        }
        else if (type == "filters.editor.category.csv.pick")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localize("Categoryリストを選択", "Select Category list", "选择Category列表", "選擇Category清單"),
                Filter = CsvTsvOpenDialogFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    _pendingFilterCsvPath = dialog.FileName;
                    var suggestedHeader = LooksLikeGalleryFilterCategoryCsvHeader(_pendingFilterCsvPath);
                    var previewRows = ReadGalleryFilterCategoryCsvRows(_pendingFilterCsvPath, suggestedHeader);
                    PostGalleryFilterCategoryCsvPreview(previewRows, suggestedHeader, suggestedHeader);
                }
                catch (Exception ex)
                {
                    _pendingFilterCsvPath = null;
                    PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
                }
            }
        }
        else if (type == "filters.editor.csv.pick")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localize("アイテムリストを選択", "Select item list", "选择项目列表", "選擇項目清單"),
                Filter = CsvTsvOpenDialogFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    _pendingFilterCsvPath = dialog.FileName;
                    var rawRows = ReadGalleryFilterCsvRows(_pendingFilterCsvPath, skipHeader: false);
                    var suggestedHeader = LooksLikeGalleryFilterCsvHeader(rawRows.FirstOrDefault());
                    var previewRows = suggestedHeader
                        ? ReadGalleryFilterCsvRows(_pendingFilterCsvPath, skipHeader: true)
                        : rawRows;
                    await PostGalleryFilterCsvPreviewAsync(previewRows, suggestedHeader, suggestedHeader);
                }
                catch (Exception ex)
                {
                    _pendingFilterCsvPath = null;
                    PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
                }
            }
        }
        else if (type == "filters.editor.csv.preview")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingFilterCsvPath))
                {
                    throw new InvalidOperationException("先にCSVファイルを選択してください。");
                }
                var hasHeader = root.TryGetProperty("hasHeader", out var hasHeaderProperty) &&
                    hasHeaderProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    hasHeaderProperty.GetBoolean();
                await PostGalleryFilterCsvPreviewAsync(ReadGalleryFilterCsvRows(_pendingFilterCsvPath, hasHeader), hasHeader, hasHeader);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.csv.import")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingFilterCsvPath))
                {
                    throw new InvalidOperationException("先にCSVファイルを選択してください。");
                }
                var hasHeader = root.TryGetProperty("hasHeader", out var hasHeaderProperty) &&
                    hasHeaderProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    hasHeaderProperty.GetBoolean();
                var confirmed = root.TryGetProperty("confirmed", out var confirmedProperty) &&
                    confirmedProperty.ValueKind is JsonValueKind.True &&
                    confirmedProperty.GetBoolean();
                if (!confirmed)
                {
                    throw new InvalidOperationException("取り込み内容を確認してから実行してください。");
                }
                var count = await Task.Run(() => _database.ImportGalleryFilterCombinations(ReadGalleryFilterCsvRows(_pendingFilterCsvPath, hasHeader)));
                PostMessage(new { type = "filters.editor.csv.imported", count });
                PostGalleryFilterDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.category.csv.preview")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingFilterCsvPath))
                {
                    throw new InvalidOperationException("先にCSVファイルを選択してください。");
                }
                var hasHeader = root.TryGetProperty("hasHeader", out var hasHeaderProperty) &&
                    hasHeaderProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    hasHeaderProperty.GetBoolean();
                PostGalleryFilterCategoryCsvPreview(ReadGalleryFilterCategoryCsvRows(_pendingFilterCsvPath, hasHeader), hasHeader, hasHeader);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.category.csv.import")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingFilterCsvPath))
                {
                    throw new InvalidOperationException("先にCSVファイルを選択してください。");
                }
                var hasHeader = root.TryGetProperty("hasHeader", out var hasHeaderProperty) &&
                    hasHeaderProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    hasHeaderProperty.GetBoolean();
                var count = await Task.Run(() => _database.ImportGalleryFilterCategories(ReadGalleryFilterCategoryCsvRows(_pendingFilterCsvPath, hasHeader)));
                PostMessage(new { type = "filters.editor.csv.imported", count, scope = "category" });
                PostGalleryFilterDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.csv.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.standardName.search")
        {
            try
            {
                var query = ReadRequiredString(root, "query");
                var settings = _database.GetSearchEngineSettings();
                if (settings.Provider == "google")
                {
                    OpenStandardNameSearch(query);
                    PostMessage(new { type = "filters.editor.standardName.opened" });
                }
                else
                {
                    var results = await _standardNameSearchService.SearchAsync(settings, query);
                    PostMessage(new
                    {
                        type = "filters.editor.standardName.results",
                        provider = settings.Provider,
                        results
                    });
                }
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.save")
        {
            try
            {
                long? filterId = root.TryGetProperty("id", out var filterIdProperty) &&
                    filterIdProperty.ValueKind == JsonValueKind.Number &&
                    filterIdProperty.TryGetInt64(out var parsedFilterId)
                    ? parsedFilterId
                    : null;
                long? parentTitleId = root.TryGetProperty("parentTitleId", out var parentTitleProperty) &&
                    parentTitleProperty.ValueKind == JsonValueKind.Number &&
                    parentTitleProperty.TryGetInt64(out var parsedParentId)
                    ? parsedParentId
                    : null;
                var savedFilterId = await Task.Run(() => _database.SaveGalleryFilterDefinition(
                    filterId,
                    ReadRequiredString(root, "filterType"),
                    ReadRequiredString(root, "canonicalName"),
                    ReadOptionalString(root, "categoryName") ?? string.Empty,
                    parentTitleId,
                    ReadStringArray(root, "aliases"),
                    ReadStringArray(root, "visibleCategories")));
                PostGalleryFilterDefinitions(savedFilterId, updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.delete")
        {
            try
            {
                var filterId = root.TryGetProperty("id", out var filterIdProperty) &&
                    filterIdProperty.ValueKind == JsonValueKind.Number &&
                    filterIdProperty.TryGetInt64(out var parsedFilterId)
                    ? parsedFilterId
                    : throw new ArgumentException("削除するフィルタを選択してください。", "id");
                await Task.Run(() => _database.DeleteGalleryFilterDefinition(
                    filterId,
                    ReadRequiredString(root, "filterType")));
                PostGalleryFilterDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.merge")
        {
            try
            {
                var targetFilterId = root.TryGetProperty("targetFilterId", out var targetFilterIdProperty) &&
                    targetFilterIdProperty.ValueKind == JsonValueKind.Number &&
                    targetFilterIdProperty.TryGetInt64(out var parsedTargetId)
                    ? parsedTargetId
                    : throw new ArgumentException("統合先のフィルタを選択してください。", "targetFilterId");
                await Task.Run(() => _database.MergeGalleryFilters(
                    ReadRequiredString(root, "filterType"),
                    targetFilterId,
                    ReadLongArray(root, "sourceFilterIds")));
                PostGalleryFilterDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.visibility.batch")
        {
            try
            {
                var category = ReadRequiredString(root, "category");
                var isVisible = root.TryGetProperty("isVisible", out var visibleProperty) &&
                    visibleProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    visibleProperty.GetBoolean();
                await Task.Run(() => _database.SetGalleryFilterVisibility(
                    ReadLongArray(root, "filterIds"),
                    category,
                    isVisible));
                PostGalleryFilterDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.error", message = ex.Message });
            }
        }
        else if (type == "filters.editor.allocation.list")
        {
            try
            {
                var snapshot = await Task.Run(() => _database.ListGalleryFilterDefinitions());
                PostMessage(new
                {
                    type = "filters.editor.allocation.result",
                    categories = snapshot.Categories,
                    definitions = snapshot.Titles.Concat(snapshot.Characters).ToArray()
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "filters.editor.allocation.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.list")
        {
            try
            {
                PostGalleryTagDefinitions();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.save")
        {
            try
            {
                long? tagId = root.TryGetProperty("id", out var tagIdProperty) &&
                    tagIdProperty.ValueKind == JsonValueKind.Number &&
                    tagIdProperty.TryGetInt64(out var parsedTagId)
                    ? parsedTagId
                    : null;
                var savedTagId = await Task.Run(() => _database.SaveGalleryTagDefinition(
                    tagId,
                    ReadRequiredString(root, "tag"),
                    1));
                PostGalleryTagDefinitions(savedTagId, updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.reorder")
        {
            try
            {
                await Task.Run(() => _database.SaveGalleryTagOrder(ReadLongArray(root, "ids")));
                PostGalleryTagDefinitions();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.disable")
        {
            try
            {
                await Task.Run(() => _database.DisableGalleryTag(ReadRequiredLong(root, "id")));
                PostGalleryTagDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.mapping.batch")
        {
            try
            {
                var category = ReadRequiredString(root, "category");
                var isEnabled = root.TryGetProperty("isEnabled", out var enabledProperty) &&
                    enabledProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    enabledProperty.GetBoolean();
                await Task.Run(() => _database.SetGalleryTagCategoryMapping(
                    ReadLongArray(root, "tagIds"),
                    category,
                    isEnabled));
                PostGalleryTagDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.list.export")
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Localize("Tagリストを保存", "Save Tag list", "保存Tag列表", "儲存Tag清單"),
                Filter = CsvTsvSaveDialogFilter(),
                DefaultExt = ".tsv",
                AddExtension = true,
                FileName = $"GalleryBrowser_tagList_{DateTime.Now:yyyyMMdd_HHmmss}"
            };
            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    await Task.Run(() => ExportGalleryTagList(dialog.FileName));
                    PostMessage(new { type = "tags.manager.list.exported", fileName = Path.GetFileName(dialog.FileName) });
                }
                catch (Exception ex)
                {
                    PostMessage(new { type = "tags.manager.csv.error", message = ex.Message });
                }
            }
        }
        else if (type == "tags.manager.csv.pick")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localize("Tagリストを選択", "Select Tag list", "选择Tag列表", "選擇Tag清單"),
                Filter = CsvTsvOpenDialogFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    _pendingTagCsvPath = dialog.FileName;
                    var suggestedHeader = LooksLikeGalleryTagCsvHeader(_pendingTagCsvPath);
                    PostGalleryTagCsvPreview(ReadGalleryTagCsvRows(_pendingTagCsvPath, suggestedHeader), suggestedHeader, suggestedHeader);
                }
                catch (Exception ex)
                {
                    _pendingTagCsvPath = null;
                    PostMessage(new { type = "tags.manager.csv.error", message = ex.Message });
                }
            }
        }
        else if (type == "tags.manager.csv.preview")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingTagCsvPath))
                {
                    throw new InvalidOperationException("先にTagリストを選択してください。");
                }
                var hasHeader = root.TryGetProperty("hasHeader", out var hasHeaderProperty) &&
                    hasHeaderProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    hasHeaderProperty.GetBoolean();
                PostGalleryTagCsvPreview(ReadGalleryTagCsvRows(_pendingTagCsvPath, hasHeader), hasHeader, hasHeader);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.csv.error", message = ex.Message });
            }
        }
        else if (type == "tags.manager.csv.import")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_pendingTagCsvPath))
                {
                    throw new InvalidOperationException("先にTagリストを選択してください。");
                }
                var hasHeader = root.TryGetProperty("hasHeader", out var hasHeaderProperty) &&
                    hasHeaderProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    hasHeaderProperty.GetBoolean();
                var count = await Task.Run(() => _database.ImportGalleryTags(ReadGalleryTagCsvRows(_pendingTagCsvPath, hasHeader)));
                PostMessage(new { type = "tags.manager.csv.imported", count });
                PostGalleryTagDefinitions(updated: true);
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "tags.manager.csv.error", message = ex.Message });
            }
        }
        else if (type == "gallery.work.open")
        {
            var path = ReadRequiredString(root, "path");
            var activation = string.Equals(ReadOptionalString(root, "activation"), "double", StringComparison.OrdinalIgnoreCase)
                ? ExternalAppActivation.DoubleClick
                : ExternalAppActivation.SingleClick;
            var rule = _database.FindExternalAppRule(path, activation);
            if (rule is not null)
            {
                OpenExternal(path, rule);
            }
        }
        else if (type == "gallery.works.delete")
        {
            var paths = ReadStringArray(root, "paths");
            try
            {
                var deletion = await DeleteFilesAndRelatedDataAsync(paths);
                PostMessage(new
                {
                    type = "gallery.works.delete.result",
                    paths,
                    deletedRecords = deletion.DeletedDatabaseRecords,
                    deletedThumbnailEntries = deletion.DeletedThumbnailEntries
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                PostMessage(new { type = "gallery.works.delete.error", message = ex.Message });
            }
        }
        else if (type == "gallery.work.rating.adjust")
        {
            var path = ReadRequiredString(root, "path");
            var id = ReadOptionalString(root, "id") ?? path;
            var delta = root.TryGetProperty("delta", out var deltaProperty) &&
                deltaProperty.ValueKind == JsonValueKind.Number &&
                deltaProperty.TryGetInt32(out var parsedDelta)
                ? parsedDelta
                : 0;
            try
            {
                int? baseline;
                lock (_galleryRatingBaselineLock)
                {
                    baseline = _galleryRatingBaselines.TryGetValue(path, out var capturedBaseline)
                        ? capturedBaseline
                        : null;
                }

                var result = await Task.Run(() => _database.AdjustGalleryWorkRating(path, delta, baseline));
                lock (_galleryRatingBaselineLock)
                {
                    _galleryRatingBaselines.TryAdd(path, result.Baseline);
                }

                PostMessage(new { type = "gallery.work.rating.result", id, rating = result.Rating, baseline = result.Baseline });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "gallery.work.rating.error", id, message = ex.Message });
            }
        }
        else if (type == "thumbnail.request" &&
                 root.TryGetProperty("id", out var idProperty) &&
                 idProperty.TryGetInt64(out var id) &&
                 _itemsById.TryGetValue(id, out var item))
        {
            EnsureThumbnailCacheConfiguration();
            var thumbnailUri = await _thumbnailService.GetOrCreateThumbnailUriAsync(item);
            PostMessage(new
            {
                type = "thumbnail.result",
                id,
                thumbnailUri
            });
        }
        else if (type == "item.open" &&
                 root.TryGetProperty("id", out var openIdProperty) &&
                 openIdProperty.TryGetInt64(out var openId) &&
                 _itemsById.TryGetValue(openId, out var openItem))
        {
            var rule = _database.FindExternalAppRule(openItem.Path, ExternalAppActivation.SingleClick);
            if (rule is not null)
            {
                OpenExternal(openItem.Path, rule);
            }
        }
        else if (type == "settings.externalApps.list")
        {
            PostExternalAppRules();
        }
        else if (type == "settings.gid.list")
        {
            PostGidSettings();
        }
        else if (type == "settings.gid.save")
        {
            try
            {
                _database.SaveGidSettings(
                    ReadRequiredString(root, "targetExtensions"),
                    _database.GetGidSettings().DigitCount);
                PostGidSettings(updated: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.gid.error", message = ex.Message });
            }
        }
        else if (type == "settings.gid.migration.preview")
        {
            try
            {
                var targetDigitCount = root.TryGetProperty("targetDigitCount", out var digitCountProperty) &&
                    digitCountProperty.TryGetInt32(out var digitCount)
                        ? digitCount
                        : _database.GetGidSettings().DigitCount;
                var preview = await Task.Run(() => _database.PreviewGidMigration(targetDigitCount));
                PostMessage(new { type = "settings.gid.migration.preview.result", preview });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.gid.migration.error", message = ex.Message });
            }
        }
        else if (type == "settings.gid.migration.execute")
        {
            if (_gidMigrationInProgress)
            {
                PostMessage(new { type = "settings.gid.migration.error", message = "GID移行は既に実行中です。" });
                return;
            }

            _gidMigrationInProgress = true;
            try
            {
                var targetDigitCount = root.TryGetProperty("targetDigitCount", out var digitCountProperty) &&
                    digitCountProperty.TryGetInt32(out var digitCount)
                        ? digitCount
                        : _database.GetGidSettings().DigitCount;
                var result = await Task.Run(() => _database.MigrateGids(targetDigitCount, progress =>
                    Dispatcher.BeginInvoke(() => PostMessage(new
                    {
                        type = "settings.gid.migration.progress",
                        progress.Phase,
                        progress.Completed,
                        progress.Total,
                        progress.Message
                    }))));
                _thumbnailService.InvalidateCacheTracking();
                PostGidSettings();
                PostMessage(new { type = "settings.gid.migration.result", result });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.gid.migration.error", message = ex.Message });
            }
            finally
            {
                _gidMigrationInProgress = false;
            }
        }
        else if (type == "settings.winrar.list")
        {
            PostWinRarSettings();
        }
        else if (type == "settings.winrar.save")
        {
            var executablePath = ReadOptionalString(root, "executablePath") ?? string.Empty;
            var supportedExtensions = ReadOptionalString(root, "supportedExtensions") ?? string.Empty;
            var showOpenInContextMenu = root.TryGetProperty("showOpenInContextMenu", out var showOpenProperty) &&
                showOpenProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                showOpenProperty.GetBoolean();
            _database.SaveWinRarSettings(executablePath, supportedExtensions, showOpenInContextMenu);
            PostWinRarSettings();
        }
        else if (type == "settings.winrar.pickExecutable")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localize("WinRAR.exe を選択", "Select WinRAR.exe", "选择WinRAR.exe", "選擇WinRAR.exe"),
                Filter = ExecutableDialogFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                PostMessage(new { type = "settings.winrar.pickExecutable.result", path = dialog.FileName });
            }
        }
        else if (type == "settings.ffmpeg.list")
        {
            PostFfmpegSettings();
        }
        else if (type == "settings.ffmpeg.save")
        {
            var executablePath = ReadOptionalString(root, "executablePath") ?? string.Empty;
            var supportedExtensions = ReadOptionalString(root, "supportedExtensions") ?? string.Empty;
            _database.SaveFfmpegSettings(executablePath, supportedExtensions);
            PostFfmpegSettings();
        }
        else if (type == "settings.ffmpeg.pickExecutable")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localize("ffmpeg.exe を選択", "Select ffmpeg.exe", "选择ffmpeg.exe", "選擇ffmpeg.exe"),
                Filter = ExecutableDialogFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                PostMessage(new { type = "settings.ffmpeg.pickExecutable.result", path = dialog.FileName });
            }
        }
        else if (type == "settings.thumbnailCache.list")
        {
            PostThumbnailCacheSettings();
        }
        else if (type == "settings.searchEngine.list")
        {
            PostSearchEngineSettings();
        }
        else if (type == "settings.searchEngine.save")
        {
            try
            {
                _database.SaveSearchEngineSettings(
                    ReadOptionalString(root, "provider") ?? "google",
                    ReadOptionalString(root, "googleSearchUrlTemplate") ?? string.Empty,
                    ReadOptionalString(root, "braveApiKey") ?? string.Empty,
                    ReadOptionalString(root, "geminiApiKey") ?? string.Empty);
                PostSearchEngineSettings();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.searchEngine.error", message = ex.Message });
            }
        }
        else if (type == "settings.sqliteDatabase.list")
        {
            PostGalleryDatabaseSettings();
            PostPCloudBackupSettings();
        }
        else if (type == "settings.sqliteDatabase.merge.pick")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "結合するGallery SQLiteDBを選択",
                Filter = "SQLite database (*.sqlite;*.db)|*.sqlite;*.db|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) == true)
            {
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.merge.picked",
                    path = dialog.FileName
                });
            }
        }
        else if (type == "settings.sqliteDatabase.merge")
        {
            try
            {
                var selectedDatabasePath = ReadRequiredString(root, "path");
                var selectedIsCanonical = root.TryGetProperty("canonical", out var canonicalProperty) &&
                                          string.Equals(canonicalProperty.GetString(), "selected", StringComparison.OrdinalIgnoreCase);
                var result = await Task.Run(() =>
                    _database.MergeGalleryDatabase(selectedDatabasePath, selectedIsCanonical));
                PostGalleryDatabaseSettings();
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.operation.result",
                    action = "merge",
                    message = $"SQLiteDBを結合しました。正: {result.DatabasePath}\n" +
                              $"追加: 作品 {result.ImportedItems:N0}件 / Creator Tracking {result.ImportedCreatorTrackingEntries:N0}件 / " +
                              $"ブックマーク {result.ImportedBookmarks:N0}件 / 付箋 {result.ImportedStickyNotes:N0}件\n" +
                              $"バックアップ: {Path.GetDirectoryName(result.CurrentDatabaseBackupPath)}"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
            {
                PostMessage(new { type = "settings.sqliteDatabase.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.sqliteDatabase.cache.move")
        {
            try
            {
                var targetDirectory = ReadRequiredString(root, "directory");
                var result = await Task.Run(() => _database.ConfigureCacheDatabaseLocation(targetDirectory));
                PostGalleryDatabaseSettings();
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.operation.result",
                    action = "cacheMove",
                    message = result.RestartRequired
                        ? $"キャッシュDBを新しい保存先へコピーしました。次回起動時から使用します。\n{result.DatabasePath}"
                        : $"キャッシュDBの保存先を確認しました。\n{result.DatabasePath}"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.sqliteDatabase.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.sqliteDatabase.move")
        {
            try
            {
                var targetDirectory = ReadRequiredString(root, "directory");
                var result = await Task.Run(() => _database.MoveGalleryDatabase(targetDirectory));
                PostGalleryDatabaseSettings();
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.operation.result",
                    action = "move",
                    message = $"SQLiteDBを新しい保存先へ移動しました。\n{result.DatabasePath}"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.sqliteDatabase.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.sqliteDatabase.maintain")
        {
            try
            {
                await Task.Run(_database.MaintainGalleryDatabase);
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.operation.result",
                    action = "maintain",
                    message = "SQLiteDBのメンテナンスを完了しました。VACUUM と ANALYZE を実行しました。"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.sqliteDatabase.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.sqliteDatabase.update")
        {
            if (_galleryDatabaseUpdateCancellation is not null)
            {
                PostMessage(new { type = "settings.sqliteDatabase.operation.error", message = "SQLiteDBの更新は既に実行中です。" });
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _galleryDatabaseUpdateCancellation = cancellation;
            try
            {
                var categories = ReadStringArray(root, "categories");
                var result = await _galleryDatabaseUpdateService.UpdateAsync(categories, progress =>
                {
                    Dispatcher.BeginInvoke(() =>
                        PostMessage(new { type = "settings.sqliteDatabase.update.progress", message = progress }));
                }, cancellation.Token);
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.operation.result",
                    action = "update",
                    message = $"SQLiteDBを更新しました。{result.ScanSummary} / {result.ErrorSummary}"
                });
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.operation.cancelled",
                    message = "SQLiteDBの更新を中断しました。次回は同じ区分で更新を再実行してください。"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                PostMessage(new { type = "settings.sqliteDatabase.operation.error", message = ex.Message });
            }
            finally
            {
                _galleryDatabaseUpdateCancellation = null;
            }
        }
        else if (type == "settings.sqliteDatabase.update.cancel")
        {
            if (_galleryDatabaseUpdateCancellation is not null)
            {
                _galleryDatabaseUpdateCancellation.Cancel();
                PostMessage(new
                {
                    type = "settings.sqliteDatabase.update.progress",
                    message = "中断を要求しました。現在のファイル処理を停止しています..."
                });
            }
        }
        else if (type == "settings.pcloud.list")
        {
            PostPCloudBackupSettings();
        }
        else if (type == "settings.pcloud.save")
        {
            try
            {
                SavePCloudSettingsFromMessage(root);
                RestartPCloudAutoBackupScheduler();
                PostPCloudBackupSettings();
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "save",
                    message = "pCloudバックアップ設定を保存しました。"
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.pcloud.connect")
        {
            try
            {
                var apiHost = ReadOptionalString(root, "apiHost") ?? "eapi.pcloud.com";
                var targetFolder = ReadOptionalString(root, "targetFolder") ?? string.Empty;
                var clientId = ReadOptionalString(root, "clientId") ?? string.Empty;
                var result = await _pCloudBackupService.ConnectWithOAuthAsync(apiHost, targetFolder, clientId, CancellationToken.None);
                PostPCloudBackupSettings();
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "connect",
                    message = $"pCloudと連携しました: {result.AccountEmail}"
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.pcloud.test")
        {
            try
            {
                SavePCloudSettingsFromMessage(root);
                var result = await _pCloudBackupService.TestConnectionAsync(CancellationToken.None);
                PostPCloudBackupSettings();
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "test",
                    message = $"pCloud接続を確認しました: {result.AccountEmail}"
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.pcloud.backup")
        {
            try
            {
                SavePCloudSettingsFromMessage(root);
                PostMessage(new
                {
                    type = "settings.pcloud.operation.progress",
                    message = "SQLiteDBの整合性を保ったスナップショットを作成しています..."
                });
                var result = await _pCloudBackupService.BackupAsync(CancellationToken.None);
                PostPCloudBackupSettings();
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "backup",
                    message = BuildPCloudBackupCompletionMessage(result, "pCloudへバックアップしました。")
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.pcloud.snapshots.list")
        {
            try
            {
                PostMessage(new
                {
                    type = "settings.pcloud.operation.progress",
                    message = "pCloudのスナップショット一覧を取得しています..."
                });
                var snapshots = await _pCloudBackupService.ListSnapshotsAsync(CancellationToken.None);
                PostMessage(new { type = "settings.pcloud.snapshots.result", snapshots });
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "listSnapshots",
                    message = $"pCloudに保存されているスナップショットは{snapshots.Count:N0}件です。"
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.pcloud.restore")
        {
            try
            {
                var fileId = ReadRequiredLong(root, "fileId");
                PostMessage(new
                {
                    type = "settings.pcloud.operation.progress",
                    message = "選択したスナップショットを取得し、SQLiteDBの整合性を検証しています..."
                });
                var result = await _pCloudBackupService.PrepareRestoreAsync(fileId, CancellationToken.None);
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "restorePrepared",
                    message = $"{result.FileName} の復元準備が完了しました。\n現在のDB: {result.CurrentDatabaseBackupPath}\n安全に切り替えるためアプリを再起動します。"
                });
                await Task.Delay(600);
                RestartApplicationAfterExit();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.pcloud.disconnect")
        {
            try
            {
                _pCloudBackupService.Disconnect();
                RestartPCloudAutoBackupScheduler();
                PostPCloudBackupSettings();
                PostMessage(new
                {
                    type = "settings.pcloud.operation.result",
                    action = "disconnect",
                    message = "pCloud連携を解除しました。"
                });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "settings.pcloud.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.gallerySections.create")
        {
            try
            {
                var section = _database.CreateGallerySection(ReadRequiredString(root, "label"));
                PostGalleryTargets(section.Id, "created");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SqliteException)
            {
                PostMessage(new { type = "settings.gallerySections.error", message = ex.Message });
            }
        }
        else if (type == "settings.gallerySections.rename")
        {
            try
            {
                var sectionId = ReadRequiredString(root, "sectionId");
                _database.RenameGallerySection(sectionId, ReadRequiredString(root, "label"));
                PostGalleryTargets(sectionId, "renamed");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SqliteException)
            {
                PostMessage(new { type = "settings.gallerySections.error", message = ex.Message });
            }
        }
        else if (type == "settings.gallerySections.delete")
        {
            try
            {
                _database.DeleteGallerySection(ReadRequiredString(root, "sectionId"));
                PostGalleryTargets(sectionAction: "deleted");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or SqliteException)
            {
                PostMessage(new { type = "settings.gallerySections.error", message = ex.Message });
            }
        }
        else if (type == "settings.galleryTargets.list")
        {
            PostGalleryTargets();
        }
        else if (type == "settings.galleryTargets.save")
        {
            try
            {
                var category = ReadRequiredString(root, "category");
                _database.SaveGalleryScanTargets(category, ReadStringArray(root, "paths"));
                _database.SaveGalleryScanSettings(
                    category,
                    ReadOptionalString(root, "supportedExtensions") ?? string.Empty,
                    ReadOptionalString(root, "cardAspect") ?? "portrait",
                    ReadStringArray(root, "enabledFilters"),
                    root.TryGetProperty("fileNameLines", out var fileNameLinesProperty) &&
                    fileNameLinesProperty.ValueKind == JsonValueKind.Number &&
                    fileNameLinesProperty.TryGetInt32(out var parsedFileNameLines)
                        ? parsedFileNameLines
                        : 3,
                    ReadOptionalString(root, "creatorLabel") ?? "Creator",
                    ReadOptionalString(root, "titleLabel") ?? "Title",
                    ReadOptionalString(root, "characterLabel") ?? "Character",
                    ReadOptionalString(root, "tagLabel") ?? "Tag",
                    ReadOptionalString(root, "coreTitleLabel") ?? "Core title",
                    ReadOptionalString(root, "coreTagsLabel") ?? "Core tags");
                PostGalleryTargets();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                PostMessage(new { type = "settings.galleryTargets.error", message = ex.Message });
            }
        }
        else if (type == "settings.thumbnailAdjustments.list")
        {
            PostThumbnailAdjustments();
        }
        else if (type == "settings.thumbnailAdjustments.save")
        {
            try
            {
                var category = ReadRequiredString(root, "category");
                var horizontalOffsetPercent = root.TryGetProperty("horizontalOffsetPercent", out var horizontalProperty) &&
                    horizontalProperty.ValueKind == JsonValueKind.Number &&
                    horizontalProperty.TryGetDouble(out var parsedHorizontal)
                    ? parsedHorizontal
                    : 0;
                var verticalOffsetPercent = root.TryGetProperty("verticalOffsetPercent", out var verticalProperty) &&
                    verticalProperty.ValueKind == JsonValueKind.Number &&
                    verticalProperty.TryGetDouble(out var parsedVertical)
                    ? parsedVertical
                    : -10;
                var scalePercent = root.TryGetProperty("scalePercent", out var scaleProperty) &&
                    scaleProperty.ValueKind == JsonValueKind.Number &&
                    scaleProperty.TryGetDouble(out var parsedScale)
                    ? parsedScale
                    : 100;
                _database.SaveThumbnailCropAdjustment(category, horizontalOffsetPercent, verticalOffsetPercent, scalePercent);
                lock (_thumbnailCropAdjustmentLock)
                {
                    _thumbnailCropAdjustments.Remove(category);
                }
                PostThumbnailAdjustments(updated: true);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                PostMessage(new { type = "settings.thumbnailAdjustments.error", message = ex.Message });
            }
        }
        else if (type == "settings.thumbnailCache.saveTargets")
        {
            _database.SaveThumbnailCacheTargets(ReadStringArray(root, "targets"));
            PostThumbnailCacheSettings();
        }
        else if (type == "settings.thumbnailCache.saveRoot")
        {
            try
            {
                EnsureThumbnailCacheConfiguration();
                var requestedRoot = ReadOptionalString(root, "cacheRoot") ?? string.Empty;
                var result = await Task.Run(() => _thumbnailService.MoveCacheRoot(requestedRoot));
                _database.SaveThumbnailCacheRoot(result.CacheRoot);
                PostThumbnailCacheSettings();
                PostMessage(new
                {
                    type = "settings.thumbnailCache.operation.result",
                    action = "moveRoot",
                    message = result.FailedFiles == 0
                        ? $"キャッシュ {result.MovedFiles} 件を新しい保存先へ移動しました。"
                        : $"キャッシュ {result.MovedFiles} 件を移動しました。移動できなかったファイル: {result.FailedFiles} 件。"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                PostMessage(new { type = "settings.thumbnailCache.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.thumbnailCache.maintain")
        {
            try
            {
                EnsureThumbnailCacheConfiguration();
                var result = _thumbnailService.MaintainCache();
                PostMessage(new
                {
                    type = "settings.thumbnailCache.operation.result",
                    action = "maintain",
                    message = $"キャッシュをメンテナンスしました。古いキャッシュ {result.StaleEntriesRemoved} 件、未追跡キャッシュ {result.OrphanFilesRemoved} 件を削除しました。"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.thumbnailCache.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.thumbnailCache.rebuild")
        {
            try
            {
                EnsureThumbnailCacheConfiguration();
                var result = await _thumbnailService.RebuildAsync((completed, total) =>
                {
                    PostMessage(new { type = "settings.thumbnailCache.rebuild.progress", completed, total });
                    return Task.CompletedTask;
                });
                PostMessage(new
                {
                    type = "settings.thumbnailCache.operation.result",
                    action = "rebuild",
                    message = $"キャッシュを再構築しました。{result.DeletedCacheFiles} 件を破棄し、{result.GeneratedThumbnails}/{result.ScannedSources} 件のサムネイルを生成しました。"
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                PostMessage(new { type = "settings.thumbnailCache.operation.error", message = ex.Message });
            }
        }
        else if (type == "settings.newTabCandidates.list")
        {
            PostNewTabCandidates();
        }
        else if (type == "settings.newTabCandidates.save")
        {
            try
            {
                _database.SaveNewTabCandidate(
                    ReadRequiredString(root, "path"),
                    ReadOptionalString(root, "label") ?? string.Empty);
                PostNewTabCandidates();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                PostMessage(new { type = "settings.newTabCandidates.error", message = ex.Message });
            }
        }
        else if (type == "settings.newTabCandidates.delete")
        {
            _database.DeleteNewTabCandidate(ReadRequiredString(root, "path"));
            PostNewTabCandidates();
        }
        else if (type == "settings.newTabCandidates.separator.add")
        {
            _database.AddNewTabSeparator();
            PostNewTabCandidates();
        }
        else if (type == "settings.newTabCandidates.reorder")
        {
            _database.SaveNewTabCandidateOrder(ReadStringArray(root, "paths"));
            PostNewTabCandidates();
        }
        else if (type == "ui.navigation.load")
        {
            PostMessage(new
            {
                type = "ui.navigation.result",
                state = _database.GetUiNavigationState()
            });
        }
        else if (type == "ui.navigation.save")
        {
            var activeView = ReadOptionalString(root, "activeView") ?? "library";
            var explorerBookmarksExpanded = root.TryGetProperty("explorerBookmarksExpanded", out var expandedProperty) &&
                expandedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                expandedProperty.GetBoolean();
            var explorerDetailColumns = ReadOptionalString(root, "explorerDetailColumns") ?? string.Empty;
            var mouseGestureSettings = ReadOptionalString(root, "mouseGestureSettings") ?? string.Empty;
            var keyboardShortcutSettings = ReadOptionalString(root, "keyboardShortcutSettings") ?? string.Empty;
            var galleryCardColumns = ReadOptionalString(root, "galleryCardColumns") ?? "{}";
            var galleryFilterSorts = ReadOptionalString(root, "galleryFilterSorts") ?? "[]";
            var galleryThumbnailSorts = ReadOptionalString(root, "galleryThumbnailSorts") ?? "[]";
            var explorerCardColumns = root.TryGetProperty("explorerCardColumns", out var explorerCardColumnsProperty) &&
                explorerCardColumnsProperty.ValueKind == JsonValueKind.Number &&
                explorerCardColumnsProperty.TryGetInt32(out var parsedExplorerCardColumns)
                ? parsedExplorerCardColumns
                : 5;
            _database.SaveUiNavigationState(
                activeView,
                explorerBookmarksExpanded,
                explorerDetailColumns,
                mouseGestureSettings,
                galleryCardColumns,
                explorerCardColumns,
                keyboardShortcutSettings,
                galleryFilterSorts,
                galleryThumbnailSorts);
        }
        else if (type == "view.bookmarks.list")
        {
            PostViewBookmarks();
        }
        else if (type == "stickyNotes.list")
        {
            PostStickyNotes();
        }
        else if (type == "stickyNotes.create")
        {
            try
            {
                var note = _database.CreateStickyNote(
                    ReadRequiredString(root, "viewType"),
                    ReadRequiredString(root, "contextKey"),
                    ReadOptionalString(root, "contextLabel") ?? string.Empty,
                    ReadOptionalString(root, "colorKey") ?? "amber",
                    ReadOptionalString(root, "contentMode") ?? "plain",
                    ReadOptionalDouble(root, "x", 24),
                    ReadOptionalDouble(root, "y", 88),
                    ReadOptionalDouble(root, "width", 250),
                    ReadOptionalDouble(root, "height", 200));
                PostMessage(new { type = "stickyNotes.created", note });
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "stickyNotes.error", message = ex.Message });
            }
        }
        else if (type == "stickyNotes.save")
        {
            try
            {
                var noteId = root.TryGetProperty("id", out var noteIdProperty) && noteIdProperty.TryGetInt64(out var parsedNoteId)
                    ? parsedNoteId
                    : 0;
                _database.SaveStickyNote(new StickyNoteDto(
                    noteId,
                    ReadRequiredString(root, "viewType"),
                    ReadRequiredString(root, "contextKey"),
                    ReadOptionalString(root, "contextLabel") ?? string.Empty,
                    ReadOptionalString(root, "content") ?? string.Empty,
                    ReadOptionalDouble(root, "x", 24),
                    ReadOptionalDouble(root, "y", 88),
                    ReadOptionalDouble(root, "width", 250),
                    ReadOptionalDouble(root, "height", 200),
                    ReadOptionalString(root, "colorKey") ?? "amber",
                    ReadOptionalString(root, "contentMode") ?? "plain",
                    string.Empty,
                    string.Empty));
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "stickyNotes.error", message = ex.Message });
            }
        }
        else if (type == "stickyNotes.restore")
        {
            try
            {
                var notes = root.TryGetProperty("notes", out var notesProperty) && notesProperty.ValueKind == JsonValueKind.Array
                    ? notesProperty.Deserialize<StickyNoteDto[]>(JsonOptions) ?? []
                    : [];
                _database.RestoreStickyNotes(notes);
                PostStickyNotes();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "stickyNotes.error", message = ex.Message });
            }
        }
        else if (type == "stickyNotes.delete")
        {
            try
            {
                if (root.TryGetProperty("id", out var noteIdProperty) && noteIdProperty.TryGetInt64(out var noteId))
                {
                    _database.DeleteStickyNote(noteId);
                }
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "stickyNotes.error", message = ex.Message });
            }
        }
        else if (type == "stickyNotes.board.list")
        {
            PostStickyNoteBoardItems();
        }
        else if (type == "stickyNotes.board.save")
        {
            try
            {
                var note = root.TryGetProperty("note", out var noteProperty) && noteProperty.ValueKind == JsonValueKind.Object
                    ? noteProperty.Deserialize<StickyNoteDto>(JsonOptions)
                    : null;
                if (note is null)
                {
                    throw new InvalidOperationException("更新する付箋を読み取れませんでした。");
                }
                _database.SaveStickyNoteFromBoard(note);
                PostStickyNotes();
                PostStickyNoteBoardItems();
                PostViewBookmarks();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "stickyNotes.board.error", message = ex.Message });
            }
        }
        else if (type == "stickyNotes.board.delete")
        {
            try
            {
                if (!root.TryGetProperty("id", out var noteIdProperty) || !noteIdProperty.TryGetInt64(out var noteId))
                {
                    throw new InvalidOperationException("削除する付箋を特定できませんでした。");
                }
                _database.DeleteStickyNoteFromBoard(noteId);
                PostStickyNotes();
                PostStickyNoteBoardItems();
                PostViewBookmarks();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "stickyNotes.board.error", message = ex.Message });
            }
        }
        else if (type == "view.bookmarks.save")
        {
            try
            {
                long? bookmarkId = root.TryGetProperty("id", out var viewBookmarkIdProperty) &&
                    viewBookmarkIdProperty.ValueKind == JsonValueKind.Number &&
                    viewBookmarkIdProperty.TryGetInt64(out var parsedId)
                    ? parsedId
                    : null;
                var bounds = WindowState == System.Windows.WindowState.Normal
                    ? new Rect(0, 0, Width, Height)
                    : RestoreBounds;
                var thumbnailDataUrl = await CaptureBookmarkThumbnailAsync();
                _database.SaveViewBookmark(
                    bookmarkId,
                    ReadRequiredString(root, "name"),
                    ReadRequiredString(root, "viewType"),
                    ReadRequiredString(root, "stateJson"),
                    thumbnailDataUrl,
                    bounds.Width,
                    bounds.Height,
                    WindowState == System.Windows.WindowState.Maximized);
                PostViewBookmarks();
            }
            catch (Exception ex)
            {
                PostMessage(new { type = "view.bookmarks.error", message = ex.Message });
            }
        }
        else if (type == "view.bookmarks.window.restore")
        {
            var width = root.TryGetProperty("width", out var widthProperty) && widthProperty.TryGetDouble(out var parsedWidth)
                ? parsedWidth
                : 0;
            var height = root.TryGetProperty("height", out var heightProperty) && heightProperty.TryGetDouble(out var parsedHeight)
                ? parsedHeight
                : 0;
            var isMaximized = root.TryGetProperty("isMaximized", out var maximizedProperty) &&
                maximizedProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                maximizedProperty.GetBoolean();
            RestoreBookmarkWindowSize(width, height, isMaximized);
        }
        else if (type == "view.bookmarks.delete")
        {
            if (root.TryGetProperty("id", out var deleteBookmarkIdProperty) && deleteBookmarkIdProperty.TryGetInt64(out var bookmarkId))
            {
                _database.DeleteViewBookmark(bookmarkId);
                PostViewBookmarks();
            }
        }
        else if (type == "view.bookmarks.paths.validate")
        {
            var paths = ReadStringArray(root, "paths");
            PostMessage(new
            {
                type = "view.bookmarks.paths.result",
                requestId = ReadOptionalString(root, "requestId"),
                validPaths = paths.Where(Directory.Exists).ToArray(),
                invalidPaths = paths.Where(path => !Directory.Exists(path)).ToArray()
            });
        }
        else if (type == "settings.externalApps.save")
        {
            long? programRuleId = root.TryGetProperty("id", out var programRuleIdProperty) &&
                programRuleIdProperty.ValueKind == JsonValueKind.Number &&
                programRuleIdProperty.TryGetInt64(out var parsedId)
                ? parsedId
                : null;
            var name = root.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
            var executablePath = root.TryGetProperty("executablePath", out var executablePathProperty)
                ? executablePathProperty.GetString()
                : null;
            var launchOptions = root.TryGetProperty("launchOptions", out var launchOptionsProperty)
                ? launchOptionsProperty.GetString()
                : null;
            var allowMultiple = root.TryGetProperty("allowMultiple", out var allowMultipleProperty) &&
                allowMultipleProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                allowMultipleProperty.GetBoolean();
            var clickExtensions = ReadOptionalString(root, "clickExtensions") ?? string.Empty;
            var doubleClickExtensions = ReadOptionalString(root, "doubleClickExtensions") ?? string.Empty;
            var contextMenuExtensions = ReadOptionalString(root, "contextMenuExtensions") ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(executablePath))
            {
                _database.SaveExternalAppRule(
                    programRuleId,
                    name,
                    executablePath,
                    launchOptions ?? "\"{path}\"",
                    allowMultiple,
                    clickExtensions,
                    doubleClickExtensions,
                    contextMenuExtensions);
                PostExternalAppRules();
            }
        }
        else if (type == "settings.externalApps.delete" &&
                 root.TryGetProperty("id", out var deleteIdProperty) &&
                 deleteIdProperty.TryGetInt64(out var deleteId))
        {
            _database.DeleteExternalAppRule(deleteId);
            PostExternalAppRules();
        }
        else if (type == "settings.externalApps.reorder")
        {
            _database.SaveExternalAppRuleOrder(ReadLongArray(root, "ids"));
            PostExternalAppRules();
        }
        else if (type == "settings.externalApps.pickExecutable")
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Localize("起動プログラムを選択", "Select launch program", "选择启动程序", "選擇啟動程式"),
                Filter = ExecutableDialogFilter()
            };
            if (dialog.ShowDialog(this) == true)
            {
                PostMessage(new { type = "settings.externalApps.pickExecutable.result", path = dialog.FileName });
            }
        }
        else if (type == "explorer.path.copy")
        {
            try
            {
                System.Windows.Clipboard.SetText(ReadRequiredString(root, "path"));
                PostMessage(new
                {
                    type = "explorer.operation.result",
                    message = "フォルダパスをクリップボードに格納しました。"
                });
            }
            catch (Exception ex)
            {
                PostMessage(new
                {
                    type = "explorer.operation.error",
                    message = "クリップボードへ格納できませんでした。" + Environment.NewLine + ex.Message
                });
            }
        }
        else if (type == "explorer.thumbnail")
        {
            EnsureThumbnailCacheConfiguration();
            var path = ReadRequiredString(root, "path");
            var priority = root.TryGetProperty("priority", out var priorityProperty) && priorityProperty.TryGetInt32(out var parsedPriority)
                ? parsedPriority
                : 0;
            var forceRefresh = root.TryGetProperty("forceRefresh", out var forceRefreshProperty) &&
                forceRefreshProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                forceRefreshProperty.GetBoolean();
            if (File.Exists(path) || Directory.Exists(path))
            {
                var thumbnailItem = new GalleryItemDto(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(path),
                    Path.GetFileName(path.TrimEnd('\\', '/')),
                    Directory.Exists(path) ? "folder" : "archive",
                    path,
                    0,
                    [],
                    "#93c5fd",
                    null);
                var thumbnailUri = await _thumbnailService.GetOrCreateThumbnailUriAsync(thumbnailItem, priority, forceRefresh);
                PostMessage(new
                {
                    type = "explorer.thumbnail.result",
                    path,
                    thumbnailUri
                });
            }
        }
        else if (type is not null && type.StartsWith("explorer.", StringComparison.Ordinal))
        {
            await HandleExplorerMessage(type, root);
        }
    }

    private async Task HandleExplorerMessage(string type, JsonElement root)
    {
        try
        {
            if (type == "explorer.paths.validate")
            {
                var requestId = ReadOptionalString(root, "requestId") ?? string.Empty;
                var paths = ReadStringArray(root, "paths")
                    .Select(path => path.Trim())
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                PostMessage(new
                {
                    type = "explorer.paths.validate.result",
                    requestId,
                    validPaths = paths.Where(Directory.Exists).ToArray(),
                    invalidPaths = paths.Where(path => !Directory.Exists(path)).ToArray()
                });
            }
            else if (type.StartsWith("explorer.winrar.", StringComparison.Ordinal))
            {
                _winRarService.Configure(_database.GetWinRarSettings());
            }

            if (type == "explorer.bookmarks.list")
            {
                PostExplorerBookmarks();
            }
            else if (type == "explorer.bookmarks.save")
            {
                var path = ReadRequiredString(root, "path");
                var label = ReadOptionalString(root, "label") ?? string.Empty;
                _database.SaveExplorerBookmark(path, label);
                PostExplorerBookmarks();
            }
            else if (type == "explorer.bookmarks.delete")
            {
                _database.DeleteExplorerBookmark(ReadRequiredString(root, "path"));
                PostExplorerBookmarks();
            }
            else if (type == "explorer.bookmarks.reorder")
            {
                _database.SaveExplorerBookmarkOrder(ReadStringArray(root, "paths"));
                PostExplorerBookmarks();
            }
            else if (type == "explorer.tabs.list")
            {
                PostExplorerTabs();
            }
            else if (type == "explorer.tabs.save")
            {
                _database.SaveExplorerTabs(ReadExplorerTabs(root));
            }
            else if (type == "explorer.list")
            {
                PostExplorerDirectory(
                    ReadOptionalString(root, "path"),
                    pane: ReadOptionalString(root, "pane"));
            }
            else if (type == "explorer.gid.assign")
            {
                if (Interlocked.Exchange(ref _explorerDatabaseManagementInProgress, 1) != 0)
                {
                    throw new InvalidOperationException("別のDB管理機能を実行中です。完了してから再試行してください。");
                }

                var directory = ReadRequiredString(root, "directory");
                var pane = ReadOptionalString(root, "pane");
                try
                {
                    var settings = _database.GetGidSettings();
                    var allowedExtensions = settings.TargetExtensions
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(extension => extension.Trim().TrimStart('.').ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var requestedExtensions = ReadStringArray(root, "extensions")
                        .Append(ReadOptionalString(root, "extension"))
                        .Where(extension => !string.IsNullOrWhiteSpace(extension))
                        .Select(extension => extension!.Trim().TrimStart('.').ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (requestedExtensions.Length == 0 ||
                        requestedExtensions.Any(extension =>
                            !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException("選択した拡張子の一部または全部が現在のgid発番対象に含まれていません。");
                    }

                    var folders = ReadStringArray(root, "folders");
                    var result = await Task.Run(() => _fileBrowser.AssignGids(
                        folders,
                        requestedExtensions,
                        settings.DigitCount));
                    PostExplorerDatabaseProgress("gid発番後のDB登録とサムネイルキャッシュ作成を開始します。");
                    var synchronization = await TrySynchronizeExplorerFoldersAsync(folders, requestedExtensions);
                    var extensionLabel = string.Join(" / ", requestedExtensions);
                    var message = result.AssignedCount > 0
                        ? $"{extensionLabel}: {result.AssignedCount}件のファイルに{settings.DigitCount}桁のgidを付与しました。"
                        : $"{extensionLabel}: gidを付与する対象ファイルはありませんでした。";
                    if (result.SkippedExistingCount > 0)
                    {
                        message += $" gid設定済みの{result.SkippedExistingCount}件はスキップしました。";
                    }
                    if (result.ExistingGidCount > 0)
                    {
                        message += $" うちDB登録済みGIDを{result.ExistingGidCount}件のファイル名へ反映しました。";
                    }
                    message += $" {synchronization.Message}";
                    PostMessage(new
                    {
                        type = "explorer.gid.assign.result",
                        result.TargetFileCount,
                        result.AssignedCount,
                        result.ExistingGidCount,
                        result.SkippedExistingCount,
                        errorCount = result.Errors.Count,
                        message,
                        hasWarnings = result.Errors.Count > 0 || synchronization.HasWarnings
                    });
                    PostExplorerDirectory(directory, pane: pane);
                    if (result.Errors.Count > 0)
                    {
                        PostMessage(new
                        {
                            type = "explorer.operation.error",
                            message = $"{result.Errors.Count} 件にgidを付与できませんでした。\n" + string.Join("\n", result.Errors.Take(10))
                        });
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _explorerDatabaseManagementInProgress, 0);
                }
            }
            else if (type == "explorer.creatorFolder.convert")
            {
                if (Interlocked.Exchange(ref _explorerDatabaseManagementInProgress, 1) != 0)
                {
                    throw new InvalidOperationException("別のDB管理機能を実行中です。完了してから再試行してください。");
                }

                var directory = ReadRequiredString(root, "directory");
                var pane = ReadOptionalString(root, "pane");
                try
                {
                    var folders = ReadStringArray(root, "folders");
                    if (folders.Count == 0)
                    {
                        throw new InvalidOperationException("作者フォルダ化するフォルダを選択してください。");
                    }

                    var convertedFolders = new List<string>();
                    var errors = new List<string>();
                    var renamedCount = 0;
                    var trackingCreatedCount = 0;
                    var trackingExistingCount = 0;
                    foreach (var folder in folders)
                    {
                        try
                        {
                            PostExplorerDatabaseProgress($"作者フォルダ化: {Path.GetFileName(folder)}");
                            var converted = await Task.Run(() => _fileBrowser.ConvertToCreatorFolder(folder));
                            if (converted.Renamed)
                            {
                                EnsureThumbnailCacheConfiguration();
                                _thumbnailService.InvalidateSourcesUnderPath(converted.OldPath);
                                try
                                {
                                    await Task.Run(() => _database.UpdateGalleryPathsAfterDirectoryMove(converted.OldPath, converted.NewPath));
                                }
                                catch
                                {
                                    if (!Directory.Exists(converted.OldPath) && Directory.Exists(converted.NewPath))
                                    {
                                        Directory.Move(converted.NewPath, converted.OldPath);
                                    }
                                    throw;
                                }
                                renamedCount++;
                            }

                            convertedFolders.Add(converted.NewPath);
                            try
                            {
                                var tracking = await Task.Run(() => _database.GetOrCreateCreatorTracking(
                                    converted.Creator,
                                    new CreatorTrackingTemplateContextDto(converted.Creator, converted.NewPath)));
                                if (tracking.Created)
                                {
                                    trackingCreatedCount++;
                                }
                                else
                                {
                                    trackingExistingCount++;
                                }
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
                            {
                                errors.Add($"{converted.NewPath}: Creator Trackingを作成できませんでした: {ex.Message}");
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
                        {
                            errors.Add($"{folder}: {ex.Message}");
                        }
                    }

                    if (convertedFolders.Count == 0)
                    {
                        throw new InvalidOperationException("作者フォルダ化を完了できませんでした。\n" + string.Join("\n", errors.Take(10)));
                    }

                    PostExplorerDatabaseProgress("作者情報をDBへ登録し、サムネイルキャッシュを作成しています。");
                    var synchronization = await TrySynchronizeExplorerFoldersAsync(convertedFolders);
                    var message = $"{convertedFolders.Count}フォルダを作者フォルダとして処理しました（名称変更 {renamedCount}件）。";
                    if (trackingCreatedCount > 0)
                    {
                        message += $" Creator Trackingを{trackingCreatedCount}件作成しました。";
                    }
                    if (trackingExistingCount > 0)
                    {
                        message += $" 登録済みCreator Tracking {trackingExistingCount}件は維持しました。";
                    }
                    message += $" {synchronization.Message}";
                    if (errors.Count > 0)
                    {
                        message += $" {errors.Count}件は処理できませんでした。";
                    }

                    // Refresh the parent directory before reporting completion so
                    // the renamed creator folders are already in their new sort
                    // positions when the completion toast is shown. Keep the
                    // converted folders selected and visible after the refresh.
                    PostExplorerDirectory(
                        directory,
                        pane: pane,
                        focusPaths: convertedFolders);
                    PostMessage(new
                    {
                        type = "explorer.creatorFolder.convert.result",
                        renamedCount,
                        trackingCreatedCount,
                        errorCount = errors.Count,
                        message,
                        hasWarnings = errors.Count > 0 || synchronization.HasWarnings
                    });
                }
                finally
                {
                    Interlocked.Exchange(ref _explorerDatabaseManagementInProgress, 0);
                }
            }
        else if (type == "explorer.open")
            {
                var path = ReadRequiredString(root, "path");
                var activation = string.Equals(ReadOptionalString(root, "activation"), "double", StringComparison.OrdinalIgnoreCase)
                    ? ExternalAppActivation.DoubleClick
                    : ExternalAppActivation.SingleClick;
                if (Directory.Exists(path))
                {
                    PostExplorerDirectory(path);
                }
                else
                {
                    var rule = _database.FindExternalAppRule(path, activation);
                    if (rule is not null)
                    {
                        OpenExternal(path, rule);
                    }
                }
            }
            else if (type == "explorer.externalApp.open" &&
                     root.TryGetProperty("id", out var ruleIdProperty) &&
                     ruleIdProperty.TryGetInt64(out var ruleId))
            {
                var path = ReadRequiredString(root, "path");
                var rule = _database.FindExternalAppRule(ruleId);
                if (rule is not null)
                {
                    OpenExternal(path, rule);
                }
            }
            else if (type == "explorer.thumbnail.delete")
            {
                var folderPath = ReadRequiredString(root, "path");
                var deleted = _thumbnailService.DeleteFolderCover(folderPath);
                PostMessage(new
                {
                    type = "explorer.thumbnail.action.result",
                    message = deleted ? "フォルダのサムネイルを初期化しました。" : "フォルダにカバー画像は設定されていません。",
                    thumbnailPath = folderPath
                });
            }
            else if (type == "explorer.winrar.extractHere")
            {
                var archivePath = ReadRequiredString(root, "path");
                var pane = ReadOptionalString(root, "pane");
                await _winRarService.ExtractHereAsync(archivePath);
                PostExplorerDirectory(
                    Path.GetDirectoryName(archivePath),
                    "WinRAR: ここに解凍しました。",
                    pane);
            }
            else if (type == "explorer.winrar.extractToSubfolder")
            {
                var archivePath = ReadRequiredString(root, "path");
                var pane = ReadOptionalString(root, "pane");
                var destination = await _winRarService.ExtractToSubfolderAsync(archivePath);
                PostExplorerDirectory(
                    Path.GetDirectoryName(archivePath),
                    "WinRAR: 書庫名のフォルダへ解凍しました。",
                    pane);
            }
            else if (type == "explorer.winrar.extractAuto")
            {
                var archivePath = ReadRequiredString(root, "path");
                var pane = ReadOptionalString(root, "pane");
                var result = await _winRarService.ExtractAutomaticallyAsync(archivePath);
                PostExplorerDirectory(
                    Path.GetDirectoryName(archivePath),
                    result.ExtractedHere
                        ? "WinRAR: 単一フォルダのため、ここに解凍しました。"
                        : "WinRAR: 内容に合わせて書庫名のフォルダへ解凍しました。",
                    pane);
            }
            else if (type == "explorer.winrar.open")
            {
                _winRarService.Open(ReadRequiredString(root, "path"));
            }
            else if (type == "explorer.winrar.convertRarToZip")
            {
                var rarPaths = ReadStringArray(root, "paths")
                    .Select(Path.GetFullPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (rarPaths.Length == 0)
                {
                    var rarPath = ReadOptionalString(root, "path");
                    if (!string.IsNullOrWhiteSpace(rarPath))
                    {
                        rarPaths = [Path.GetFullPath(rarPath)];
                    }
                }
                if (rarPaths.Length == 0)
                {
                    throw new InvalidOperationException("変換するRARファイルを選択してください。");
                }
                if (rarPaths.Any(path => !string.Equals(Path.GetExtension(path), ".rar", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("RARファイルだけをZIPへ変換できます。");
                }
                if (Interlocked.CompareExchange(ref _rarToZipBatchInProgress, 1, 0) != 0)
                {
                    throw new InvalidOperationException("別のRARからZIPへの変換を実行中です。完了してから再試行してください。");
                }

                var pane = ReadOptionalString(root, "pane");
                var directory = ReadOptionalString(root, "directory")
                    ?? Path.GetDirectoryName(rarPaths[0]);

                // This task owns its exception boundary. Do not await it here:
                // Explorer and the other views must remain usable while WinRAR
                // processes the selected archives sequentially in the background.
                _ = RunRarToZipBatchAsync(rarPaths, directory, pane);
            }
            else if (type == "explorer.winrar.compressDelete")
            {
                var folderPaths = ReadStringArray(root, "paths")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var pane = ReadOptionalString(root, "pane");
                var parentPath = GetCommonParentDirectory(folderPaths);

                _winRarService.ValidateCompressAndDeleteTargets(folderPaths);
                for (var index = 0; index < folderPaths.Length; index++)
                {
                    PostMessage(new
                    {
                        type = "explorer.winrar.progress",
                        message = $"WinRAR: 圧縮と処理後削除中 ({index + 1}/{folderPaths.Length})"
                    });
                    await _winRarService.CompressFolderAndDeleteAsync(folderPaths[index]);
                }

                PostExplorerDirectory(
                    parentPath,
                    folderPaths.Length == 1
                        ? "WinRAR: ZIP に圧縮して元フォルダを削除しました。"
                        : $"WinRAR: {folderPaths.Length} 個の ZIP を作成して元フォルダを削除しました。",
                    pane);
            }
            else if (type == "explorer.winrar.compressPackageDelete")
            {
                var folderPaths = ReadStringArray(root, "paths")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var pane = ReadOptionalString(root, "pane");
                var parentPath = GetCommonParentDirectory(folderPaths);
                var archiveName = ReadRequiredString(root, "archiveName");
                PostMessage(new
                {
                    type = "explorer.winrar.progress",
                    message = "WinRAR: 1 パッケージに圧縮と処理後削除中"
                });
                var archivePath = await _winRarService.CompressFoldersAsPackageAndDeleteAsync(folderPaths, archiveName);
                PostExplorerDirectory(
                    parentPath,
                    "WinRAR: 1 パッケージに圧縮して元フォルダを削除しました。",
                    pane);
            }
            else if (type == "explorer.thumbnail.setParent")
            {
                var parentPath = _thumbnailService.SetParentFolderCover(ReadRequiredString(root, "path"));
                var navigatePath = Path.GetDirectoryName(parentPath) ?? parentPath;
                PostExplorerDirectory(navigatePath, pane: ReadOptionalString(root, "pane"));
                PostMessage(new
                {
                    type = "explorer.thumbnail.action.result",
                    message = "親フォルダのサムネイルに設定しました。",
                    thumbnailPath = parentPath
                });
            }
            else if (type == "explorer.dragdrop")
            {
                var destination = ReadRequiredString(root, "destination");
                var copy = root.TryGetProperty("copy", out var copyProperty) &&
                    copyProperty.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    copyProperty.GetBoolean();
                var paths = ReadStringArray(root, "paths");
                if (copy)
                {
                    await Task.Run(() => _fileBrowser.Copy(
                        paths,
                        destination,
                        CreateExplorerTransferProgressReporter("コピー")));
                }
                else
                {
                    await Task.Run(() => _fileBrowser.Move(
                        paths,
                        destination,
                        CreateExplorerTransferProgressReporter("移動")));
                }

                PostExplorerDirectory(
                    destination,
                    copy ? "コピーしました。" : "移動しました。",
                    ReadOptionalString(root, "pane"));
            }
            else if (type == "explorer.folder.create")
            {
                var directory = ReadRequiredString(root, "path");
                var createdFolder = _fileBrowser.CreateNewFolder(directory);
                PostExplorerDirectory(
                    directory,
                    "フォルダを作成しました。: " + Path.GetFileName(createdFolder),
                    ReadOptionalString(root, "pane"),
                    [createdFolder]);
            }
            else if (type == "explorer.folder.organize")
            {
                var directory = ReadRequiredString(root, "path");
                var separateFolders = string.Equals(
                    ReadOptionalString(root, "mode"),
                    "separate",
                    StringComparison.OrdinalIgnoreCase);
                var result = _fileBrowser.CreateFoldersAndMoveItems(
                    ReadStringArray(root, "paths"),
                    separateFolders);
                var message = separateFolders
                    ? $"{result.MovedPaths.Count} 件を個別フォルダへ移動しました。"
                    : $"{result.MovedPaths.Count} 件を新規フォルダへ移動しました。";
                if (result.SkippedPaths.Count > 0)
                {
                    message += $" {result.SkippedPaths.Count} 件をスキップしました。";
                }

                PostExplorerDirectory(
                    directory,
                    message,
                    ReadOptionalString(root, "pane"),
                    result.CreatedFolders);
                if (result.Errors.Count > 0)
                {
                    PostMessage(new
                    {
                        type = "explorer.operation.error",
                        message = "一部の項目を移動できませんでした。\n" + string.Join("\n", result.Errors)
                    });
                }
            }
            else if (type is "explorer.copy" or "explorer.cut")
            {
                _explorerClipboardPaths = ReadStringArray(root, "paths");
                _explorerClipboardIsCut = type == "explorer.cut";
                TrySetWindowsFileClipboard(_explorerClipboardPaths, _explorerClipboardIsCut);
                PostMessage(new
                {
                    type = "explorer.operation.result",
                    message = _explorerClipboardPaths.Count == 0
                        ? "選択項目がありません。"
                        : _explorerClipboardIsCut
                            ? $"{_explorerClipboardPaths.Count} 件を移動用に選択しました。"
                            : $"{_explorerClipboardPaths.Count} 件をコピーしました。"
                });
            }
            else if (type == "explorer.paste")
            {
                var destination = ReadRequiredString(root, "path");
                var pane = ReadOptionalString(root, "pane");
                var clipboardPaths = _explorerClipboardPaths;
                var clipboardIsCut = _explorerClipboardIsCut;
                if (TryReadWindowsFileClipboard(out var externalPaths, out var externalIsCut))
                {
                    clipboardPaths = externalPaths;
                    clipboardIsCut = externalIsCut;
                }

                if (clipboardPaths.Count == 0)
                {
                    throw new InvalidOperationException("コピーまたはカットした項目がありません。");
                }

                if (clipboardIsCut)
                {
                    var pastedPaths = await Task.Run(() => _fileBrowser.Move(
                        clipboardPaths,
                        destination,
                        CreateExplorerTransferProgressReporter("移動")));
                    _explorerClipboardPaths = [];
                    _explorerClipboardIsCut = false;
                    PostExplorerDirectory(destination, "貼り付けが完了しました。", pane, pastedPaths);
                }
                else
                {
                    var pastedPaths = await Task.Run(() => _fileBrowser.Copy(
                        clipboardPaths,
                        destination,
                        CreateExplorerTransferProgressReporter("コピー")));
                    PostExplorerDirectory(destination, "貼り付けが完了しました。", pane, pastedPaths);
                }
            }
            else if (type == "explorer.rename")
            {
                _fileBrowser.Rename(
                    ReadRequiredString(root, "path"),
                    ReadRequiredString(root, "newName"));
                PostExplorerDirectory(
                    ReadRequiredString(root, "directory"),
                    "名前を変更しました。",
                    ReadOptionalString(root, "pane"));
            }
            else if (type == "explorer.delete")
            {
                var paths = ReadStringArray(root, "paths");
                var directory = ReadRequiredString(root, "directory");
                var pane = ReadOptionalString(root, "pane");
                try
                {
                    var deletion = await DeleteFilesAndRelatedDataAsync(paths);
                    PostExplorerDirectory(
                        directory,
                        $"削除しました。関連DB {deletion.DeletedDatabaseRecords:N0}件、" +
                        $"サムネイルキャッシュ {deletion.DeletedThumbnailEntries:N0}件を整理しました。",
                        pane);
                }
                catch
                {
                    // A filesystem deletion can complete before a later DB or
                    // cache cleanup error. Always refresh Explorer so the view
                    // reflects the actual disk state before surfacing the error.
                    PostExplorerDirectory(directory, pane: pane);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            PostMessageOnDispatcher(new
            {
                type = "explorer.operation.error",
                operation = type,
                message = ex.Message
            });
        }
    }

    private async Task<FileDeletionCleanupResult> DeleteFilesAndRelatedDataAsync(
        IReadOnlyList<string> paths)
    {
        var targetPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetPaths.Length == 0)
        {
            throw new InvalidOperationException("削除するファイルまたはフォルダを選択してください。");
        }

        await Task.Run(() => _fileBrowser.Delete(targetPaths));

        var cleanupErrors = new List<Exception>();
        var deletedDatabaseRecords = 0;
        try
        {
            deletedDatabaseRecords = await Task.Run(() => _database.DeleteGalleryWorks(targetPaths));
        }
        catch (Exception ex)
        {
            cleanupErrors.Add(ex);
        }

        EnsureThumbnailCacheConfiguration();
        var deletedThumbnailEntries = 0;
        try
        {
            deletedThumbnailEntries = await Task.Run(() => targetPaths.Sum(
                path => _thumbnailService.InvalidateSourcesUnderPath(path)));
        }
        catch (Exception ex)
        {
            cleanupErrors.Add(ex);
        }

        RemoveGalleryRatingBaselinesUnderPaths(targetPaths);
        if (cleanupErrors.Count > 0)
        {
            throw new InvalidOperationException(
                "ファイル本体は削除しましたが、関連するDBまたはキャッシュの整理を完了できませんでした。" +
                Environment.NewLine +
                string.Join(Environment.NewLine, cleanupErrors.Select(error => error.Message).Distinct()),
                new AggregateException(cleanupErrors));
        }

        return new FileDeletionCleanupResult(deletedDatabaseRecords, deletedThumbnailEntries);
    }

    private void RemoveGalleryRatingBaselinesUnderPaths(IReadOnlyList<string> targetPaths)
    {
        var prefixes = targetPaths
            .Select(path => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Select(path => (Path: path, ChildPrefix: path + Path.DirectorySeparatorChar))
            .ToArray();
        lock (_galleryRatingBaselineLock)
        {
            var removedPaths = _galleryRatingBaselines.Keys
                .Where(path => prefixes.Any(target =>
                    string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(target.ChildPrefix, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            foreach (var path in removedPaths)
            {
                _galleryRatingBaselines.Remove(path);
            }
        }
    }

    private async Task RunRarToZipBatchAsync(
        IReadOnlyList<string> rarPaths,
        string? requestedDirectory,
        string? pane)
    {
        var converted = new List<RarToZipConversionResult>();
        var conversionErrors = new List<string>();
        var synchronizationErrors = new List<string>();
        var parentPaths = rarPaths
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var synchronization = new ExplorerDatabaseSyncResult(
            "DB同期の対象はありませんでした。",
            false);

        try
        {
            for (var index = 0; index < rarPaths.Count; index++)
            {
                var rarPath = rarPaths[index];
                var itemPrefix = $"WinRAR: RARからZIPへ変換中 ({index + 1}/{rarPaths.Count})";
                PostExplorerWinRarProgress($"{itemPrefix} {Path.GetFileName(rarPath)}");
                try
                {
                    var result = await Task.Run(() => _winRarService.ConvertRarToZipAsync(
                        rarPath,
                        message => PostExplorerWinRarProgress(
                            $"{itemPrefix} {Path.GetFileName(rarPath)}\n{message}")));
                    converted.Add(result);

                    EnsureThumbnailCacheConfiguration();
                    _thumbnailService.InvalidateSourcesUnderPath(result.RarPath);
                    try
                    {
                        await Task.Run(() => _database.UpdateGalleryPathsAfterDirectoryMove(
                            result.RarPath,
                            result.ZipPath));
                    }
                    catch (Exception ex)
                    {
                        synchronizationErrors.Add(
                            $"{Path.GetFileName(result.ZipPath)}: DB内のパスを更新できませんでした: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    conversionErrors.Add($"{Path.GetFileName(rarPath)}: {ex.Message}");
                }
            }

            if (converted.Count > 0 && parentPaths.Length > 0)
            {
                PostExplorerWinRarProgress("RARからZIPへの変換後、DB登録とサムネイルを同期しています。");
                synchronization = await TrySynchronizeExplorerFoldersAsync(
                    parentPaths,
                    ["rar", "zip"]);
            }

            var totalFiles = converted.Sum(result => result.FileCount);
            var message = converted.Count switch
            {
                0 => $"RAR {rarPaths.Count:N0}件をZIPへ変換できませんでした。",
                _ when conversionErrors.Count == 0 =>
                    $"RAR {converted.Count:N0}件をZIPへ変換しました（合計 {totalFiles:N0}ファイル）。",
                _ =>
                    $"RAR {rarPaths.Count:N0}件中 {converted.Count:N0}件をZIPへ変換しました（合計 {totalFiles:N0}ファイル）。"
            };
            if (converted.Count > 0)
            {
                message += " " + synchronization.Message;
            }
            if (conversionErrors.Count > 0)
            {
                message += Environment.NewLine + "変換できなかった項目:" + Environment.NewLine +
                           string.Join(Environment.NewLine, conversionErrors.Take(10));
            }
            if (synchronizationErrors.Count > 0)
            {
                message += Environment.NewLine + string.Join(Environment.NewLine, synchronizationErrors.Take(10));
            }

            PostMessageOnDispatcher(new
            {
                type = "explorer.winrar.convertRarToZip.result",
                message,
                successCount = converted.Count,
                failureCount = conversionErrors.Count,
                hasWarnings = conversionErrors.Count > 0 ||
                              synchronizationErrors.Count > 0 ||
                              synchronization.HasWarnings,
                directory = requestedDirectory,
                pane,
                focusPaths = converted
                    .Where(result => string.Equals(
                        Path.GetDirectoryName(result.ZipPath),
                        requestedDirectory,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(result => result.ZipPath)
                    .ToArray()
            });
        }
        catch (Exception ex)
        {
            PostMessageOnDispatcher(new
            {
                type = "explorer.winrar.convertRarToZip.result",
                message = "RARからZIPへの変換処理を完了できませんでした。" + Environment.NewLine + ex.Message,
                successCount = converted.Count,
                failureCount = Math.Max(1, rarPaths.Count - converted.Count),
                hasWarnings = true,
                directory = requestedDirectory,
                pane,
                focusPaths = converted.Select(result => result.ZipPath).ToArray()
            });
        }
        finally
        {
            Interlocked.Exchange(ref _rarToZipBatchInProgress, 0);
        }
    }

    private void PostExplorerDirectory(
        string? path,
        string? message = null,
        string? pane = null,
        IReadOnlyList<string>? focusPaths = null)
    {
        var result = _fileBrowser.ListDirectory(path);
        PostMessage(new
        {
            type = "explorer.list.result",
            path = result.Path,
            parentPath = result.ParentPath,
            roots = result.Roots,
            entries = result.Entries,
            isTruncated = result.IsTruncated,
            message,
            pane,
            focusPaths
        });
    }

    private void PostExplorerBookmarks()
    {
        PostMessage(new
        {
            type = "explorer.bookmarks.result",
            bookmarks = _database.ListExplorerBookmarks()
        });
    }

    private void PostViewBookmarks()
    {
        PostMessage(new
        {
            type = "view.bookmarks.result",
            bookmarks = _database.ListViewBookmarks()
        });
    }

    private void PostStickyNotes()
    {
        PostMessage(new
        {
            type = "stickyNotes.result",
            notes = _database.ListStickyNotes()
        });
    }

    private void PostStickyNoteBoardItems()
    {
        PostMessage(new
        {
            type = "stickyNotes.board.result",
            items = _database.ListStickyNoteBoardItems()
        });
    }

    private async Task<string> CaptureBookmarkThumbnailAsync()
    {
        using var previewStream = new MemoryStream();
        await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, previewStream);
        previewStream.Position = 0;
        using var image = await Image.LoadAsync(previewStream);
        image.Mutate(context => context.Resize(new ResizeOptions
        {
            Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
            Size = new SixLabors.ImageSharp.Size(BookmarkThumbnailMaximumWidth, BookmarkThumbnailMaximumHeight),
            Sampler = KnownResamplers.Lanczos3
        }));
        using var thumbnailStream = new MemoryStream();
        await image.SaveAsync(thumbnailStream, new JpegEncoder { Quality = 78 });
        return $"data:image/jpeg;base64,{Convert.ToBase64String(thumbnailStream.ToArray())}";
    }

    private void RestoreBookmarkWindowSize(double width, double height, bool isMaximized)
    {
        if (width >= MinWidth && height >= MinHeight)
        {
            WindowState = System.Windows.WindowState.Normal;
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(width, workArea.Width);
            Height = Math.Min(height, workArea.Height);
        }

        if (isMaximized)
        {
            WindowState = System.Windows.WindowState.Maximized;
        }
    }

    private void PostExplorerTabs()
    {
        PostMessage(new
        {
            type = "explorer.tabs.result",
            tabs = _database.ListExplorerTabs()
        });
    }

    private void PostNewTabCandidates()
    {
        PostMessage(new
        {
            type = "settings.newTabCandidates.result",
            candidates = _database.ListNewTabCandidates()
        });
    }

    private string Localize(string japanese, string english, string simplifiedChinese, string traditionalChinese) =>
        _database.GetLanguageSettings().Language switch
        {
            "en" => english,
            "zh-CN" => simplifiedChinese,
            "zh-TW" => traditionalChinese,
            _ => japanese
        };

    private string CsvTsvSaveDialogFilter() => Localize(
        "TSVファイル (*.tsv)|*.tsv|CSVファイル (*.csv)|*.csv",
        "TSV files (*.tsv)|*.tsv|CSV files (*.csv)|*.csv",
        "TSV文件 (*.tsv)|*.tsv|CSV文件 (*.csv)|*.csv",
        "TSV檔案 (*.tsv)|*.tsv|CSV檔案 (*.csv)|*.csv");

    private string CsvTsvOpenDialogFilter() => Localize(
        "CSV / TSVファイル (*.csv;*.tsv)|*.csv;*.tsv|CSVファイル (*.csv)|*.csv|TSVファイル (*.tsv)|*.tsv|すべてのファイル (*.*)|*.*",
        "CSV / TSV files (*.csv;*.tsv)|*.csv;*.tsv|CSV files (*.csv)|*.csv|TSV files (*.tsv)|*.tsv|All files (*.*)|*.*",
        "CSV / TSV文件 (*.csv;*.tsv)|*.csv;*.tsv|CSV文件 (*.csv)|*.csv|TSV文件 (*.tsv)|*.tsv|所有文件 (*.*)|*.*",
        "CSV / TSV檔案 (*.csv;*.tsv)|*.csv;*.tsv|CSV檔案 (*.csv)|*.csv|TSV檔案 (*.tsv)|*.tsv|所有檔案 (*.*)|*.*");

    private string ExecutableDialogFilter() => Localize(
        "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
        "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
        "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        "執行檔 (*.exe)|*.exe|所有檔案 (*.*)|*.*");

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string ReadRequiredString(JsonElement root, string name) =>
        ReadOptionalString(root, name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} が指定されていません。");

    private static double ReadOptionalDouble(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var property) && property.TryGetDouble(out var value)
            ? value
            : fallback;

    private static bool ReadRequiredBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new InvalidOperationException($"{name} が指定されていません。");

    private static int ReadRequiredInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"{name} が指定されていません。");

    private static CreatorTrackingDashboardContextDto ReadCreatorTrackingDashboardContext(
        JsonElement root,
        string creator)
    {
        if (root.TryGetProperty("dashboardContext", out var property) &&
            property.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var context = property.Deserialize<CreatorTrackingDashboardContextDto>(JsonOptions);
                if (context is not null)
                {
                    return context;
                }
            }
            catch (JsonException)
            {
                // Older web assets may not send the dashboard context yet.
            }
        }

        return new CreatorTrackingDashboardContextDto(string.Empty, [creator], 1, 0, 1, 0, 1, 0, 1);
    }

    private static CreatorTrackingDashboardContextDto BuildCreatorTrackingDashboardContext(
        GalleryCreatorSummarySnapshotDto snapshot,
        CreatorTrackingDashboardContextDto fallback,
        string creator)
    {
        var categoryItems = snapshot.Items
            .Where(item => string.Equals(item.Category, fallback.Category, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Creator.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Creator = group.Key,
                FileCount = group.Sum(item => Math.Max(0, item.FileCount)),
                TotalImageCount = group.Sum(item => Math.Max(0, item.TotalImageCount)),
                TotalRating = group.Sum(item => Math.Max(0, item.TotalRating))
            })
            .ToArray();
        var current = categoryItems.FirstOrDefault(item =>
            string.Equals(item.Creator, creator.Trim(), StringComparison.OrdinalIgnoreCase));
        if (current is null)
        {
            return fallback;
        }

        return new CreatorTrackingDashboardContextDto(
            fallback.Category,
            categoryItems.Select(item => item.Creator).ToArray(),
            categoryItems.Length,
            current.FileCount,
            1 + categoryItems.Count(item => item.FileCount > current.FileCount),
            current.TotalImageCount,
            1 + categoryItems.Count(item => item.TotalImageCount > current.TotalImageCount),
            current.TotalRating,
            1 + categoryItems.Count(item => item.TotalRating > current.TotalRating));
    }

    private static GalleryCreatorSummaryDto? FindCreatorTrackingSummary(
        GalleryCreatorSummarySnapshotDto snapshot,
        string category,
        string creator,
        string? creatorFolder)
    {
        var matches = snapshot.Items
            .Where(item =>
                string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Creator.Trim(), creator.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(creatorFolder))
        {
            var folderMatch = matches.FirstOrDefault(item =>
                string.Equals(
                    item.CreatorFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    creatorFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
            if (folderMatch is not null)
            {
                return folderMatch;
            }
        }

        return matches[0];
    }

    private static CreatorTrackingTemplateContextDto? ReadCreatorTrackingTemplateContext(JsonElement root)
    {
        if (!root.TryGetProperty("templateContext", out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return property.Deserialize<CreatorTrackingTemplateContextDto>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long ReadRequiredLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var value)
            ? value
            : throw new InvalidOperationException($"{name} が指定されていません。");

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .OfType<string>()
            .ToArray();
    }

    private static IReadOnlyList<int> ReadIntArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out _))
            .Select(item => item.GetInt32())
            .Distinct()
            .ToArray();
    }

    private static string GetCommonParentDirectory(IReadOnlyList<string> paths)
    {
        var parentPath = Path.GetDirectoryName(paths.FirstOrDefault() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(parentPath) ||
            paths.Any(path => !string.Equals(Path.GetDirectoryName(path), parentPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("同じフォルダ内のフォルダだけをまとめて圧縮できます。");
        }

        return parentPath;
    }

    private static IReadOnlyList<long> ReadLongArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _))
            .Select(value => value.GetInt64())
            .ToArray();
    }

    private static IReadOnlyList<ExplorerTabStateDto> ReadExplorerTabs(JsonElement root)
    {
        if (!root.TryGetProperty("tabs", out var tabsProperty) || tabsProperty.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var activeIndex = root.TryGetProperty("activeIndex", out var activeIndexProperty) && activeIndexProperty.TryGetInt32(out var value)
            ? value
            : -1;
        var tabs = new List<ExplorerTabStateDto>();
        var position = 0;
        foreach (var tab in tabsProperty.EnumerateArray())
        {
            var path = ReadOptionalString(tab, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var label = ReadOptionalString(tab, "label") ?? Path.GetFileName(path.TrimEnd('\\', '/'));
            tabs.Add(new ExplorerTabStateDto(position, path, label, position == activeIndex));
            position++;
        }

        return tabs;
    }

    private static void TrySetWindowsFileClipboard(IReadOnlyList<string> paths, bool isCut)
    {
        try
        {
            var files = paths
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                return;
            }

            var fileDropList = new StringCollection();
            fileDropList.AddRange(files);

            var data = new DataObject();
            data.SetFileDropList(fileDropList);
            data.SetData(
                PreferredDropEffectFormat,
                new MemoryStream(BitConverter.GetBytes(isCut ? DropEffectMove : DropEffectCopy)));
            Clipboard.SetDataObject(data, true);
        }
        catch
        {
            // The internal clipboard remains available when another process locks Windows Clipboard.
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_gidMigrationInProgress)
        {
            e.Cancel = true;
            PostMessage(new { type = "settings.gid.migration.closeBlocked" });
            return;
        }

        if (_allowCloseAfterCreatorTrackingFlush || Browser.CoreWebView2 is null)
        {
            return;
        }

        e.Cancel = true;
        if (_creatorTrackingCloseFlushRequested)
        {
            return;
        }

        _creatorTrackingCloseFlushRequested = true;
        try
        {
            var flushStarted = await Browser.CoreWebView2.ExecuteScriptAsync(
                "window.galleryBrowserFlushCreatorTracking ? (window.galleryBrowserFlushCreatorTracking(), true) : false");
            if (!string.Equals(flushStarted, "true", StringComparison.OrdinalIgnoreCase))
            {
                _allowCloseAfterCreatorTrackingFlush = true;
                Close();
            }
        }
        catch
        {
            _allowCloseAfterCreatorTrackingFlush = true;
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _galleryDatabaseUpdateCancellation?.Cancel();
        _pCloudAutoBackupCancellation?.Cancel();
        try
        {
            var bounds = WindowState == System.Windows.WindowState.Normal
                ? new Rect(0, 0, Width, Height)
                : RestoreBounds;
            _database.SaveWindowLayout(bounds.Width, bounds.Height, WindowState == System.Windows.WindowState.Maximized);
        }
        catch
        {
            // Window shutdown should not be blocked when the local settings store is unavailable.
        }
    }

    private void RestoreWindowLayout()
    {
        var state = _database.GetUiNavigationState();
        if (state.WindowWidth >= MinWidth && state.WindowHeight >= MinHeight)
        {
            Width = state.WindowWidth;
            Height = state.WindowHeight;
        }

        _restoreWindowMaximized = state.WindowIsMaximized;
    }

    private static bool TryReadWindowsFileClipboard(out IReadOnlyList<string> paths, out bool isCut)
    {
        paths = [];
        isCut = false;

        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return false;
            }

            var files = Clipboard.GetFileDropList()
                .Cast<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length == 0)
            {
                return false;
            }

            isCut = ReadPreferredDropEffect() is int effect && (effect & DropEffectMove) == DropEffectMove;
            paths = files;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadPreferredDropEffect()
    {
        var rawValue = Clipboard.GetData(PreferredDropEffectFormat);
        return rawValue switch
        {
            byte[] bytes when bytes.Length >= sizeof(int) => BitConverter.ToInt32(bytes, 0),
            MemoryStream stream when stream.Length >= sizeof(int) => BitConverter.ToInt32(stream.ToArray(), 0),
            int effect => effect,
            _ => null
        };
    }

    private void OpenExternal(string path, ExternalAppRuleDto? selectedRule = null, bool useConfiguredDefault = true)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        var rule = selectedRule ?? (useConfiguredDefault ? _database.FindExternalAppRule(path) : null);
        if (rule is null || !File.Exists(rule.ExecutablePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = rule.ExecutablePath,
            Arguments = BuildArguments(rule.LaunchOptions, path),
            UseShellExecute = false
        });
    }

    private void PostExternalAppRules()
    {
        PostMessage(new
        {
            type = "settings.externalApps.result",
            rules = _database.ListExternalAppRules()
        });
    }

    private void PostGidSettings(bool updated = false)
    {
        PostMessage(new
        {
            type = "settings.gid.result",
            settings = _database.GetGidSettings(),
            updated
        });
    }

    private void PostWinRarSettings()
    {
        var settings = _database.GetWinRarSettings();
        _winRarService.Configure(settings);
        PostMessage(new
        {
            type = "settings.winrar.result",
            settings
        });
    }

    private void PostFfmpegSettings()
    {
        var settings = _database.GetFfmpegSettings();
        _ffmpegService.Configure(settings);
        PostMessage(new
        {
            type = "settings.ffmpeg.result",
            settings,
            isAvailable = _ffmpegService.IsAvailable
        });
    }

    private void PostThumbnailCacheSettings()
    {
        _thumbnailService.ConfigureCacheRoot(_database.GetThumbnailCacheRoot());
        var targets = _database.ListThumbnailCacheTargets();
        _thumbnailService.ConfigureTargetDirectories(targets);
        RefreshThumbnailCacheHostMapping();
        PostMessage(new
        {
            type = "settings.thumbnailCache.result",
            targets,
            cacheRoot = _thumbnailService.CacheRoot
        });
    }

    private Action<FileTransferProgress> CreateExplorerTransferProgressReporter(string operation) =>
        progress => Dispatcher.BeginInvoke(() =>
            PostMessage(new
            {
                type = "explorer.transfer.progress",
                operation,
                progress.BytesTransferred,
                progress.TotalBytes,
                progress.CompletedFiles,
                progress.TotalFiles,
                progress.CurrentPath,
                progress.IsMove
            }));

    private void PostGalleryTargets(string? selectedSectionId = null, string? sectionAction = null)
    {
        PostMessage(new
        {
            type = "settings.galleryTargets.result",
            sections = _database.ListGallerySections(),
            targets = _database.ListGalleryScanTargets(),
            settings = _database.ListGalleryScanSettings(),
            selectedSectionId,
            sectionAction
        });
    }

    private void PostGalleryFilterDefinitions(long? selectedId = null, long? selectedCategoryId = null, bool updated = false)
    {
        var snapshot = _database.ListGalleryFilterDefinitions();
        PostMessage(new
        {
            type = "filters.editor.result",
            categories = snapshot.Categories,
            titles = snapshot.Titles,
            characters = snapshot.Characters,
            selectedId,
            selectedCategoryId,
            updated
        });
    }

    private void PostGalleryTagDefinitions(long? selectedId = null, bool updated = false)
    {
        var snapshot = _database.ListGalleryTagDefinitions();
        PostMessage(new
        {
            type = "tags.manager.result",
            tags = snapshot.Tags,
            selectedId,
            updated
        });
    }

    private void PostSearchEngineSettings()
    {
        PostMessage(new
        {
            type = "settings.searchEngine.result",
            settings = _database.GetSearchEngineSettings()
        });
    }

    private void OpenStandardNameSearch(string query)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("検索する名称を入力してください。", nameof(query));
        }

        var settings = _database.GetSearchEngineSettings();
        var encodedQuery = Uri.EscapeDataString(query);
        var url = settings.Provider switch
        {
            "brave" => $"https://search.brave.com/search?q={encodedQuery}",
            "gemini" => $"https://gemini.google.com/app?q={encodedQuery}",
            _ => settings.GoogleSearchUrlTemplate.Replace("{query}", encodedQuery, StringComparison.OrdinalIgnoreCase)
        };
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private static void OpenCreatorTrackingUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("http または https のURLを入力してください。", nameof(url));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private static async Task<string> FetchSiteIconDataUriAsync(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("URL／場所の背景文字にhttpまたはhttpsのURLを入力してください。", nameof(sourceUrl));
        }

        var candidates = new List<Uri>();
        var resolvedPageUri = sourceUri;
        try
        {
            using var pageResponse = await SiteIconHttpClient.GetAsync(
                sourceUri,
                HttpCompletionOption.ResponseHeadersRead);
            if (pageResponse.IsSuccessStatusCode)
            {
                var mediaType = pageResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;
                resolvedPageUri = pageResponse.RequestMessage?.RequestUri ?? sourceUri;
                if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    var directBytes = await ReadLimitedResponseBytesAsync(pageResponse, SiteIconDownloadMaximumBytes);
                    return CreateNormalizedSiteIconDataUri(mediaType, directBytes);
                }

                var pageBytes = await ReadResponsePrefixBytesAsync(pageResponse, 1_000_000);
                var html = Encoding.UTF8.GetString(pageBytes);
                foreach (Match tagMatch in HtmlLinkTagRegex.Matches(html))
                {
                    var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match attributeMatch in HtmlAttributeRegex.Matches(tagMatch.Value))
                    {
                        attributes[attributeMatch.Groups["name"].Value] = attributeMatch.Groups["value"].Value;
                    }
                    if (!attributes.TryGetValue("rel", out var rel) ||
                        !rel.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                        !attributes.TryGetValue("href", out var href) ||
                        !Uri.TryCreate(resolvedPageUri, href, out var iconUri) ||
                        iconUri.Scheme is not ("http" or "https"))
                    {
                        continue;
                    }
                    candidates.Add(iconUri);
                }
            }
        }
        catch (HttpRequestException)
        {
            // Some sites block page requests while still exposing /favicon.ico.
        }
        catch (TaskCanceledException)
        {
            // Fall back to the conventional favicon path after a page timeout.
        }

        candidates.Add(new Uri(resolvedPageUri, "/favicon.ico"));
        if (resolvedPageUri != sourceUri)
        {
            candidates.Add(new Uri(sourceUri, "/favicon.ico"));
        }
        Exception? lastIconError = null;
        foreach (var iconUri in candidates.DistinctBy(candidate => candidate.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var iconResponse = await SiteIconHttpClient.GetAsync(
                    iconUri,
                    HttpCompletionOption.ResponseHeadersRead);
                if (!iconResponse.IsSuccessStatusCode)
                {
                    continue;
                }

                var iconMediaType = iconResponse.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(iconMediaType) ||
                    !iconMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    iconMediaType = Path.GetExtension(iconUri.AbsolutePath).ToLowerInvariant() switch
                    {
                        ".png" => "image/png",
                        ".svg" => "image/svg+xml",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        _ => "image/x-icon"
                    };
                }

                var iconBytes = await ReadLimitedResponseBytesAsync(iconResponse, SiteIconDownloadMaximumBytes);
                if (iconBytes.Length > 0)
                {
                    return CreateNormalizedSiteIconDataUri(iconMediaType, iconBytes);
                }
            }
            catch (HttpRequestException)
            {
                // Try the next icon declared by the page.
            }
            catch (TaskCanceledException)
            {
                // Try the next icon declared by the page.
            }
            catch (InvalidDataException ex)
            {
                lastIconError = ex;
                // A page can declare multiple icons. Try a smaller or supported candidate next.
            }
        }

        if (lastIconError is not null)
        {
            throw new InvalidOperationException(lastIconError.Message, lastIconError);
        }
        throw new InvalidOperationException("サイトのアイコン画像を取得できませんでした。");
    }

    private static async Task<byte[]> ReadLimitedResponseBytesAsync(HttpResponseMessage response, int maximumBytes)
    {
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException("取得対象の画像サイズが上限を超えています。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var count = await stream.ReadAsync(chunk);
            if (count == 0)
            {
                break;
            }
            if (buffer.Length + count > maximumBytes)
            {
                throw new InvalidDataException("取得対象の画像サイズが上限を超えています。");
            }
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadResponsePrefixBytesAsync(HttpResponseMessage response, int maximumBytes)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream(maximumBytes);
        var chunk = new byte[16 * 1024];
        while (buffer.Length < maximumBytes)
        {
            var remaining = maximumBytes - (int)buffer.Length;
            var count = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)));
            if (count == 0)
            {
                break;
            }
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static string CreateImageDataUri(string mediaType, byte[] bytes) =>
        $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";

    private static string CreateNormalizedSiteIconDataUri(string mediaType, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("取得したアイコン画像が空です。");
        }

        if (mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            if (bytes.Length <= SiteIconStoredMaximumBytes)
            {
                return CreateImageDataUri(mediaType, bytes);
            }
            throw new InvalidDataException("取得したSVG画像は保存上限を超えているため縮小できませんでした。");
        }

        try
        {
            using var image = Image.Load(bytes);
            var requiresNormalization = bytes.Length > SiteIconStoredMaximumBytes ||
                Math.Max(image.Width, image.Height) > SiteIconMaximumDimension;
            if (!requiresNormalization)
            {
                return CreateImageDataUri(mediaType, bytes);
            }

            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Mode = SixLabors.ImageSharp.Processing.ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(SiteIconMaximumDimension, SiteIconMaximumDimension),
                Sampler = KnownResamplers.Lanczos3
            }));

            using var output = new MemoryStream();
            image.Save(output, new PngEncoder());
            var normalizedBytes = output.ToArray();
            if (normalizedBytes.Length > SiteIconStoredMaximumBytes)
            {
                throw new InvalidDataException("縮小後のアイコン画像サイズが保存上限を超えています。");
            }
            return CreateImageDataUri("image/png", normalizedBytes);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            if (bytes.Length <= SiteIconStoredMaximumBytes)
            {
                return CreateImageDataUri(mediaType, bytes);
            }
            throw new InvalidDataException("取得した画像形式を縮小できませんでした。", ex);
        }
    }

    private static HttpClient CreateSiteIconHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GalleryBrowser/1.0 (+desktop-site-icon-fetcher)");
        return client;
    }

    private static IReadOnlyList<GalleryFilterCategoryImportRowDto> ReadGalleryFilterCategoryCsvRows(string path, bool skipHeader)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        var delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        var fields = ParseDelimited(text, delimiter);
        var header = skipHeader && fields.Count > 0
            ? BuildGalleryFilterCsvHeader(fields[0])
            : null;
        var isTemplate = header?.ContainsKey("filtercategoryid") == true;
        var start = skipHeader ? 1 : 0;
        var rows = new List<GalleryFilterCategoryImportRowDto>();
        for (var index = start; index < fields.Count; index++)
        {
            var values = fields[index];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            if ((!isTemplate || header is null) && values.Count < 5)
            {
                throw new InvalidDataException($"リストの{index + 1}行目は5列（filter_category_id, Category, New_Category, Add, Merge）が必要です。");
            }

            if (isTemplate && header is not null)
            {
                rows.Add(new GalleryFilterCategoryImportRowDto(
                    index + 1,
                    ReadOptionalLong(values, header, "filtercategoryid"),
                    ReadGalleryFilterCsvValue(values, header, "category"),
                    ReadGalleryFilterCsvValue(values, header, "newcategory"),
                    ReadBoolean(values, header, "add"),
                    ReadOptionalLong(values, header, "merge")));
                continue;
            }

            rows.Add(new GalleryFilterCategoryImportRowDto(
                index + 1,
                ReadOptionalPositiveLong(values[0], "filter_category_id", index + 1),
                values[1].Trim().TrimStart('\uFEFF'),
                values[2].Trim(),
                ReadBooleanValue(values[3]),
                ReadOptionalPositiveLong(values[4], "Merge", index + 1)));
        }
        return rows;
    }

    private static IReadOnlyList<GalleryTagImportRowDto> ReadGalleryTagCsvRows(string path, bool skipHeader)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        var delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        var fields = ParseDelimited(text, delimiter);
        var header = skipHeader && fields.Count > 0
            ? BuildGalleryFilterCsvHeader(fields[0])
            : null;
        var isTemplate = header?.ContainsKey("tagid") == true && header.ContainsKey("tag") && header.ContainsKey("newtag") && header.ContainsKey("useflg");
        var start = skipHeader ? 1 : 0;
        var rows = new List<GalleryTagImportRowDto>();
        for (var index = start; index < fields.Count; index++)
        {
            var values = fields[index];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            if ((!isTemplate || header is null) && values.Count < 4)
            {
                throw new InvalidDataException($"リストの{index + 1}行目は4列（tag_id, tag, New_tag, use_flg）が必要です。");
            }

            if (isTemplate && header is not null)
            {
                rows.Add(new GalleryTagImportRowDto(
                    index + 1,
                    ReadOptionalLong(values, header, "tagid"),
                    ReadGalleryFilterCsvValue(values, header, "tag"),
                    ReadGalleryFilterCsvValue(values, header, "newtag"),
                    ReadOptionalUseFlag(values, header, "useflg")));
                continue;
            }

            rows.Add(new GalleryTagImportRowDto(
                index + 1,
                ReadOptionalPositiveLong(values[0], "tag_id", index + 1),
                values[1].Trim().TrimStart('\uFEFF'),
                values[2].Trim(),
                ReadOptionalUseFlagValue(values[3], index + 1)));
        }
        return rows;
    }

    private static bool LooksLikeGalleryTagCsvHeader(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        var delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        var firstRow = ParseDelimited(text, delimiter).FirstOrDefault();
        if (firstRow is null)
        {
            return false;
        }
        var header = BuildGalleryFilterCsvHeader(firstRow);
        return header.ContainsKey("tagid") && header.ContainsKey("tag") && header.ContainsKey("newtag") && header.ContainsKey("useflg");
    }

    private static bool LooksLikeGalleryFilterCategoryCsvHeader(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        var delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        var firstRow = ParseDelimited(text, delimiter).FirstOrDefault();
        if (firstRow is null)
        {
            return false;
        }
        var header = BuildGalleryFilterCsvHeader(firstRow);
        return header.ContainsKey("filtercategoryid") && header.ContainsKey("category");
    }

    private static IReadOnlyList<GalleryFilterCombinationImportRowDto> ReadGalleryFilterCsvRows(string path, bool skipHeader)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        var delimiter = string.Equals(Path.GetExtension(path), ".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
        var fields = ParseDelimited(text, delimiter);
        var header = skipHeader && fields.Count > 0
            ? BuildGalleryFilterCsvHeader(fields[0])
            : null;
        var isCombinationTemplate = header?.ContainsKey("combinationid") == true;
        var start = skipHeader ? 1 : 0;
        var rows = new List<GalleryFilterCombinationImportRowDto>();
        for (var index = start; index < fields.Count; index++)
        {
            var values = fields[index];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }
            if ((!isCombinationTemplate || header is null) && values.Count < 3)
            {
                throw new InvalidDataException($"リストの{index + 1}行目は3列（Category, Title, Character）が必要です。");
            }
            if (!isCombinationTemplate || header is null)
            {
                rows.Add(new GalleryFilterCombinationImportRowDto(
                    index + 1,
                    null,
                    null,
                    values[0].Trim().TrimStart('\uFEFF'),
                    null,
                    values[1].Trim(),
                    null,
                    values[2].Trim(),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    1,
                    true,
                    null,
                    true));
                continue;
            }

            rows.Add(new GalleryFilterCombinationImportRowDto(
                index + 1,
                ReadOptionalLong(values, header, "combinationid"),
                ReadOptionalLong(values, header, "filtercategoryid"),
                ReadGalleryFilterCsvValue(values, header, "category"),
                ReadOptionalLong(values, header, "titlefilterid"),
                ReadGalleryFilterCsvValue(values, header, "title"),
                ReadOptionalLong(values, header, "characterfilterid"),
                ReadGalleryFilterCsvValue(values, header, "character"),
                ReadGalleryFilterCsvValue(values, header, "newcategory"),
                ReadGalleryFilterCsvValue(values, header, "newtitle"),
                ReadGalleryFilterCsvValue(values, header, "newcharacter"),
                ReadOptionalUseFlag(values, header, "useflg"),
                ReadBoolean(values, header, "add"),
                ReadOptionalLong(values, header, "merge"),
                false));
        }
        return rows;
    }

    private void ExportGalleryFilterList(string path)
    {
        var combinations = _database.ListGalleryFilterCombinationExportRows();
        var delimiter = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t';
        var output = new StringBuilder();

        AppendDelimitedRow(output, delimiter,
            "combination_id", "filter_category_id", "Category", "title_filter_id", "Title", "character_filter_id", "Character",
            "New_Category", "New_Title", "New_Character", "use_flg", "Add", "Merge");
        foreach (var combination in combinations)
        {
            AppendDelimitedRow(
                output,
                delimiter,
                combination.CombinationId.ToString(),
                combination.FilterCategoryId?.ToString() ?? string.Empty,
                NormalizeExportFilterCategory(combination.CategoryName),
                combination.TitleFilterId.ToString(),
                combination.TitleName,
                combination.CharacterFilterId?.ToString() ?? string.Empty,
                string.IsNullOrWhiteSpace(combination.CharacterName) ? "Unassigned" : combination.CharacterName,
                string.Empty,
                string.Empty,
                string.Empty,
                combination.UseFlag.ToString(),
                string.Empty,
                string.Empty);
        }

        File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void ExportGalleryFilterCategoryList(string path)
    {
        var categories = _database.ListGalleryFilterCategoryExportRows();
        var delimiter = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t';
        var output = new StringBuilder();

        AppendDelimitedRow(output, delimiter, "filter_category_id", "Category", "New_Category", "Add", "Merge");
        foreach (var category in categories)
        {
            AppendDelimitedRow(
                output,
                delimiter,
                category.FilterCategoryId.ToString(),
                category.CategoryName,
                string.Empty,
                string.Empty,
                string.Empty);
        }

        File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void ExportGalleryTagList(string path)
    {
        var tags = _database.ListGalleryTagExportRows();
        var delimiter = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t';
        var output = new StringBuilder();

        AppendDelimitedRow(output, delimiter, "tag_id", "tag", "New_tag", "use_flg");
        foreach (var tag in tags)
        {
            AppendDelimitedRow(
                output,
                delimiter,
                tag.TagId.ToString(),
                tag.Tag,
                string.Empty,
                tag.UseFlag.ToString());
        }

        File.WriteAllText(path, output.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string NormalizeExportFilterCategory(string value) =>
        string.Equals(value, "未分類", StringComparison.Ordinal) ? string.Empty : value;

    private static void AppendDelimitedRow(StringBuilder output, char delimiter, params string[] values)
    {
        output.AppendLine(string.Join(delimiter, values.Select(value => EscapeDelimitedValue(value, delimiter))));
    }

    private static string EscapeDelimitedValue(string value, char delimiter)
    {
        if (value.IndexOfAny([delimiter, '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static bool LooksLikeGalleryFilterCsvHeader(GalleryFilterCombinationImportRowDto? row) =>
        row is not null && row.IsLegacyRow &&
        ((string.Equals(row.CategoryName, "Category", StringComparison.OrdinalIgnoreCase) &&
          string.Equals(row.TitleName, "Title", StringComparison.OrdinalIgnoreCase) &&
          string.Equals(row.CharacterName, "Character", StringComparison.OrdinalIgnoreCase)) ||
         (string.Equals(row.CategoryName, "combination_id", StringComparison.OrdinalIgnoreCase) &&
          string.Equals(row.TitleName, "filter_category_id", StringComparison.OrdinalIgnoreCase) &&
          string.Equals(row.CharacterName, "Category", StringComparison.OrdinalIgnoreCase)));

    private async Task PostGalleryFilterCsvPreviewAsync(IReadOnlyList<GalleryFilterCombinationImportRowDto> rows, bool hasHeader, bool suggestedHeader)
    {
        var analysis = await Task.Run(() => _database.PreviewGalleryFilterCombinationImport(rows));
        PostMessage(new
        {
            type = "filters.editor.csv.preview",
            scope = "combination",
            fileName = Path.GetFileName(_pendingFilterCsvPath),
            hasHeader,
            suggestedHeader,
            total = rows.Count,
            rows = rows.Take(20).ToArray(),
            analysis
        });
    }

    private void PostGalleryFilterCategoryCsvPreview(IReadOnlyList<GalleryFilterCategoryImportRowDto> rows, bool hasHeader, bool suggestedHeader)
    {
        PostMessage(new
        {
            type = "filters.editor.csv.preview",
            scope = "category",
            fileName = Path.GetFileName(_pendingFilterCsvPath),
            hasHeader,
            suggestedHeader,
            total = rows.Count,
            rows = rows.Take(20).Select(row => new
            {
                row.RowNumber,
                row.FilterCategoryId,
                row.CategoryName,
                row.NewCategoryName,
                row.Add,
                row.MergeFilterCategoryId
            }).ToArray()
        });
    }

    private void PostGalleryTagCsvPreview(IReadOnlyList<GalleryTagImportRowDto> rows, bool hasHeader, bool suggestedHeader)
    {
        PostMessage(new
        {
            type = "tags.manager.csv.preview",
            fileName = Path.GetFileName(_pendingTagCsvPath),
            hasHeader,
            suggestedHeader,
            total = rows.Count,
            rows = rows.Take(20).ToArray()
        });
    }

    private static Dictionary<string, int> BuildGalleryFilterCsvHeader(IReadOnlyList<string> values)
    {
        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < values.Count; index++)
        {
            var key = NormalizeGalleryFilterCsvHeader(values[index]);
            if (!string.IsNullOrWhiteSpace(key) && !header.ContainsKey(key))
            {
                header[key] = index;
            }
        }
        return header;
    }

    private static string NormalizeGalleryFilterCsvHeader(string value) =>
        value.Trim().TrimStart('\uFEFF').Replace("_", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string ReadGalleryFilterCsvValue(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index) && index < values.Count ? values[index].Trim() : string.Empty;

    private static long? ReadOptionalLong(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> header, string name)
    {
        var value = ReadGalleryFilterCsvValue(values, header, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidDataException($"{name} の値が不正です: {value}");
        }
        return parsed;
    }

    private static int? ReadOptionalUseFlag(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> header, string name)
    {
        var value = ReadGalleryFilterCsvValue(values, header, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!int.TryParse(value, out var parsed) || parsed is not 0 and not 1)
        {
            throw new InvalidDataException("use_flg は 0 または 1 で指定してください。");
        }
        return parsed;
    }

    private static int? ReadOptionalUseFlagValue(string value, int rowNumber)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!int.TryParse(value, out var parsed) || parsed is not 0 and not 1)
        {
            throw new InvalidDataException($"{rowNumber}行目のuse_flgは0または1で指定してください。");
        }
        return parsed;
    }

    private static bool ReadBoolean(IReadOnlyList<string> values, IReadOnlyDictionary<string, int> header, string name)
    {
        var value = ReadGalleryFilterCsvValue(values, header, name);
        return ReadBooleanValue(value);
    }

    private static bool ReadBooleanValue(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("on", StringComparison.OrdinalIgnoreCase);

    private static long? ReadOptionalPositiveLong(string value, string name, int rowNumber)
    {
        value = value.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!long.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new InvalidDataException($"{rowNumber}行目の{name}の値が不正です: {value}");
        }
        return parsed;
    }

    private static IReadOnlyList<List<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == delimiter && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if ((character == '\r' || character == '\n') && !quoted)
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else
            {
                field.Append(character);
            }
        }
        if (quoted)
        {
            throw new InvalidDataException("CSVの引用符が閉じられていません。");
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private void PostThumbnailAdjustments(bool updated = false)
    {
        PostMessage(new
        {
            type = "settings.thumbnailAdjustments.result",
            adjustments = _database.ListThumbnailCropAdjustments(),
            updated
        });
    }

    private void PostGalleryDatabaseSettings()
    {
        var settings = _database.GetGalleryDatabaseSettings();
        PostMessage(new
        {
            type = "settings.sqliteDatabase.result",
            settings = new
            {
                databasePath = ApplicationPaths.ToEnvironmentVariablePath(settings.DatabasePath),
                cacheDatabasePath = ApplicationPaths.ToEnvironmentVariablePath(settings.CacheDatabasePath),
                configuredCacheDatabasePath = ApplicationPaths.ToEnvironmentVariablePath(settings.ConfiguredCacheDatabasePath),
                settings.CacheDatabaseRestartRequired
            }
        });
    }

    private void PostPCloudBackupSettings()
    {
        PostMessage(new
        {
            type = "settings.pcloud.result",
            settings = _pCloudBackupService.GetSettings()
        });
    }

    private void SavePCloudSettingsFromMessage(JsonElement root)
    {
        _pCloudBackupService.SaveSettings(
            ReadOptionalString(root, "apiHost") ?? "eapi.pcloud.com",
            ReadOptionalString(root, "targetFolder") ?? string.Empty,
            ReadOptionalString(root, "clientId") ?? string.Empty,
            ReadOptionalString(root, "accessToken"),
            ReadRequiredBoolean(root, "autoBackupEnabled"),
            ReadRequiredInt32(root, "checkIntervalMinutes"),
            ReadRequiredInt32(root, "backupIntervalDays"),
            ReadRequiredInt32(root, "maximumSnapshots"),
            ReadRequiredInt32(root, "idleThresholdMinutes"));
    }

    private void StartPCloudAutoBackupScheduler()
    {
        if (_pCloudAutoBackupCancellation is not null)
        {
            return;
        }

        _pCloudAutoBackupCancellation = new CancellationTokenSource();
        _ = RunPCloudAutoBackupSchedulerAsync(_pCloudAutoBackupCancellation.Token);
    }

    private void RestartPCloudAutoBackupScheduler()
    {
        _pCloudAutoBackupCancellation?.Cancel();
        _pCloudAutoBackupCancellation = null;
        StartPCloudAutoBackupScheduler();
    }

    private async Task RunPCloudAutoBackupSchedulerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await TryRunPCloudAutoBackupAsync(cancellationToken);
                var settings = _pCloudBackupService.GetSettings();
                await Task.Delay(TimeSpan.FromMinutes(settings.CheckIntervalMinutes), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private async Task TryRunPCloudAutoBackupAsync(CancellationToken cancellationToken)
    {
        var settings = _pCloudBackupService.GetSettings();
        if (!_pCloudBackupService.IsAutomaticBackupDue() ||
            !IsApplicationIdleFor(settings.IdleThresholdMinutes) ||
            Interlocked.CompareExchange(ref _pCloudAutoBackupInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            PostMessage(new
            {
                type = "settings.pcloud.autoBackup.started",
                message = "pCloudへSQLiteDBをバックアップしています..."
            });
            var result = await _pCloudBackupService.BackupIfAutomaticDueAsync(cancellationToken);
            if (result is null)
            {
                PostMessage(new
                {
                    type = "settings.pcloud.autoBackup.finished"
                });
                return;
            }
            PostPCloudBackupSettings();
            PostMessage(new
            {
                type = "settings.pcloud.autoBackup.finished"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception exception)
        {
            PostMessage(new
            {
                type = "settings.pcloud.operation.error",
                action = "autoBackup",
                message = $"pCloud自動バックアップに失敗しました: {exception.Message}"
            });
        }
        finally
        {
            Interlocked.Exchange(ref _pCloudAutoBackupInProgress, 0);
        }
    }

    private bool IsApplicationIdleFor(int idleThresholdMinutes)
    {
        var lastActivityTicks = Interlocked.Read(ref _lastUserActivityUtcTicks);
        var idleDuration = DateTime.UtcNow - new DateTime(lastActivityTicks, DateTimeKind.Utc);
        return idleDuration >= TimeSpan.FromMinutes(idleThresholdMinutes);
    }

    private string BuildPCloudBackupCompletionMessage(PCloudBackupResult result, string heading)
    {
        var details = new List<string>
        {
            heading,
            $"{result.TargetFolder}/{result.FileName}",
            $"{result.SizeBytes:N0} bytes",
            $"保持数: 直近{_pCloudBackupService.GetSettings().MaximumSnapshots:N0}件"
        };
        if (result.RemovedOldSnapshots > 0)
        {
            details.Add($"古いスナップショットを{result.RemovedOldSnapshots:N0}件整理しました。");
        }
        if (!string.IsNullOrWhiteSpace(result.RetentionWarning))
        {
            details.Add(result.RetentionWarning);
        }
        return string.Join('\n', details);
    }

    private void RestartApplicationAfterExit()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("GalleryBrowserの実行ファイルを確認できません。");
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"--wait-for-pid {Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            UseShellExecute = true
        });
        Close();
    }

    private async Task<string?> GetGalleryThumbnailUriAsync(string path, string category)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }

        EnsureThumbnailCacheConfiguration();
        var thumbnailItem = new GalleryItemDto(
            StringComparer.OrdinalIgnoreCase.GetHashCode(path),
            Path.GetFileName(path.TrimEnd('\\', '/')),
            Directory.Exists(path) ? "folder" : "archive",
            path,
            0,
            [],
            "#93c5fd",
            null);
        return await _thumbnailService.GetOrCreateThumbnailUriAsync(
            thumbnailItem,
            priority: 10,
            cropAdjustment: GetThumbnailCropAdjustment(category));
    }

    private async Task<ExplorerDatabaseSyncResult> SynchronizeExplorerFoldersAsync(
        IReadOnlyList<string> folders,
        IReadOnlyList<string>? additionalExtensions = null)
    {
        var normalizedFolders = folders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mappedFolders = normalizedFolders
            .Select(path => new { Path = path, Category = _database.FindGalleryCategoryForPath(path) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Category))
            .ToArray();
        var unmappedCount = normalizedFolders.Length - mappedFolders.Length;
        if (mappedFolders.Length == 0)
        {
            return new ExplorerDatabaseSyncResult(
                "Gallery走査対象外のため、DB登録とサムネイル作成は行いませんでした。",
                true);
        }

        var requests = mappedFolders
            .GroupBy(item => item.Category!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new GalleryFolderScanRequest(
                group.Key,
                group.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                additionalExtensions))
            .ToArray();
        var updateResult = await _galleryDatabaseUpdateService.UpdateFoldersAsync(requests, PostExplorerDatabaseProgress);

        EnsureThumbnailCacheConfiguration();
        var thumbnailSources = requests
            .SelectMany(request => _database.ListGalleryItemPathsUnderFolders(request.Category, request.Folders)
                .Select(path => new GalleryThumbnailSource(path, path, request.Category)))
            .GroupBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(source => File.Exists(source.Path) || Directory.Exists(source.Path))
            .ToArray();
        var generatedCount = 0;
        var completedCount = 0;
        foreach (var batch in thumbnailSources.Chunk(16))
        {
            var results = await Task.WhenAll(batch.Select(source =>
                GetGalleryThumbnailUriAsync(source.Path, source.Category)));
            generatedCount += results.Count(uri => !string.IsNullOrWhiteSpace(uri));
            completedCount += batch.Length;
            PostExplorerDatabaseProgress($"サムネイルキャッシュを作成しています ({completedCount:N0}/{thumbnailSources.Length:N0})");
        }

        var hasScanErrors = !string.Equals(updateResult.ErrorSummary.Trim(), "Errors: 0", StringComparison.OrdinalIgnoreCase);
        var message = $"DBを{thumbnailSources.Length:N0}件同期し、サムネイルを{generatedCount:N0}件準備しました。";
        if (unmappedCount > 0)
        {
            message += $" Gallery走査対象外の{unmappedCount}フォルダはDB同期をスキップしました。";
        }
        if (hasScanErrors)
        {
            message += $" {updateResult.ErrorSummary}";
        }
        return new ExplorerDatabaseSyncResult(message, unmappedCount > 0 || hasScanErrors);
    }

    private async Task<string> RefreshCreatorTrackingGalleryFolderAsync(CreatorTrackingDto tracking)
    {
        var galleryPath = tracking.StorageLocations
            .FirstOrDefault(location => string.Equals(location.Usage, "Gallery", StringComparison.OrdinalIgnoreCase))?
            .Path?
            .Trim();
        if (string.IsNullOrWhiteSpace(galleryPath))
        {
            galleryPath = tracking.MainStoragePath?.Trim();
        }

        var creatorName = string.IsNullOrWhiteSpace(tracking.DisplayName)
            ? tracking.Creator
            : tracking.DisplayName;
        if (string.IsNullOrWhiteSpace(galleryPath))
        {
            throw new InvalidOperationException($"{creatorName}のStorageに用途「Gallery」のフォルダが設定されていません。");
        }
        if (!Directory.Exists(galleryPath))
        {
            throw new DirectoryNotFoundException($"{creatorName}のGalleryフォルダが見つかりません: {galleryPath}");
        }
        if (string.IsNullOrWhiteSpace(_database.FindGalleryCategoryForPath(galleryPath)))
        {
            throw new InvalidOperationException(
                $"{creatorName}のGalleryフォルダはSettings > Appearance > 区分別の設定の対象ディレクトリ配下にありません: {galleryPath}");
        }

        PostExplorerDatabaseProgress($"{creatorName}のGalleryフォルダを走査しています...");
        var result = await SynchronizeExplorerFoldersAsync([galleryPath]);
        if (result.HasWarnings)
        {
            throw new InvalidOperationException(result.Message);
        }
        return result.Message;
    }

    private async Task<ExplorerDatabaseSyncResult> TrySynchronizeExplorerFoldersAsync(
        IReadOnlyList<string> folders,
        IReadOnlyList<string>? additionalExtensions = null)
    {
        try
        {
            return await SynchronizeExplorerFoldersAsync(folders, additionalExtensions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or SqliteException)
        {
            return new ExplorerDatabaseSyncResult(
                $"ファイル操作は完了しましたが、DB登録またはサムネイル作成に失敗しました: {ex.Message}",
                true);
        }
    }

    private void PostExplorerDatabaseProgress(string message)
    {
        PostMessageOnDispatcher(new { type = "explorer.dbManagement.progress", message });
    }

    private void PostExplorerWinRarProgress(string message)
    {
        PostMessageOnDispatcher(new { type = "explorer.winrar.progress", message });
    }

    private void PostMessageOnDispatcher(object payload)
    {
        void TryPost()
        {
            try
            {
                if (Browser.CoreWebView2 is not null)
                {
                    PostMessage(payload);
                }
            }
            catch (ObjectDisposedException)
            {
                // The background operation is allowed to finish after navigation
                // or during shutdown; there is no UI left to notify in this case.
            }
            catch (InvalidOperationException)
            {
                // The WebView can disappear while the application is closing.
                // A late progress/result notification must never terminate WPF.
            }
        }

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (Dispatcher.CheckAccess())
        {
            TryPost();
            return;
        }

        Dispatcher.BeginInvoke((Action)TryPost);
    }

    private IReadOnlyDictionary<string, string> GetCachedGalleryThumbnailUris(
        IEnumerable<GalleryThumbnailSource> sources)
    {
        var thumbnailUris = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!File.Exists(source.Path) && !Directory.Exists(source.Path))
            {
                continue;
            }

            var thumbnailItem = new GalleryItemDto(
                StringComparer.OrdinalIgnoreCase.GetHashCode(source.Path),
                Path.GetFileName(source.Path.TrimEnd('\\', '/')),
                Directory.Exists(source.Path) ? "folder" : "archive",
                source.Path,
                0,
                [],
                "#93c5fd",
                null);
            var thumbnailUri = _thumbnailService.TryGetCachedThumbnailUri(
                thumbnailItem,
                GetThumbnailCropAdjustment(source.Category));
            if (!string.IsNullOrWhiteSpace(thumbnailUri))
            {
                thumbnailUris[source.Id] = thumbnailUri;
            }
        }
        return thumbnailUris;
    }

    private void EnsureThumbnailCacheConfiguration()
    {
        if (_thumbnailCacheConfigurationReady)
        {
            return;
        }

        _thumbnailService.ConfigureCacheRoot(_database.GetThumbnailCacheRoot());
        _thumbnailService.ConfigureTargetDirectories(_database.ListThumbnailCacheTargets());
        _ffmpegService.Configure(_database.GetFfmpegSettings());
        _thumbnailCacheConfigurationReady = true;
    }

    private ThumbnailCropAdjustmentDto GetThumbnailCropAdjustment(string category)
    {
        lock (_thumbnailCropAdjustmentLock)
        {
            if (_thumbnailCropAdjustments.TryGetValue(category, out var adjustment))
            {
                return adjustment;
            }

            adjustment = _database.GetThumbnailCropAdjustment(category);
            _thumbnailCropAdjustments[category] = adjustment;
            return adjustment;
        }
    }

    private void RefreshThumbnailCacheHostMapping()
    {
        if (Browser.CoreWebView2 is not null)
        {
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "gallerybrowser-cache.local",
                _thumbnailService.CacheRoot,
                CoreWebView2HostResourceAccessKind.Allow);
        }
    }

    private static string BuildArguments(string template, string path)
    {
        return template.Replace("{path}", path, StringComparison.Ordinal)
            .Replace("{directory}", Path.GetDirectoryName(path) ?? string.Empty, StringComparison.Ordinal)
            .Replace("{filename}", Path.GetFileName(path), StringComparison.Ordinal);
    }

    private void PostMessage(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            return;
        }

        Browser.NavigateToString($$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>GalleryBrowser</title>
                <style>
                  body {
                    margin: 0;
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                    background: #111318;
                    color: #f4f6fb;
                    font-family: "Segoe UI", sans-serif;
                  }
                  main {
                    max-width: 760px;
                    padding: 28px;
                    border: 1px solid rgba(255,255,255,.12);
                    border-radius: 8px;
                    background: rgba(255,255,255,.07);
                  }
                  code {
                    color: #93c5fd;
                  }
                </style>
              </head>
              <body>
                <main>
                  <h1>GalleryBrowser failed to load</h1>
                  <p>WebView2 navigation error:</p>
                  <p><code>{{System.Net.WebUtility.HtmlEncode(e.WebErrorStatus.ToString())}}</code></p>
                </main>
              </body>
            </html>
            """);
    }

}
