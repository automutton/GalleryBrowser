using GalleryBrowser.Models;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Text.RegularExpressions;

namespace GalleryBrowser.Services;

public enum ExternalAppActivation
{
    SingleClick,
    DoubleClick
}

public sealed class GalleryDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UnassignedFilterValue = "Unassigned";
    private const string LegacyNoCharacterFilterValue = "No Character";
    private const string DefaultCreatorTrackingDisplayCurrency = "JPY";
    private const int DefaultCreatorTrackingCompositionLabelLimit = 5;
    private const string CreatorSummaryCacheVersion = "v4";
    private const string UserMetricsCacheVersion = "v2";
    private const string CreatorTrackingDashboardCacheVersion = "v1";
    private const string GalleryWorksCacheVersion = "v2";
    private const string GalleryFiltersCacheVersion = "v1";
    private const string DefaultGidTargetExtensions = "zip;rar;7z;cbz;cbr";
    private const int DefaultGidDigitCount = 6;
    private const int MinimumGidDigitCount = 4;
    private const int MaximumGidDigitCount = 8;
    private const string GidAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string CreatorTrackingMetricDefaultsFileName = "creator-tracking-metrics.json";
    private static readonly Regex GidTagRegex = new(
        @"\{gid=(?<gid>[^{}]+)\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly string[] StandardGalleryFilters = ["rating", "creator", "title", "character", "tag", "core_title", "core_tags"];
    private static readonly string[] AvGalleryFilters = ["rating", "creator", "character", "tag", "core_tags"];
    private static readonly string[] CreatorTrackingMetricKeys =
        ["situation", "consistency", "continuity", "quality", "texture", "volume"];
    private static readonly CreatorTrackingActivityPlaceSettingDto[] DefaultCreatorTrackingActivityPlaces = [];
    private static readonly string[] DefaultCreatorTrackingFollowPolicyOptions = [];
    private static readonly HttpClient CreatorTrackingExchangeRateHttpClient = CreateCreatorTrackingExchangeRateHttpClient();
    private sealed record CreatorTrackingCharge(DateOnly Date, double Amount, string Currency);
    private sealed record CreatorTrackingSpendSummary(double Total, double RecentThreeMonthTotal, bool HasMissingRates);
    private sealed record CreatorTrackingDashboardAggregate(
        IReadOnlyDictionary<string, int> TrackingDaysByCreator,
        IReadOnlyDictionary<string, CreatorTrackingSpendSummary> SpendByCreator);
    private sealed record GidMigrationEntry(
        string OldGid,
        string NewGid,
        string OldPath,
        string NewPath,
        string OldSourceZipName,
        string NewSourceZipName,
        bool RenameFile,
        bool DeleteMissingItem);
    private sealed record GidMigrationPlan(
        IReadOnlyList<GidMigrationEntry> Entries,
        GidMigrationPreviewDto Preview);
    private sealed record CreatorTrackingSummaryFacts(
        string LastActivityOn,
        int SinceLastCheckDays,
        bool FollowWarnFlg,
        bool FollowAlertFlg,
        int TrackingDays,
        double TotalSpend,
        IReadOnlyList<string> Sites,
        IReadOnlyDictionary<string, int> EvaluationMetrics,
        double PersonalRating);
    private sealed class LegacyCreatorTrackingSubscription
    {
        public string Id { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Platform { get; init; } = string.Empty;
        public string Plan { get; init; } = string.Empty;
        public string Currency { get; init; } = "JPY";
        public double Amount { get; init; }
        public string BillingFrequency { get; init; } = "monthly";
        public string StartedOn { get; init; } = string.Empty;
        public string RenewalOn { get; init; } = string.Empty;
        public bool EndingPlanned { get; init; }
        public bool Reminder { get; init; }
    }
    private static readonly GalleryScanSettingsDto[] DefaultGalleryScanSettings =
    [
        new("gallery", ".zip,.rar,.7z,.cbz,.cbr", "portrait", StandardGalleryFilters, 3)
    ];
    private static readonly GallerySectionDto[] DefaultGallerySections =
    [
        new("gallery", "Gallery", 0)
    ];

    private readonly string _databasePath;
    private readonly string _defaultCacheDatabasePath;
    private readonly string _defaultExternalGalleryDatabasePath;
    private readonly StorageSettingsStore _storageSettingsStore;
    private readonly JsonSettingsStore _jsonSettingsStore;
    private readonly IReadOnlyList<CreatorTrackingMetricSettingDto> _creatorTrackingMetricDefaults;
    private string _externalGalleryDatabasePath = string.Empty;
    private string? _externalGalleryFilterSeedSignature;
    private string? _externalGallerySchemaReadyPath;
    private string? _applicationDataSchemaReadyPath;
    private readonly object _externalGallerySchemaLock = new();
    private readonly object _applicationDataSchemaLock = new();
    private readonly object _gidReservationLock = new();
    private readonly object _creatorSummaryCacheLock = new();
    private readonly object _derivedDataCacheLock = new();
    private string? _creatorSummaryMemorySignature;
    private GalleryCreatorSummarySnapshotDto? _creatorSummaryMemorySnapshot;
    private readonly Dictionary<string, DerivedDataMemoryCacheEntry> _derivedDataMemoryCache = new(StringComparer.Ordinal);
    private readonly MemoryLruCache<GalleryWorksPageDto> _galleryWorksCache = new(32);
    private readonly MemoryLruCache<GalleryWorkFiltersDto> _galleryFiltersCache = new(64);

    private sealed record DerivedDataMemoryCacheEntry(string SourceSignature, object Value);

    private sealed class MemoryLruCache<T>(int capacity)
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, (T Value, LinkedListNode<string> Node)> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _recency = [];

        public bool TryGet(string key, out T value)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    value = default!;
                    return false;
                }

                _recency.Remove(entry.Node);
                _recency.AddFirst(entry.Node);
                value = entry.Value;
                return true;
            }
        }

        public void Set(string key, T value)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    _recency.Remove(existing.Node);
                }

                var node = _recency.AddFirst(key);
                _entries[key] = (value, node);
                while (_entries.Count > capacity && _recency.Last is { } oldest)
                {
                    _entries.Remove(oldest.Value);
                    _recency.RemoveLast();
                }
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _recency.Clear();
            }
        }
    }

    private sealed class GalleryCreatorSummaryAccumulator(
        string category,
        string creator,
        string creatorFolder)
    {
        public string Category { get; } = category;
        public string Creator { get; } = creator;
        public string CreatorFolder { get; } = creatorFolder;
        public Dictionary<string, int> TitleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TagCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalRating { get; set; }
        public int FileCount { get; set; }
        public int ZipFileCount { get; set; }
        public int TotalImageCount { get; set; }
        public int RatedFileCount { get; set; }
        public int MaxRating { get; set; }
        public string LastAccessTime { get; set; } = string.Empty;
        public string LastUpdatedTime { get; set; } = string.Empty;
    }

    private sealed class UserMetricsEntityAccumulator(string label)
    {
        public string Label { get; } = label;
        public int FileCount { get; set; }
        public int ImageCount { get; set; }
        public int TotalRating { get; set; }
        public int RecentFileCount { get; set; }
        public int TargetTagFileCount { get; set; }
    }

    private sealed class UserMetricsTrendAccumulator
    {
        public int AddedFiles { get; set; }
        public int AddedImages { get; set; }
        public double Spend { get; set; }
    }

    private string ExternalGalleryDatabasePath => _externalGalleryDatabasePath;

    public GalleryDatabase(string dataDirectory)
        : this(dataDirectory, new StorageSettingsStore(dataDirectory))
    {
    }

    internal GalleryDatabase(string dataDirectory, StorageSettingsStore storageSettingsStore)
    {
        Batteries_V2.Init();

        Directory.CreateDirectory(dataDirectory);
        _defaultCacheDatabasePath = Path.Combine(dataDirectory, "gallerybrowser.cache.sqlite");
        _defaultExternalGalleryDatabasePath = Path.Combine(dataDirectory, "gallery_api.sqlite");
        _storageSettingsStore = storageSettingsStore;
        _creatorTrackingMetricDefaults = LoadCreatorTrackingMetricDefaults(dataDirectory);
        _databasePath = _storageSettingsStore.ResolveCacheDatabasePath(_defaultCacheDatabasePath);
        var cacheDirectory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(cacheDirectory))
        {
            Directory.CreateDirectory(cacheDirectory);
        }
        _jsonSettingsStore = new JsonSettingsStore(dataDirectory);
        EnsureCreated();
        using (var connection = OpenConnection())
        {
            _jsonSettingsStore.HydrateOrCreate(connection);
        }
        MigrateGalleryScanCoreFilters();
        _externalGalleryDatabasePath = ReadConfiguredGalleryDatabasePath();
        EnsureExternalApplicationDataSchema();
    }

    public void EnsureCreated()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS gallery_items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                kind TEXT NOT NULL CHECK (kind IN ('folder', 'archive')),
                path TEXT NOT NULL,
                item_count INTEGER NOT NULL DEFAULT 0,
                accent TEXT NOT NULL DEFAULT '#93c5fd',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS gallery_item_tags (
                item_id INTEGER NOT NULL,
                tag TEXT NOT NULL,
                PRIMARY KEY (item_id, tag),
                FOREIGN KEY (item_id) REFERENCES gallery_items(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS external_app_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL DEFAULT '',
                target_kind TEXT NOT NULL DEFAULT 'file',
                extension TEXT NOT NULL UNIQUE,
                executable_path TEXT NOT NULL,
                arguments_template TEXT NOT NULL DEFAULT '"{path}"',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS app_migrations (
                name TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS file_metadata (
                path TEXT PRIMARY KEY,
                file_name TEXT NOT NULL,
                romanized_name TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_file_metadata_romanized_name
                ON file_metadata (romanized_name);

            CREATE TABLE IF NOT EXISTS explorer_bookmarks (
                path TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                position INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS view_bookmarks (
                bookmark_id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                view_type TEXT NOT NULL,
                state_json TEXT NOT NULL,
                thumbnail_data_url TEXT NOT NULL DEFAULT '',
                window_width REAL NOT NULL DEFAULT 0,
                window_height REAL NOT NULL DEFAULT 0,
                window_is_maximized INTEGER NOT NULL DEFAULT 0,
                position INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS sticky_notes (
                note_id INTEGER PRIMARY KEY AUTOINCREMENT,
                view_type TEXT NOT NULL,
                context_key TEXT NOT NULL DEFAULT '',
                context_label TEXT NOT NULL DEFAULT '',
                content TEXT NOT NULL DEFAULT '',
                position_x REAL NOT NULL DEFAULT 24,
                position_y REAL NOT NULL DEFAULT 88,
                width REAL NOT NULL DEFAULT 250,
                height REAL NOT NULL DEFAULT 200,
                color_key TEXT NOT NULL DEFAULT 'amber',
                content_mode TEXT NOT NULL DEFAULT 'plain',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS explorer_tabs (
                position INTEGER PRIMARY KEY,
                path TEXT NOT NULL,
                label TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS new_tab_candidates (
                path TEXT PRIMARY KEY,
                label TEXT NOT NULL,
                position INTEGER NOT NULL DEFAULT 0,
                kind TEXT NOT NULL DEFAULT 'folder',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS ui_navigation_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                active_view TEXT NOT NULL DEFAULT 'library',
                explorer_bookmarks_expanded INTEGER NOT NULL DEFAULT 1,
                explorer_detail_columns TEXT NOT NULL DEFAULT 'icon,name,pages,gid,modified,type,size',
                mouse_gesture_settings TEXT NOT NULL DEFAULT '',
                gallery_card_columns TEXT NOT NULL DEFAULT '{}',
                explorer_card_columns INTEGER NOT NULL DEFAULT 5,
                window_width REAL NOT NULL DEFAULT 0,
                window_height REAL NOT NULL DEFAULT 0,
                window_is_maximized INTEGER NOT NULL DEFAULT 0,
                keyboard_shortcut_settings TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS winrar_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                executable_path TEXT NOT NULL DEFAULT '',
                supported_extensions TEXT NOT NULL DEFAULT '.zip,.rar,.7z,.cbz,.cbr',
                show_open_in_context_menu INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS ffmpeg_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                executable_path TEXT NOT NULL DEFAULT '',
                supported_extensions TEXT NOT NULL DEFAULT '.mp4,.mkv,.avi,.mov,.wmv,.webm,.flv,.m4v,.mpeg,.mpg,.ts',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS nconvert_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                executable_path TEXT NOT NULL DEFAULT '',
                temporary_directory TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS thumbnail_cache_targets (
                path TEXT PRIMARY KEY,
                position INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS thumbnail_cache_entries (
                cache_path TEXT PRIMARY KEY,
                source_path TEXT NOT NULL,
                source_stamp TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS thumbnail_cache_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                cache_root TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS creator_summary_cache (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                source_signature TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS derived_data_cache (
                cache_key TEXT PRIMARY KEY,
                source_signature TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS gallery_sections (
                section_id TEXT PRIMARY KEY,
                label TEXT NOT NULL COLLATE NOCASE,
                position INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_gallery_sections_label
                ON gallery_sections (label COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS gallery_scan_targets (
                category TEXT NOT NULL,
                path TEXT NOT NULL,
                position INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (category, path)
            );

            CREATE TABLE IF NOT EXISTS gallery_scan_settings (
                category TEXT PRIMARY KEY,
                supported_extensions TEXT NOT NULL DEFAULT '',
                card_aspect TEXT NOT NULL DEFAULT 'portrait',
                enabled_filters TEXT NOT NULL DEFAULT 'rating,creator,title,character,tag',
                file_name_lines INTEGER NOT NULL DEFAULT 3,
                creator_label TEXT NOT NULL DEFAULT 'Creator',
                title_label TEXT NOT NULL DEFAULT 'Title',
                character_label TEXT NOT NULL DEFAULT 'Character',
                tag_label TEXT NOT NULL DEFAULT 'Tag',
                core_title_label TEXT NOT NULL DEFAULT 'Core title',
                core_tags_label TEXT NOT NULL DEFAULT 'Core tags'
            );

            CREATE TABLE IF NOT EXISTS thumbnail_crop_adjustments (
                category TEXT PRIMARY KEY,
                horizontal_offset_percent REAL NOT NULL DEFAULT 0,
                vertical_offset_percent REAL NOT NULL DEFAULT -10,
                scale_percent REAL NOT NULL DEFAULT 100
            );

            CREATE TABLE IF NOT EXISTS gallery_database_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                database_path TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS search_engine_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                provider TEXT NOT NULL DEFAULT 'google',
                google_search_url_template TEXT NOT NULL DEFAULT 'https://www.google.com/search?q={query}',
                brave_api_key TEXT NOT NULL DEFAULT '',
                gemini_api_key TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS theme_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                theme TEXT NOT NULL DEFAULT 'dark',
                main_color TEXT NOT NULL DEFAULT '#caff19',
                sub_color TEXT NOT NULL DEFAULT '#ffb342',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS language_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                language TEXT NOT NULL DEFAULT 'ja',
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS calendar_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                week_start_day INTEGER NOT NULL DEFAULT 0 CHECK (week_start_day BETWEEN 0 AND 6),
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS gid_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                target_extensions TEXT NOT NULL DEFAULT 'zip;rar;7z;cbz;cbr',
                digit_count INTEGER NOT NULL DEFAULT 6,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS creator_tracking (
                creator TEXT PRIMARY KEY COLLATE NOCASE,
                display_name TEXT NOT NULL DEFAULT '',
                alternate_name TEXT NOT NULL DEFAULT '',
                tracking_status TEXT NOT NULL DEFAULT '',
                activity_status TEXT NOT NULL DEFAULT '',
                follow_up_status TEXT NOT NULL DEFAULT '',
                last_checked_on TEXT NOT NULL DEFAULT '',
                last_activity_on TEXT NOT NULL DEFAULT '',
                activity_summary TEXT NOT NULL DEFAULT '',
                activity_links_json TEXT NOT NULL DEFAULT '[]',
                main_storage_path TEXT NOT NULL DEFAULT '',
                work_storage_path TEXT NOT NULL DEFAULT '',
                unsorted_storage_path TEXT NOT NULL DEFAULT '',
                storage_locations_json TEXT NOT NULL DEFAULT '[]',
                evaluation_metrics_json TEXT NOT NULL DEFAULT '{}',
                personal_rating REAL NOT NULL DEFAULT 0,
                evaluation_memo TEXT NOT NULL DEFAULT '',
                subscription_history_json TEXT NOT NULL DEFAULT '[]',
                purchase_history_json TEXT NOT NULL DEFAULT '[]',
                monthly_support_amount REAL NOT NULL DEFAULT 0,
                lifetime_spend REAL NOT NULL DEFAULT 0,
                currency TEXT NOT NULL DEFAULT 'JPY',
                support_started_on TEXT NOT NULL DEFAULT '',
                support_ended_on TEXT NOT NULL DEFAULT '',
                support_memo TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS creator_tracking_exchange_rates (
                rate_date TEXT NOT NULL,
                base_currency TEXT NOT NULL,
                quote_currency TEXT NOT NULL,
                rate REAL NOT NULL,
                provider TEXT NOT NULL DEFAULT 'Frankfurter',
                fetched_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (rate_date, base_currency, quote_currency)
            );

            CREATE TABLE IF NOT EXISTS creator_tracking_archive_snapshots (
                creator TEXT NOT NULL COLLATE NOCASE,
                category TEXT NOT NULL,
                snapshot_date TEXT NOT NULL,
                file_count INTEGER NOT NULL DEFAULT 0,
                image_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (creator, category, snapshot_date)
            );

            CREATE TABLE IF NOT EXISTS creator_tracking_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                activity_places_json TEXT NOT NULL DEFAULT '[]',
                composition_label_limit INTEGER NOT NULL DEFAULT 5,
                follow_policy_options_json TEXT NOT NULL DEFAULT '[]',
                metric_definitions_json TEXT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        command.ExecuteNonQuery();
        EnsureExternalAppRuleColumns(connection);
        MigrateExternalAppRulesToZipPlaStyle(connection);
        EnsureExplorerBookmarkColumns(connection);
        EnsureViewBookmarkColumns(connection);
        EnsureStickyNoteColumns(connection);
        EnsureUiNavigationStateColumns(connection);
        EnsureNewTabCandidateColumns(connection);
        EnsureGallerySections(connection);
        EnsureGalleryScanSettingsColumns(connection);
        MigrateGalleryScanCardAspects(connection);
        MigrateGalleryScanEnabledFilters(connection);
        EnsureThumbnailCropAdjustments(connection);
        EnsureCreatorTrackingColumns(connection);
        EnsureCreatorTrackingSettingsColumns(connection);
        RemoveLegacyWebTabs(connection);

        using (var databaseSettings = connection.CreateCommand())
        {
            databaseSettings.CommandText = "INSERT OR IGNORE INTO gallery_database_settings (id, database_path) VALUES (1, $path);";
            databaseSettings.Parameters.AddWithValue("$path", _defaultExternalGalleryDatabasePath);
            databaseSettings.ExecuteNonQuery();
        }

        using (var searchEngineSettings = connection.CreateCommand())
        {
            searchEngineSettings.CommandText = "INSERT OR IGNORE INTO search_engine_settings (id) VALUES (1);";
            searchEngineSettings.ExecuteNonQuery();
        }

        using (var themeSettings = connection.CreateCommand())
        {
            themeSettings.CommandText = "INSERT OR IGNORE INTO theme_settings (id) VALUES (1);";
            themeSettings.ExecuteNonQuery();
        }

        using (var languageSettings = connection.CreateCommand())
        {
            languageSettings.CommandText = "INSERT OR IGNORE INTO language_settings (id) VALUES (1);";
            languageSettings.ExecuteNonQuery();
        }

        using (var calendarSettings = connection.CreateCommand())
        {
            calendarSettings.CommandText = "INSERT OR IGNORE INTO calendar_settings (id) VALUES (1);";
            calendarSettings.ExecuteNonQuery();
        }

        using (var gidSettings = connection.CreateCommand())
        {
            gidSettings.CommandText = "INSERT OR IGNORE INTO gid_settings (id) VALUES (1);";
            gidSettings.ExecuteNonQuery();
        }

    }

    private void EnsureExternalApplicationDataSchema()
    {
        lock (_applicationDataSchemaLock)
        {
            if (string.Equals(
                    _applicationDataSchemaReadyPath,
                    ExternalGalleryDatabasePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!File.Exists(ExternalGalleryDatabasePath))
            {
                CreateExternalGalleryDatabaseFile(ExternalGalleryDatabasePath);
            }

            using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS view_bookmarks (
                    bookmark_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    view_type TEXT NOT NULL,
                    state_json TEXT NOT NULL,
                    thumbnail_data_url TEXT NOT NULL DEFAULT '',
                    window_width REAL NOT NULL DEFAULT 0,
                    window_height REAL NOT NULL DEFAULT 0,
                    window_is_maximized INTEGER NOT NULL DEFAULT 0,
                    position INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS sticky_notes (
                    note_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    view_type TEXT NOT NULL,
                    context_key TEXT NOT NULL DEFAULT '',
                    context_label TEXT NOT NULL DEFAULT '',
                    content TEXT NOT NULL DEFAULT '',
                    position_x REAL NOT NULL DEFAULT 24,
                    position_y REAL NOT NULL DEFAULT 88,
                    width REAL NOT NULL DEFAULT 250,
                    height REAL NOT NULL DEFAULT 200,
                    color_key TEXT NOT NULL DEFAULT 'amber',
                    content_mode TEXT NOT NULL DEFAULT 'plain',
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS creator_tracking (
                    creator TEXT PRIMARY KEY COLLATE NOCASE,
                    display_name TEXT NOT NULL DEFAULT '',
                    alternate_name TEXT NOT NULL DEFAULT '',
                    tracking_status TEXT NOT NULL DEFAULT '',
                    activity_status TEXT NOT NULL DEFAULT '',
                    follow_up_status TEXT NOT NULL DEFAULT '',
                    last_checked_on TEXT NOT NULL DEFAULT '',
                    last_activity_on TEXT NOT NULL DEFAULT '',
                    activity_summary TEXT NOT NULL DEFAULT '',
                    activity_links_json TEXT NOT NULL DEFAULT '[]',
                    main_storage_path TEXT NOT NULL DEFAULT '',
                    work_storage_path TEXT NOT NULL DEFAULT '',
                    unsorted_storage_path TEXT NOT NULL DEFAULT '',
                    storage_locations_json TEXT NOT NULL DEFAULT '[]',
                    evaluation_metrics_json TEXT NOT NULL DEFAULT '{}',
                    personal_rating REAL NOT NULL DEFAULT 0,
                    evaluation_memo TEXT NOT NULL DEFAULT '',
                    subscription_history_json TEXT NOT NULL DEFAULT '[]',
                    purchase_history_json TEXT NOT NULL DEFAULT '[]',
                    monthly_support_amount REAL NOT NULL DEFAULT 0,
                    lifetime_spend REAL NOT NULL DEFAULT 0,
                    currency TEXT NOT NULL DEFAULT 'JPY',
                    support_started_on TEXT NOT NULL DEFAULT '',
                    support_ended_on TEXT NOT NULL DEFAULT '',
                    support_memo TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS creator_tracking_exchange_rates (
                    rate_date TEXT NOT NULL,
                    base_currency TEXT NOT NULL,
                    quote_currency TEXT NOT NULL,
                    rate REAL NOT NULL,
                    provider TEXT NOT NULL DEFAULT 'Frankfurter',
                    fetched_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (rate_date, base_currency, quote_currency)
                );

                CREATE TABLE IF NOT EXISTS creator_tracking_archive_snapshots (
                    creator TEXT NOT NULL COLLATE NOCASE,
                    category TEXT NOT NULL,
                    snapshot_date TEXT NOT NULL,
                    file_count INTEGER NOT NULL DEFAULT 0,
                    image_count INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (creator, category, snapshot_date)
                );

                CREATE TABLE IF NOT EXISTS gid_registry (
                    gid TEXT PRIMARY KEY,
                    source_path TEXT NOT NULL DEFAULT '',
                    issued_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );
                """;
            command.ExecuteNonQuery();
            EnsureViewBookmarkColumns(connection);
            EnsureStickyNoteColumns(connection);
            EnsureCreatorTrackingColumns(connection);
            EnsureExternalItemsGenreRemoved(connection, ExternalGalleryDatabasePath);
            _applicationDataSchemaReadyPath = ExternalGalleryDatabasePath;
        }
    }

    private static void CreateExternalGalleryDatabaseFile(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = OpenStandaloneConnection(fullPath, SqliteOpenMode.ReadWriteCreate);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS items (
                gid TEXT PRIMARY KEY,
                current_path TEXT NOT NULL UNIQUE,
                source_zip_name TEXT NOT NULL,
                media_type TEXT NOT NULL DEFAULT 'archive',
                category TEXT NOT NULL DEFAULT '',
                top_folder TEXT NOT NULL DEFAULT '',
                creator TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                character TEXT NOT NULL DEFAULT '',
                rating INTEGER NOT NULL DEFAULT 0,
                image_count INTEGER,
                duration_seconds INTEGER,
                zip_entry_count INTEGER,
                total_uncompressed_size INTEGER,
                last_access_time TEXT,
                last_write_time TEXT,
                archived_flg INTEGER NOT NULL DEFAULT 0 CHECK (archived_flg IN (0, 1)),
                metadata_dirty INTEGER NOT NULL DEFAULT 0,
                metadata_synced_at TEXT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS tags (
                tag_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tag TEXT NOT NULL UNIQUE,
                use_flg INTEGER NOT NULL DEFAULT 1 CHECK (use_flg IN (0, 1)),
                position INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS item_tags (
                gid TEXT NOT NULL REFERENCES items(gid) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
                PRIMARY KEY (gid, tag_id)
            );

            CREATE TABLE IF NOT EXISTS item_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                gid TEXT REFERENCES items(gid) ON DELETE SET NULL,
                event_type TEXT NOT NULL,
                detail_json TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_items_path ON items(current_path);
            CREATE INDEX IF NOT EXISTS idx_items_rating ON items(rating);
            CREATE INDEX IF NOT EXISTS idx_items_creator ON items(creator);
            CREATE INDEX IF NOT EXISTS idx_items_title ON items(title);
            CREATE INDEX IF NOT EXISTS idx_items_character ON items(character);
            CREATE INDEX IF NOT EXISTS idx_items_archived_category ON items(archived_flg, category);
            CREATE INDEX IF NOT EXISTS idx_item_tags_tag_id ON item_tags(tag_id);

            CREATE VIEW IF NOT EXISTS v_items AS
            SELECT
                i.gid,
                i.current_path,
                i.source_zip_name,
                i.media_type,
                i.category,
                i.top_folder,
                i.creator,
                i.title,
                i.character,
                i.rating,
                COALESCE((
                    SELECT group_concat(t.tag, ',')
                    FROM item_tags AS it
                    JOIN tags AS t ON t.tag_id = it.tag_id
                    WHERE it.gid = i.gid
                    ORDER BY t.tag
                ), '') AS tags,
                i.image_count,
                i.duration_seconds,
                i.zip_entry_count,
                i.total_uncompressed_size,
                i.last_access_time,
                i.last_write_time,
                i.archived_flg,
                i.metadata_dirty,
                i.metadata_synced_at,
                i.created_at,
                i.updated_at
            FROM items AS i;
            """;
        command.ExecuteNonQuery();
        ValidateSqliteDatabase(connection, "新規Gallery SQLiteDB");
    }

    private static void EnsureExternalItemsGenreRemoved(SqliteConnection connection, string databasePath)
    {
        if (!DatabaseTableExists(connection, "main", "items"))
        {
            return;
        }
        var columns = ReadDatabaseColumns(connection, "main", "items");
        if (!columns.Contains("gid", StringComparer.OrdinalIgnoreCase) ||
            !columns.Contains("genre", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var backupPath = CreateGenreRemovalBackup(connection, databasePath);
        try
        {
            RemoveExternalItemsGenreColumn(connection);
            var migratedColumns = ReadDatabaseColumns(connection, "main", "items");
            if (migratedColumns.Contains("genre", StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Genre列が残っています。");
            }
            ValidateSqliteDatabase(connection, "Genre削除後のGallery SQLiteDB");
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Gallery SQLiteDBからGenre列を削除できませんでした。移行前バックアップ: {backupPath}",
                ex);
        }
    }

    private static void RemoveExternalItemsGenreColumn(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = """
                    DROP VIEW IF EXISTS v_items;
                    DROP INDEX IF EXISTS idx_items_genre;
                    ALTER TABLE items DROP COLUMN genre;
                    """;
                migrate.ExecuteNonQuery();
            }
            CreateExternalItemsView(connection, transaction);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string CreateGenreRemovalBackup(SqliteConnection source, string databasePath)
    {
        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalleryBrowser",
            "DatabaseMigrationBackups");
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var safeName = Path.GetFileNameWithoutExtension(databasePath);
        var backupPath = Path.Combine(backupDirectory, $"{safeName}_before_genre_removal_{timestamp}.sqlite");
        for (var suffix = 1; File.Exists(backupPath); suffix++)
        {
            backupPath = Path.Combine(backupDirectory, $"{safeName}_before_genre_removal_{timestamp}_{suffix}.sqlite");
        }

        using var destination = OpenStandaloneConnection(backupPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
        ValidateSqliteDatabase(destination, "Genre削除前バックアップ");
        return backupPath;
    }

    private static void CreateExternalItemsView(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var hasArchivedFlag = false;
        using (var schema = connection.CreateCommand())
        {
            schema.Transaction = transaction;
            schema.CommandText = "PRAGMA table_info(items);";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "archived_flg", StringComparison.OrdinalIgnoreCase))
                {
                    hasArchivedFlag = true;
                    break;
                }
            }
        }
        var archivedSelect = hasArchivedFlag ? "i.archived_flg" : "0 AS archived_flg";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            CREATE VIEW IF NOT EXISTS v_items AS
            SELECT
                i.gid,
                i.current_path,
                i.source_zip_name,
                i.media_type,
                i.category,
                i.top_folder,
                i.creator,
                i.title,
                i.character,
                i.rating,
                COALESCE((
                    SELECT group_concat(t.tag, ',')
                    FROM item_tags AS it
                    JOIN tags AS t ON t.tag_id = it.tag_id
                    WHERE it.gid = i.gid
                    ORDER BY t.tag
                ), '') AS tags,
                i.image_count,
                i.duration_seconds,
                i.zip_entry_count,
                i.total_uncompressed_size,
                i.last_access_time,
                i.last_write_time,
                {archivedSelect},
                i.metadata_dirty,
                i.metadata_synced_at,
                i.created_at,
                i.updated_at
            FROM items AS i;
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenApplicationDataConnection()
    {
        EnsureExternalApplicationDataSchema();
        return OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
    }

    private static void EnsureViewBookmarkColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "view_bookmarks")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnsureTableColumn(connection, columns, "thumbnaildataurl", "view_bookmarks", "thumbnail_data_url TEXT NOT NULL DEFAULT ''");
        EnsureTableColumn(connection, columns, "windowwidth", "view_bookmarks", "window_width REAL NOT NULL DEFAULT 0");
        EnsureTableColumn(connection, columns, "windowheight", "view_bookmarks", "window_height REAL NOT NULL DEFAULT 0");
        EnsureTableColumn(connection, columns, "windowismaximized", "view_bookmarks", "window_is_maximized INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureStickyNoteColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "sticky_notes")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnsureTableColumn(connection, columns, "contextkey", "sticky_notes", "context_key TEXT NOT NULL DEFAULT ''");
        EnsureTableColumn(connection, columns, "contextlabel", "sticky_notes", "context_label TEXT NOT NULL DEFAULT ''");
        EnsureTableColumn(connection, columns, "colorkey", "sticky_notes", "color_key TEXT NOT NULL DEFAULT 'amber'");
        EnsureTableColumn(connection, columns, "contentmode", "sticky_notes", "content_mode TEXT NOT NULL DEFAULT 'plain'");

        using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            UPDATE sticky_notes
            SET context_key = CASE view_type
                WHEN 'library' THEN 'ai'
                WHEN 'creators' THEN 'ai'
                ELSE context_key
            END
            WHERE TRIM(context_key) = '';
            UPDATE sticky_notes SET context_label = context_key WHERE TRIM(context_label) = '';
            UPDATE sticky_notes SET color_key = 'amber' WHERE TRIM(color_key) = '';
            UPDATE sticky_notes SET content_mode = 'plain' WHERE content_mode NOT IN ('plain', 'markdown');
            """;
        migrate.ExecuteNonQuery();
    }

    private static void EnsureTableColumn(
        SqliteConnection connection,
        ISet<string> columns,
        string normalizedName,
        string table,
        string definition)
    {
        if (columns.Contains(normalizedName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {definition};";
        command.ExecuteNonQuery();
        columns.Add(normalizedName);
    }

    private static void EnsureCreatorTrackingColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "creator_tracking")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains("evaluationmetricsjson"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking ADD COLUMN evaluation_metrics_json TEXT NOT NULL DEFAULT '{}';";
            addColumn.ExecuteNonQuery();
        }

        if (!columns.Contains("alternatename"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking ADD COLUMN alternate_name TEXT NOT NULL DEFAULT '';";
            addColumn.ExecuteNonQuery();
        }

        if (!columns.Contains("subscriptionhistoryjson"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking ADD COLUMN subscription_history_json TEXT NOT NULL DEFAULT '[]';";
            addColumn.ExecuteNonQuery();
        }

        if (!columns.Contains("purchasehistoryjson"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking ADD COLUMN purchase_history_json TEXT NOT NULL DEFAULT '[]';";
            addColumn.ExecuteNonQuery();
        }

        if (!columns.Contains("storagelocationsjson"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking ADD COLUMN storage_locations_json TEXT NOT NULL DEFAULT '[]';";
            addColumn.ExecuteNonQuery();
        }

        if (columns.Contains("supportstatus"))
        {
            MigrateCreatorTrackingPaymentStatuses(connection);
        }
        RemoveCreatorTrackingPrioritySiteFlags(connection);
        MigrateCreatorTrackingStorageLocations(connection);
    }

    private static void MigrateCreatorTrackingStorageLocations(SqliteConnection connection)
    {
        var rows = new List<(string Creator, string MainPath, string WorkPath, string UnsortedPath)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT creator, main_storage_path, work_storage_path, unsorted_storage_path
                FROM creator_tracking
                WHERE TRIM(storage_locations_json) IN ('', '[]');
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }
        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            var locations = NormalizeCreatorTrackingStorageLocations(
                null,
                row.MainPath,
                row.WorkPath,
                row.UnsortedPath);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE creator_tracking SET storage_locations_json = $json WHERE creator = $creator COLLATE NOCASE;";
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(locations, JsonOptions));
            update.Parameters.AddWithValue("$creator", row.Creator);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void RemoveCreatorTrackingPrioritySiteFlags(SqliteConnection connection)
    {
        var rows = new List<(string Creator, string Json)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT creator, activity_links_json FROM creator_tracking WHERE activity_links_json LIKE '%isPrioritySite%';";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            try
            {
                var links = JsonSerializer.Deserialize<CreatorTrackingActivityLinkDto[]>(row.Json, JsonOptions);
                if (links is null)
                {
                    continue;
                }
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE creator_tracking SET activity_links_json = $json WHERE creator = $creator COLLATE NOCASE;";
                update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(NormalizeCreatorTrackingActivityLinks(links), JsonOptions));
                update.Parameters.AddWithValue("$creator", row.Creator);
                update.ExecuteNonQuery();
            }
            catch (JsonException)
            {
                // Preserve malformed legacy JSON instead of discarding unrelated activity-place data.
            }
        }
        transaction.Commit();
    }

    private static void MigrateCreatorTrackingPaymentStatuses(SqliteConnection connection)
    {
        var rows = new List<(string Creator, string Json)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT creator, subscription_history_json FROM creator_tracking;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var row in rows)
        {
            IReadOnlyList<CreatorTrackingSubscriptionDto> subscriptions;
            try
            {
                var legacy = JsonSerializer.Deserialize<LegacyCreatorTrackingSubscription[]>(row.Json, JsonOptions) ?? [];
                subscriptions = NormalizeCreatorTrackingSubscriptions(legacy
                    .Where(item => item.Status is not "未契約" and not "検討中")
                    .Select(item => new CreatorTrackingSubscriptionDto(
                        item.Id,
                        item.Platform,
                        item.Plan,
                        item.Currency,
                        item.Amount,
                        item.BillingFrequency,
                        item.StartedOn,
                        item.RenewalOn,
                        item.EndingPlanned || item.Status is "一時停止" or "解約済み",
                        item.Reminder))
                    .ToArray());
            }
            catch (JsonException)
            {
                subscriptions = [];
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE creator_tracking SET subscription_history_json = $json WHERE creator = $creator COLLATE NOCASE;";
            update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(subscriptions, JsonOptions));
            update.Parameters.AddWithValue("$creator", row.Creator);
            update.ExecuteNonQuery();
        }

        using (var dropColumn = connection.CreateCommand())
        {
            dropColumn.Transaction = transaction;
            dropColumn.CommandText = "ALTER TABLE creator_tracking DROP COLUMN support_status;";
            dropColumn.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void EnsureCreatorTrackingSettingsColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "creator_tracking_settings")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains("compositionlabellimit"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking_settings ADD COLUMN composition_label_limit INTEGER NOT NULL DEFAULT 5;";
            addColumn.ExecuteNonQuery();
        }
        if (!columns.Contains("followpolicyoptionsjson"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking_settings ADD COLUMN follow_policy_options_json TEXT NOT NULL DEFAULT '[]';";
            addColumn.ExecuteNonQuery();
        }
        if (!columns.Contains("metricdefinitionsjson"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE creator_tracking_settings ADD COLUMN metric_definitions_json TEXT NULL;";
            addColumn.ExecuteNonQuery();
        }
    }

    private static void EnsureGallerySections(SqliteConnection connection)
    {
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM gallery_sections;";
        if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var section in DefaultGallerySections)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO gallery_sections (section_id, label, position) VALUES ($id, $label, $position);";
            insert.Parameters.AddWithValue("$id", section.Id);
            insert.Parameters.AddWithValue("$label", section.Label);
            insert.Parameters.AddWithValue("$position", section.Position);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void EnsureGalleryScanSettingsColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "gallery_scan_settings")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains("cardaspect"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE gallery_scan_settings ADD COLUMN card_aspect TEXT NOT NULL DEFAULT 'portrait';";
            addColumn.ExecuteNonQuery();
        }

        if (!columns.Contains("enabledfilters"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE gallery_scan_settings ADD COLUMN enabled_filters TEXT NOT NULL DEFAULT 'rating,creator,title,character,tag';";
            addColumn.ExecuteNonQuery();
        }

        if (!columns.Contains("filenamelines"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE gallery_scan_settings ADD COLUMN file_name_lines INTEGER NOT NULL DEFAULT 3;";
            addColumn.ExecuteNonQuery();
        }

        AddGalleryScanSettingsColumn(connection, columns, "creatorlabel", "creator_label TEXT NOT NULL DEFAULT 'Creator'");
        AddGalleryScanSettingsColumn(connection, columns, "titlelabel", "title_label TEXT NOT NULL DEFAULT 'Title'");
        AddGalleryScanSettingsColumn(connection, columns, "characterlabel", "character_label TEXT NOT NULL DEFAULT 'Character'");
        AddGalleryScanSettingsColumn(connection, columns, "taglabel", "tag_label TEXT NOT NULL DEFAULT 'Tag'");
        AddGalleryScanSettingsColumn(connection, columns, "coretitlelabel", "core_title_label TEXT NOT NULL DEFAULT 'Core title'");
        AddGalleryScanSettingsColumn(connection, columns, "coretagslabel", "core_tags_label TEXT NOT NULL DEFAULT 'Core tags'");

        foreach (var setting in DefaultGalleryScanSettings)
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT OR IGNORE INTO gallery_scan_settings (category, supported_extensions, card_aspect, enabled_filters, file_name_lines)
                VALUES ($category, $supportedExtensions, $cardAspect, $enabledFilters, $fileNameLines);
                """;
            seed.Parameters.AddWithValue("$category", setting.Category);
            seed.Parameters.AddWithValue("$supportedExtensions", setting.SupportedExtensions);
            seed.Parameters.AddWithValue("$cardAspect", setting.CardAspect);
            seed.Parameters.AddWithValue("$enabledFilters", SerializeGalleryEnabledFilters(setting.EnabledFilters));
            seed.Parameters.AddWithValue("$fileNameLines", setting.FileNameLines);
            seed.ExecuteNonQuery();
        }
    }

    private static void AddGalleryScanSettingsColumn(
        SqliteConnection connection,
        ISet<string> columns,
        string normalizedName,
        string definition)
    {
        if (columns.Contains(normalizedName))
        {
            return;
        }

        using var addColumn = connection.CreateCommand();
        addColumn.CommandText = $"ALTER TABLE gallery_scan_settings ADD COLUMN {definition};";
        addColumn.ExecuteNonQuery();
        columns.Add(normalizedName);
    }

    private static void MigrateGalleryScanEnabledFilters(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM app_migrations WHERE name = 'gallery_scan_enabled_filters_v1' LIMIT 1;";
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var setting in DefaultGalleryScanSettings)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_scan_settings SET enabled_filters = $enabledFilters WHERE category = $category;";
            update.Parameters.AddWithValue("$category", setting.Category);
            update.Parameters.AddWithValue("$enabledFilters", SerializeGalleryEnabledFilters(setting.EnabledFilters));
            update.ExecuteNonQuery();
        }

        using var markComplete = connection.CreateCommand();
        markComplete.Transaction = transaction;
        markComplete.CommandText = "INSERT INTO app_migrations (name) VALUES ('gallery_scan_enabled_filters_v1');";
        markComplete.ExecuteNonQuery();
        transaction.Commit();
    }

    private void MigrateGalleryScanCoreFilters()
    {
        using var connection = OpenConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM app_migrations WHERE name = 'gallery_scan_core_filters_v2' LIMIT 1;";
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        var currentSettings = new List<(string Category, string EnabledFilters)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT category, enabled_filters FROM gallery_scan_settings;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                currentSettings.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var setting in currentSettings)
        {
            var enabled = setting.EnabledFilters
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (!enabled.Contains("core_tags", StringComparer.OrdinalIgnoreCase))
            {
                enabled.Add("core_tags");
            }
            if (!string.Equals(setting.Category, "av", StringComparison.OrdinalIgnoreCase)
                && !enabled.Contains("core_title", StringComparer.OrdinalIgnoreCase))
            {
                enabled.Add("core_title");
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_scan_settings SET enabled_filters = $enabledFilters WHERE category = $category;";
            update.Parameters.AddWithValue("$category", setting.Category);
            update.Parameters.AddWithValue("$enabledFilters", SerializeGalleryEnabledFilters(
                NormalizeGalleryEnabledFilters(enabled, setting.Category)));
            update.ExecuteNonQuery();
        }

        using var markComplete = connection.CreateCommand();
        markComplete.Transaction = transaction;
        markComplete.CommandText = "INSERT INTO app_migrations (name) VALUES ('gallery_scan_core_filters_v2');";
        markComplete.ExecuteNonQuery();
        transaction.Commit();
        _jsonSettingsStore.SaveApplicationSettings(connection);
    }

    private static void MigrateGalleryScanCardAspects(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM app_migrations WHERE name = 'gallery_scan_card_aspects_v1' LIMIT 1;";
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        foreach (var setting in DefaultGalleryScanSettings)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_scan_settings SET card_aspect = $cardAspect WHERE category = $category;";
            update.Parameters.AddWithValue("$category", setting.Category);
            update.Parameters.AddWithValue("$cardAspect", setting.CardAspect);
            update.ExecuteNonQuery();
        }

        using var markComplete = connection.CreateCommand();
        markComplete.Transaction = transaction;
        markComplete.CommandText = "INSERT INTO app_migrations (name) VALUES ('gallery_scan_card_aspects_v1');";
        markComplete.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void EnsureThumbnailCropAdjustments(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "thumbnail_crop_adjustments")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("scalepercent"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE thumbnail_crop_adjustments ADD COLUMN scale_percent REAL NOT NULL DEFAULT 100;";
            addColumn.ExecuteNonQuery();
        }

        foreach (var setting in DefaultGalleryScanSettings)
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT OR IGNORE INTO thumbnail_crop_adjustments (category, horizontal_offset_percent, vertical_offset_percent, scale_percent)
                VALUES ($category, 0, -10, 100);
                """;
            seed.Parameters.AddWithValue("$category", setting.Category);
            seed.ExecuteNonQuery();
        }
    }

    private static void EnsureExternalAppRuleColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "external_app_rules")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!columns.Contains("name"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE external_app_rules ADD COLUMN name TEXT NOT NULL DEFAULT '';";
            command.ExecuteNonQuery();
        }

        if (!columns.Contains("targetkind"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE external_app_rules ADD COLUMN target_kind TEXT NOT NULL DEFAULT 'file';";
            command.ExecuteNonQuery();
        }

        AddExternalAppRuleColumn(connection, columns, "clickextensions", "click_extensions TEXT NOT NULL DEFAULT ''");
        AddExternalAppRuleColumn(connection, columns, "doubleclickextensions", "double_click_extensions TEXT NOT NULL DEFAULT ''");
        AddExternalAppRuleColumn(connection, columns, "contextmenuextensions", "context_menu_extensions TEXT NOT NULL DEFAULT ''");
        AddExternalAppRuleColumn(connection, columns, "allowmultiple", "allow_multiple INTEGER NOT NULL DEFAULT 0");
        AddExternalAppRuleColumn(connection, columns, "position", "position INTEGER NOT NULL DEFAULT 0");

        using var normalizeExtensions = connection.CreateCommand();
        normalizeExtensions.CommandText = """
            UPDATE external_app_rules
            SET click_extensions = REPLACE(click_extensions, '.', ''),
                double_click_extensions = REPLACE(double_click_extensions, '.', ''),
                context_menu_extensions = REPLACE(context_menu_extensions, '.', '');
            """;
        normalizeExtensions.ExecuteNonQuery();
    }

    private static void AddExternalAppRuleColumn(SqliteConnection connection, ISet<string> columns, string normalizedName, string definition)
    {
        if (columns.Contains(normalizedName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE external_app_rules ADD COLUMN {definition};";
        command.ExecuteNonQuery();
        columns.Add(normalizedName);
    }

    private static void MigrateExternalAppRulesToZipPlaStyle(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM app_migrations WHERE name = 'external_app_rules_zippla_style_v1' LIMIT 1;";
            if (check.ExecuteScalar() is not null)
            {
                return;
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE external_app_rules
            SET position = id
            WHERE position = 0;

            UPDATE external_app_rules
            SET click_extensions = LTRIM(extension, '.')
            WHERE TRIM(click_extensions) = '' AND extension NOT LIKE '__program__%';

            UPDATE external_app_rules
            SET click_extensions = REPLACE(click_extensions, '.', ''),
                double_click_extensions = REPLACE(double_click_extensions, '.', ''),
                context_menu_extensions = REPLACE(context_menu_extensions, '.', '');

            INSERT INTO app_migrations (name) VALUES ('external_app_rules_zippla_style_v1');
            """;
        command.ExecuteNonQuery();
    }

    private static void RemoveLegacyWebTabs(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "explorer_tabs")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (columns.Contains("kind"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM explorer_tabs WHERE kind = 'web';";
            command.ExecuteNonQuery();
        }
    }

    private static void EnsureExplorerBookmarkColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "explorer_bookmarks")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (columns.Contains("position"))
        {
            return;
        }

        using (var addColumn = connection.CreateCommand())
        {
            addColumn.CommandText = "ALTER TABLE explorer_bookmarks ADD COLUMN position INTEGER NOT NULL DEFAULT 0;";
            addColumn.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT path FROM explorer_bookmarks ORDER BY label COLLATE NOCASE, path COLLATE NOCASE;";
        var paths = new List<string>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE explorer_bookmarks SET position = $position WHERE path = $path;";
        var positionParameter = update.CreateParameter();
        positionParameter.ParameterName = "$position";
        update.Parameters.Add(positionParameter);
        var pathParameter = update.CreateParameter();
        pathParameter.ParameterName = "$path";
        update.Parameters.Add(pathParameter);
        for (var position = 0; position < paths.Count; position++)
        {
            positionParameter.Value = position;
            pathParameter.Value = paths[position];
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void EnsureUiNavigationStateColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "ui_navigation_state")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("explorerdetailcolumns"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE ui_navigation_state ADD COLUMN explorer_detail_columns TEXT NOT NULL DEFAULT 'icon,name,pages,gid,modified,type,size';";
            command.ExecuteNonQuery();
        }

        if (!columns.Contains("mousegesturesettings"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE ui_navigation_state ADD COLUMN mouse_gesture_settings TEXT NOT NULL DEFAULT '';";
            command.ExecuteNonQuery();
        }

        EnsureUiNavigationStateColumn(connection, columns, "gallerycardcolumns", "gallery_card_columns TEXT NOT NULL DEFAULT '{}'");
        EnsureUiNavigationStateColumn(connection, columns, "explorercardcolumns", "explorer_card_columns INTEGER NOT NULL DEFAULT 5");
        EnsureUiNavigationStateColumn(connection, columns, "windowwidth", "window_width REAL NOT NULL DEFAULT 0");
        EnsureUiNavigationStateColumn(connection, columns, "windowheight", "window_height REAL NOT NULL DEFAULT 0");
        EnsureUiNavigationStateColumn(connection, columns, "windowismaximized", "window_is_maximized INTEGER NOT NULL DEFAULT 0");
        EnsureUiNavigationStateColumn(connection, columns, "keyboardshortcutsettings", "keyboard_shortcut_settings TEXT NOT NULL DEFAULT ''");
        EnsureUiNavigationStateColumn(connection, columns, "galleryfiltersorts", "gallery_filter_sorts TEXT NOT NULL DEFAULT '[]'");
        EnsureUiNavigationStateColumn(connection, columns, "gallerythumbnailsorts", "gallery_thumbnail_sorts TEXT NOT NULL DEFAULT '[]'");
        EnsureUiNavigationStateColumn(connection, columns, "creatortrackingtabs", "creator_tracking_tabs TEXT NOT NULL DEFAULT '{\"tabs\":[],\"activeIndex\":0}'");
    }

    private static void EnsureUiNavigationStateColumn(
        SqliteConnection connection,
        ISet<string> columns,
        string normalizedName,
        string definition)
    {
        if (columns.Contains(normalizedName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE ui_navigation_state ADD COLUMN {definition};";
        command.ExecuteNonQuery();
        columns.Add(normalizedName);
    }

    private static void EnsureNewTabCandidateColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "new_tab_candidates")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (columns.Contains("kind"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE new_tab_candidates ADD COLUMN kind TEXT NOT NULL DEFAULT 'folder';";
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ExternalAppRuleDto> ListExternalAppRules()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, executable_path, arguments_template, allow_multiple, click_extensions, double_click_extensions, context_menu_extensions
            FROM external_app_rules
            ORDER BY position, id;
            """;

        using var reader = command.ExecuteReader();
        var rules = new List<ExternalAppRuleDto>();
        while (reader.Read())
        {
            rules.Add(new ExternalAppRuleDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));
        }

        return rules;
    }

    public void SaveExternalAppRule(
        long? id,
        string name,
        string executablePath,
        string launchOptions,
        bool allowMultiple,
        string clickExtensions,
        string doubleClickExtensions,
        string contextMenuExtensions)
    {
        EnsureCreated();

        name = name.Trim();
        executablePath = executablePath.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("表示名と実行ファイルを指定してください。");
        }

        launchOptions = string.IsNullOrWhiteSpace(launchOptions) ? "\"{path}\"" : launchOptions.Trim();
        clickExtensions = NormalizeProgramExtensionList(clickExtensions);
        doubleClickExtensions = NormalizeProgramExtensionList(doubleClickExtensions);
        contextMenuExtensions = NormalizeProgramExtensionList(contextMenuExtensions);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = id is long existingId
            ? """
                UPDATE external_app_rules
                SET name = $name,
                    executable_path = $executable_path,
                    arguments_template = $launch_options,
                    allow_multiple = $allow_multiple,
                    click_extensions = $click_extensions,
                    double_click_extensions = $double_click_extensions,
                    context_menu_extensions = $context_menu_extensions,
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = $id;
                """
            : """
                INSERT INTO external_app_rules (name, target_kind, extension, executable_path, arguments_template, allow_multiple, click_extensions, double_click_extensions, context_menu_extensions, position)
                VALUES ($name, 'file', $extension, $executable_path, $launch_options, $allow_multiple, $click_extensions, $double_click_extensions, $context_menu_extensions,
                    (SELECT COALESCE(MAX(position), -1) + 1 FROM external_app_rules));
                """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$executable_path", executablePath);
        command.Parameters.AddWithValue("$launch_options", launchOptions);
        command.Parameters.AddWithValue("$allow_multiple", allowMultiple ? 1 : 0);
        command.Parameters.AddWithValue("$click_extensions", clickExtensions);
        command.Parameters.AddWithValue("$double_click_extensions", doubleClickExtensions);
        command.Parameters.AddWithValue("$context_menu_extensions", contextMenuExtensions);
        if (id is long ruleId)
        {
            command.Parameters.AddWithValue("$id", ruleId);
        }
        else
        {
            command.Parameters.AddWithValue("$extension", "__program__" + Guid.NewGuid().ToString("N"));
        }
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public void DeleteExternalAppRule(long id)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM external_app_rules WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public void SaveExternalAppRuleOrder(IReadOnlyList<long> ids)
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE external_app_rules SET position = $position WHERE id = $id;";
        var position = command.CreateParameter();
        position.ParameterName = "$position";
        command.Parameters.Add(position);
        var id = command.CreateParameter();
        id.ParameterName = "$id";
        command.Parameters.Add(id);

        var nextPosition = 0;
        foreach (var ruleId in ids.Distinct())
        {
            position.Value = nextPosition++;
            id.Value = ruleId;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        PersistApplicationSettings();
    }

    public ExternalAppRuleDto? FindExternalAppRule(string path, ExternalAppActivation activation = ExternalAppActivation.SingleClick)
    {
        EnsureCreated();

        var extension = NormalizeExtension(System.IO.Path.GetExtension(path));
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        return ListExternalAppRules().FirstOrDefault(rule => ExtensionListContains(
            activation == ExternalAppActivation.DoubleClick ? rule.DoubleClickExtensions : rule.ClickExtensions,
            extension));
    }

    public ExternalAppRuleDto? FindExternalAppRule(long id) =>
        ListExternalAppRules().FirstOrDefault(rule => rule.Id == id);

    private static string NormalizeProgramExtensionList(string extensions) =>
        string.Join(",", extensions.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(extension => extension.Trim().TrimStart('.').ToLowerInvariant())
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string NormalizeExtensionList(string extensions) =>
        string.Join(",", extensions.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeExtension)
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static bool ExtensionListContains(string extensions, string extension) =>
        extensions.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(candidate => string.Equals(NormalizeExtension(candidate), extension, StringComparison.OrdinalIgnoreCase));

    public WinRarSettingsDto GetWinRarSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT executable_path, supported_extensions, show_open_in_context_menu FROM winrar_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new WinRarSettingsDto(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0)
            : new WinRarSettingsDto(string.Empty, ".zip,.rar,.7z,.cbz,.cbr", false);
    }

    public void SaveWinRarSettings(string executablePath, string supportedExtensions, bool showOpenInContextMenu)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO winrar_settings (id, executable_path, supported_extensions, show_open_in_context_menu)
            VALUES (1, $executablePath, $supportedExtensions, $showOpenInContextMenu)
            ON CONFLICT(id) DO UPDATE SET
                executable_path = excluded.executable_path,
                supported_extensions = excluded.supported_extensions,
                show_open_in_context_menu = excluded.show_open_in_context_menu,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$executablePath", executablePath.Trim());
        command.Parameters.AddWithValue("$supportedExtensions", NormalizeExtensionList(supportedExtensions));
        command.Parameters.AddWithValue("$showOpenInContextMenu", showOpenInContextMenu ? 1 : 0);
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public FfmpegSettingsDto GetFfmpegSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT executable_path, supported_extensions FROM ffmpeg_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new FfmpegSettingsDto(reader.GetString(0), reader.GetString(1))
            : new FfmpegSettingsDto(string.Empty, FfmpegService.DefaultSupportedExtensions);
    }

    public void SaveFfmpegSettings(string executablePath, string supportedExtensions)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ffmpeg_settings (id, executable_path, supported_extensions)
            VALUES (1, $executablePath, $supportedExtensions)
            ON CONFLICT(id) DO UPDATE SET
                executable_path = excluded.executable_path,
                supported_extensions = excluded.supported_extensions,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$executablePath", executablePath.Trim());
        command.Parameters.AddWithValue("$supportedExtensions", NormalizeExtensionList(supportedExtensions));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public NConvertSettingsDto GetNConvertSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT executable_path, temporary_directory FROM nconvert_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new NConvertSettingsDto(reader.GetString(0), reader.GetString(1))
            : new NConvertSettingsDto(string.Empty, string.Empty);
    }

    public void SaveNConvertSettings(string executablePath, string temporaryDirectory)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO nconvert_settings (id, executable_path, temporary_directory)
            VALUES (1, $executablePath, $temporaryDirectory)
            ON CONFLICT(id) DO UPDATE SET
                executable_path = excluded.executable_path,
                temporary_directory = excluded.temporary_directory,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$executablePath", executablePath.Trim());
        command.Parameters.AddWithValue("$temporaryDirectory", temporaryDirectory.Trim());
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public IReadOnlyList<string> ListThumbnailCacheTargets()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM thumbnail_cache_targets ORDER BY position, path COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var targets = new List<string>();
        while (reader.Read())
        {
            targets.Add(reader.GetString(0));
        }

        return targets;
    }

    public void SaveThumbnailCacheTargets(IReadOnlyList<string> paths)
    {
        EnsureCreated();
        var targets = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM thumbnail_cache_targets;";
            clear.ExecuteNonQuery();
        }

        for (var index = 0; index < targets.Length; index++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO thumbnail_cache_targets (path, position) VALUES ($path, $position);";
            insert.Parameters.AddWithValue("$path", targets[index]);
            insert.Parameters.AddWithValue("$position", index);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        PersistApplicationSettings();
    }

    public IReadOnlyList<ThumbnailCacheEntryDto> ListThumbnailCacheEntries()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_path, source_path, source_stamp FROM thumbnail_cache_entries;";
        using var reader = command.ExecuteReader();
        var entries = new List<ThumbnailCacheEntryDto>();
        while (reader.Read())
        {
            entries.Add(new ThumbnailCacheEntryDto(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return entries;
    }

    public void UpsertThumbnailCacheEntry(string cachePath, string sourcePath, string sourceStamp)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thumbnail_cache_entries (cache_path, source_path, source_stamp)
            VALUES ($cachePath, $sourcePath, $sourceStamp)
            ON CONFLICT(cache_path) DO UPDATE SET
                source_path = excluded.source_path,
                source_stamp = excluded.source_stamp,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$cachePath", cachePath);
        command.Parameters.AddWithValue("$sourcePath", sourcePath);
        command.Parameters.AddWithValue("$sourceStamp", sourceStamp);
        command.ExecuteNonQuery();
    }

    public void DeleteThumbnailCacheEntries(IEnumerable<string> cachePaths)
    {
        var paths = cachePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        EnsureCreated();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM thumbnail_cache_entries WHERE cache_path = $cachePath;";
        var cachePath = command.CreateParameter();
        cachePath.ParameterName = "$cachePath";
        command.Parameters.Add(cachePath);
        foreach (var path in paths)
        {
            cachePath.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void ClearThumbnailCacheEntries()
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM thumbnail_cache_entries;";
        command.ExecuteNonQuery();
    }

    public string GetThumbnailCacheRoot()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_root FROM thumbnail_cache_settings WHERE id = 1;";
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    public void SaveThumbnailCacheRoot(string cacheRoot)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thumbnail_cache_settings (id, cache_root)
            VALUES (1, $cacheRoot)
            ON CONFLICT(id) DO UPDATE SET
                cache_root = excluded.cache_root,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$cacheRoot", cacheRoot.Trim());
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public void UpdateThumbnailCacheEntryPaths(IReadOnlyDictionary<string, string> movedPaths)
    {
        if (movedPaths.Count == 0)
        {
            return;
        }

        EnsureCreated();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE thumbnail_cache_entries SET cache_path = $newPath WHERE cache_path = $oldPath;";
        var oldPath = command.CreateParameter();
        oldPath.ParameterName = "$oldPath";
        command.Parameters.Add(oldPath);
        var newPath = command.CreateParameter();
        newPath.ParameterName = "$newPath";
        command.Parameters.Add(newPath);
        foreach (var (oldValue, newValue) in movedPaths)
        {
            oldPath.Value = oldValue;
            newPath.Value = newValue;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<ExplorerBookmarkDto> ListExplorerBookmarks()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, label FROM explorer_bookmarks ORDER BY position, path COLLATE NOCASE;";

        using var reader = command.ExecuteReader();
        var bookmarks = new List<ExplorerBookmarkDto>();
        while (reader.Read())
        {
            bookmarks.Add(new ExplorerBookmarkDto(reader.GetString(0), reader.GetString(1)));
        }

        return bookmarks;
    }

    public void SaveExplorerBookmark(string path, string label)
    {
        EnsureCreated();

        path = Path.GetFullPath(path);
        label = string.IsNullOrWhiteSpace(label)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : label.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = path;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO explorer_bookmarks (path, label, position)
            VALUES ($path, $label, (SELECT COALESCE(MAX(position), -1) + 1 FROM explorer_bookmarks))
            ON CONFLICT(path) DO UPDATE SET
                label = excluded.label,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$label", label);
        command.ExecuteNonQuery();
        PersistUiState();
    }

    public void SaveExplorerBookmarkOrder(IReadOnlyList<string> paths)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE explorer_bookmarks SET position = $position WHERE path = $path;";
        var positionParameter = command.CreateParameter();
        positionParameter.ParameterName = "$position";
        command.Parameters.Add(positionParameter);
        var pathParameter = command.CreateParameter();
        pathParameter.ParameterName = "$path";
        command.Parameters.Add(pathParameter);

        var position = 0;
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path))
                                  .Select(Path.GetFullPath)
                                  .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            positionParameter.Value = position++;
            pathParameter.Value = path;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        PersistUiState();
    }

    public void DeleteExplorerBookmark(string path)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM explorer_bookmarks WHERE path = $path;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.ExecuteNonQuery();
        PersistUiState();
    }

    public IReadOnlyList<ViewBookmarkDto> ListViewBookmarks()
    {
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bookmark_id, name, view_type, state_json, thumbnail_data_url,
                   window_width, window_height, window_is_maximized,
                   position, created_at, updated_at
            FROM view_bookmarks
            ORDER BY position, bookmark_id;
            """;

        using var reader = command.ExecuteReader();
        var bookmarks = new List<ViewBookmarkDto>();
        while (reader.Read())
        {
            bookmarks.Add(new ViewBookmarkDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetInt32(7) != 0,
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return bookmarks;
    }

    public void SaveViewBookmark(
        long? id,
        string name,
        string viewType,
        string stateJson,
        string thumbnailDataUrl,
        double windowWidth,
        double windowHeight,
        bool windowIsMaximized)
    {
        name = name.Trim();
        viewType = viewType.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Bookmark名を入力してください。");
        }
        if (viewType.Length == 0 || stateJson.Length == 0)
        {
            throw new InvalidOperationException("保存するビューの状態を取得できませんでした。");
        }

        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        if (id is > 0)
        {
            command.CommandText = """
                UPDATE view_bookmarks
                SET name = $name,
                    view_type = $viewType,
                    state_json = $stateJson,
                    thumbnail_data_url = $thumbnailDataUrl,
                    window_width = $windowWidth,
                    window_height = $windowHeight,
                    window_is_maximized = $windowIsMaximized,
                    updated_at = CURRENT_TIMESTAMP
                WHERE bookmark_id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.Value);
        }
        else
        {
            command.CommandText = """
                INSERT INTO view_bookmarks (
                    name, view_type, state_json, thumbnail_data_url,
                    window_width, window_height, window_is_maximized, position)
                VALUES ($name, $viewType, $stateJson, $thumbnailDataUrl,
                    $windowWidth, $windowHeight, $windowIsMaximized,
                    (SELECT COALESCE(MAX(position), -1) + 1 FROM view_bookmarks));
                """;
        }
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$viewType", viewType);
        command.Parameters.AddWithValue("$stateJson", stateJson);
        command.Parameters.AddWithValue("$thumbnailDataUrl", thumbnailDataUrl ?? string.Empty);
        command.Parameters.AddWithValue("$windowWidth", windowWidth);
        command.Parameters.AddWithValue("$windowHeight", windowHeight);
        command.Parameters.AddWithValue("$windowIsMaximized", windowIsMaximized ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void DeleteViewBookmark(long id)
    {
        using var connection = OpenApplicationDataConnection();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM view_bookmarks WHERE bookmark_id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }
        using (var reorder = connection.CreateCommand())
        {
            reorder.Transaction = transaction;
            reorder.CommandText = """
                WITH ordered AS (
                    SELECT bookmark_id, ROW_NUMBER() OVER (ORDER BY position, bookmark_id) - 1 AS next_position
                    FROM view_bookmarks
                )
                UPDATE view_bookmarks
                SET position = (SELECT next_position FROM ordered WHERE ordered.bookmark_id = view_bookmarks.bookmark_id);
                """;
            reorder.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<StickyNoteDto> ListStickyNotes()
    {
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT note_id, view_type, context_key, context_label, content, position_x, position_y,
                   width, height, color_key, content_mode, created_at, updated_at
            FROM sticky_notes
            ORDER BY note_id;
            """;

        using var reader = command.ExecuteReader();
        var notes = new List<StickyNoteDto>();
        while (reader.Read())
        {
            notes.Add(ReadStickyNote(reader));
        }

        return notes;
    }

    public StickyNoteDto CreateStickyNote(
        string viewType,
        string contextKey,
        string contextLabel,
        string colorKey,
        string contentMode,
        double x,
        double y,
        double width,
        double height)
    {
        viewType = NormalizeStickyNoteViewType(viewType);
        contextKey = NormalizeStickyNoteContextKey(viewType, contextKey);
        contextLabel = NormalizeStickyNoteContextLabel(contextLabel, contextKey);
        colorKey = NormalizeStickyNoteColorKey(colorKey);
        contentMode = NormalizeStickyNoteContentMode(contentMode);
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        width = Math.Clamp(width, 125, 1200);
        height = Math.Clamp(height, 100, 900);
        var timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sticky_notes (
                view_type, context_key, context_label, content, position_x, position_y, width, height,
                color_key, content_mode, created_at, updated_at)
            VALUES ($viewType, $contextKey, $contextLabel, '', $x, $y, $width, $height, $colorKey, $contentMode, $timestamp, $timestamp)
            RETURNING note_id;
            """;
        command.Parameters.AddWithValue("$viewType", viewType);
        command.Parameters.AddWithValue("$contextKey", contextKey);
        command.Parameters.AddWithValue("$contextLabel", contextLabel);
        command.Parameters.AddWithValue("$colorKey", colorKey);
        command.Parameters.AddWithValue("$contentMode", contentMode);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$timestamp", timestamp);
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new StickyNoteDto(id, viewType, contextKey, contextLabel, string.Empty, x, y, width, height, colorKey, contentMode, timestamp, timestamp);
    }

    public void SaveStickyNote(StickyNoteDto note)
    {
        if (note.Id <= 0)
        {
            throw new InvalidOperationException("保存する付箋を特定できませんでした。");
        }

        var viewType = NormalizeStickyNoteViewType(note.ViewType);
        var contextKey = NormalizeStickyNoteContextKey(viewType, note.ContextKey);
        var contextLabel = NormalizeStickyNoteContextLabel(note.ContextLabel, contextKey);
        var colorKey = NormalizeStickyNoteColorKey(note.ColorKey);
        var contentMode = NormalizeStickyNoteContentMode(note.ContentMode);
        var content = note.Content ?? string.Empty;
        if (content.Length > 100_000)
        {
            content = content[..100_000];
        }

        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sticky_notes
            SET view_type = $viewType,
                context_key = $contextKey,
                context_label = $contextLabel,
                content = $content,
                position_x = $x,
                position_y = $y,
                width = $width,
                height = $height,
                color_key = $colorKey,
                content_mode = $contentMode,
                updated_at = $updatedAt
            WHERE note_id = $id;
            """;
        command.Parameters.AddWithValue("$id", note.Id);
        command.Parameters.AddWithValue("$viewType", viewType);
        command.Parameters.AddWithValue("$contextKey", contextKey);
        command.Parameters.AddWithValue("$contextLabel", contextLabel);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$x", Math.Max(0, note.X));
        command.Parameters.AddWithValue("$y", Math.Max(0, note.Y));
        command.Parameters.AddWithValue("$width", Math.Clamp(note.Width, 125, 1200));
        command.Parameters.AddWithValue("$height", Math.Clamp(note.Height, 100, 900));
        command.Parameters.AddWithValue("$colorKey", colorKey);
        command.Parameters.AddWithValue("$contentMode", contentMode);
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void RestoreStickyNotes(IReadOnlyList<StickyNoteDto> notes)
    {
        using var connection = OpenApplicationDataConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var note in notes.Where(candidate => candidate.Id > 0))
        {
            var viewType = NormalizeStickyNoteViewType(note.ViewType);
            var contextKey = NormalizeStickyNoteContextKey(viewType, note.ContextKey);
            var contextLabel = NormalizeStickyNoteContextLabel(note.ContextLabel, contextKey);
            var colorKey = NormalizeStickyNoteColorKey(note.ColorKey);
            var contentMode = NormalizeStickyNoteContentMode(note.ContentMode);
            var content = note.Content ?? string.Empty;
            if (content.Length > 100_000)
            {
                content = content[..100_000];
            }
            var timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sticky_notes (
                    note_id, view_type, context_key, context_label, content, position_x, position_y,
                    width, height, color_key, content_mode, created_at, updated_at)
                VALUES (
                    $id, $viewType, $contextKey, $contextLabel, $content, $x, $y,
                    $width, $height, $colorKey, $contentMode, $createdAt, $updatedAt)
                ON CONFLICT(note_id) DO UPDATE SET
                    view_type = excluded.view_type,
                    context_key = excluded.context_key,
                    context_label = excluded.context_label,
                    content = excluded.content,
                    position_x = excluded.position_x,
                    position_y = excluded.position_y,
                    width = excluded.width,
                    height = excluded.height,
                    color_key = excluded.color_key,
                    content_mode = excluded.content_mode,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", note.Id);
            command.Parameters.AddWithValue("$viewType", viewType);
            command.Parameters.AddWithValue("$contextKey", contextKey);
            command.Parameters.AddWithValue("$contextLabel", contextLabel);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$x", Math.Max(0, note.X));
            command.Parameters.AddWithValue("$y", Math.Max(0, note.Y));
            command.Parameters.AddWithValue("$width", Math.Clamp(note.Width, 125, 1200));
            command.Parameters.AddWithValue("$height", Math.Clamp(note.Height, 100, 900));
            command.Parameters.AddWithValue("$colorKey", colorKey);
            command.Parameters.AddWithValue("$contentMode", contentMode);
            command.Parameters.AddWithValue("$createdAt", string.IsNullOrWhiteSpace(note.CreatedAt) ? timestamp : note.CreatedAt);
            command.Parameters.AddWithValue("$updatedAt", timestamp);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteStickyNote(long id)
    {
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sticky_notes WHERE note_id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<StickyNoteBoardItemDto> ListStickyNoteBoardItems()
    {
        var liveNotes = ListStickyNotes().ToDictionary(note => note.Id);
        var bookmarkNotes = new Dictionary<long, StickyNoteDto>();
        var bookmarkCounts = new Dictionary<long, int>();
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM view_bookmarks ORDER BY updated_at, bookmark_id;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!TryReadStickyNoteSnapshots(reader.GetString(0), out var snapshots))
            {
                continue;
            }
            foreach (var note in snapshots.GroupBy(snapshot => snapshot.Id).Select(group => group.Last()))
            {
                bookmarkNotes[note.Id] = note;
                bookmarkCounts[note.Id] = bookmarkCounts.GetValueOrDefault(note.Id) + 1;
            }
        }

        return liveNotes.Keys
            .Concat(bookmarkNotes.Keys)
            .Distinct()
            .Select(id => new StickyNoteBoardItemDto(
                liveNotes.TryGetValue(id, out var liveNote) ? liveNote : bookmarkNotes[id],
                liveNotes.ContainsKey(id),
                bookmarkCounts.GetValueOrDefault(id)))
            .OrderBy(item => item.Note.CreatedAt, StringComparer.Ordinal)
            .ThenBy(item => item.Note.Id)
            .ToArray();
    }

    public void SaveStickyNoteFromBoard(StickyNoteDto note)
    {
        var normalized = NormalizeStickyNoteSnapshot(note);
        using var connection = OpenApplicationDataConnection();
        using var transaction = connection.BeginTransaction();
        using (var updateLive = connection.CreateCommand())
        {
            updateLive.Transaction = transaction;
            updateLive.CommandText = """
                UPDATE sticky_notes
                SET content = $content,
                    color_key = $colorKey,
                    content_mode = $contentMode,
                    updated_at = $updatedAt
                WHERE note_id = $id;
                """;
            updateLive.Parameters.AddWithValue("$id", normalized.Id);
            updateLive.Parameters.AddWithValue("$content", normalized.Content);
            updateLive.Parameters.AddWithValue("$colorKey", normalized.ColorKey);
            updateLive.Parameters.AddWithValue("$contentMode", normalized.ContentMode);
            updateLive.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            updateLive.ExecuteNonQuery();
        }
        UpdateBookmarkStickyNoteSnapshots(connection, transaction, normalized, delete: false);
        transaction.Commit();
    }

    public void DeleteStickyNoteFromBoard(long id)
    {
        using var connection = OpenApplicationDataConnection();
        using var transaction = connection.BeginTransaction();
        using (var deleteLive = connection.CreateCommand())
        {
            deleteLive.Transaction = transaction;
            deleteLive.CommandText = "DELETE FROM sticky_notes WHERE note_id = $id;";
            deleteLive.Parameters.AddWithValue("$id", id);
            deleteLive.ExecuteNonQuery();
        }
        UpdateBookmarkStickyNoteSnapshots(connection, transaction, new StickyNoteDto(
            id, "library", "placeholder", "placeholder", string.Empty,
            0, 0, 250, 200, "amber", "plain", string.Empty, string.Empty), delete: true);
        transaction.Commit();
    }

    private static void UpdateBookmarkStickyNoteSnapshots(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StickyNoteDto note,
        bool delete)
    {
        var bookmarkRows = new List<(long Id, string StateJson)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT bookmark_id, state_json FROM view_bookmarks;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                bookmarkRows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        foreach (var bookmark in bookmarkRows)
        {
            if (JsonNode.Parse(bookmark.StateJson) is not JsonObject root || root["stickyNotes"] is not JsonArray notes)
            {
                continue;
            }
            var changed = false;
            for (var index = notes.Count - 1; index >= 0; index--)
            {
                if (!TryGetStickyNoteSnapshotId(notes[index], out var snapshotId) || snapshotId != note.Id)
                {
                    continue;
                }
                if (delete)
                {
                    notes.RemoveAt(index);
                }
                else if (notes[index] is JsonObject snapshot)
                {
                    snapshot["content"] = note.Content;
                    snapshot["colorKey"] = note.ColorKey;
                    snapshot["contentMode"] = note.ContentMode;
                    snapshot["updatedAt"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                }
                changed = true;
            }
            if (!changed)
            {
                continue;
            }
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE view_bookmarks SET state_json = $stateJson, updated_at = CURRENT_TIMESTAMP WHERE bookmark_id = $id;";
            update.Parameters.AddWithValue("$id", bookmark.Id);
            update.Parameters.AddWithValue("$stateJson", root.ToJsonString(JsonOptions));
            update.ExecuteNonQuery();
        }
    }

    private static bool TryReadStickyNoteSnapshots(string stateJson, out IReadOnlyList<StickyNoteDto> snapshots)
    {
        snapshots = [];
        try
        {
            if (JsonNode.Parse(stateJson) is not JsonObject root || root["stickyNotes"] is not JsonArray notes)
            {
                return false;
            }
            snapshots = notes
                .Select(node => node?.Deserialize<StickyNoteDto>(JsonOptions))
                .Where(note => note is not null && note.Id > 0)
                .Select(note => NormalizeStickyNoteSnapshot(note!))
                .ToArray();
            return snapshots.Count > 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetStickyNoteSnapshotId(JsonNode? node, out long id)
    {
        id = 0;
        return node is JsonObject snapshot &&
               snapshot["id"] is JsonValue value &&
               value.TryGetValue(out id);
    }

    private static StickyNoteDto NormalizeStickyNoteSnapshot(StickyNoteDto note)
    {
        var viewType = NormalizeStickyNoteViewType(note.ViewType);
        var contextKey = NormalizeStickyNoteContextKey(viewType, note.ContextKey);
        var content = note.Content ?? string.Empty;
        if (content.Length > 100_000)
        {
            content = content[..100_000];
        }
        return note with
        {
            ViewType = viewType,
            ContextKey = contextKey,
            ContextLabel = NormalizeStickyNoteContextLabel(note.ContextLabel, contextKey),
            Content = content,
            X = Math.Max(0, note.X),
            Y = Math.Max(0, note.Y),
            Width = Math.Clamp(note.Width <= 0 ? 250 : note.Width, 125, 1200),
            Height = Math.Clamp(note.Height <= 0 ? 200 : note.Height, 100, 900),
            ColorKey = NormalizeStickyNoteColorKey(note.ColorKey),
            ContentMode = NormalizeStickyNoteContentMode(note.ContentMode)
        };
    }

    private static StickyNoteDto ReadStickyNote(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDouble(5),
            reader.GetDouble(6),
            reader.GetDouble(7),
            reader.GetDouble(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12));

    private static string NormalizeStickyNoteViewType(string viewType) =>
        viewType.Trim() switch
        {
            "library" => "library",
            "explorer" => "explorer",
            "creators" => "creators",
            "creatorTracking" => "creatorTracking",
            "userMetrics" => "userMetrics",
            _ => throw new InvalidOperationException("付箋を作成できない画面です。")
        };

    private static string NormalizeStickyNoteContextKey(string viewType, string contextKey)
    {
        var normalized = (contextKey ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("付箋の対象を特定できませんでした。");
        }
        if (viewType == "explorer")
        {
            normalized = normalized.Replace('/', '\\').TrimEnd('\\');
        }
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeStickyNoteColorKey(string colorKey) =>
        (colorKey ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "lime" => "lime",
            "sky" => "sky",
            "teal" => "teal",
            "violet" => "violet",
            "coral" => "coral",
            _ => "amber"
        };

    private static string NormalizeStickyNoteContextLabel(string contextLabel, string contextKey) =>
        string.IsNullOrWhiteSpace(contextLabel) ? contextKey : contextLabel.Trim();

    private static string NormalizeStickyNoteContentMode(string contentMode) =>
        string.Equals(contentMode?.Trim(), "markdown", StringComparison.OrdinalIgnoreCase) ? "markdown" : "plain";

    public IReadOnlyList<NewTabCandidateDto> ListNewTabCandidates()
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, label, position, kind FROM new_tab_candidates ORDER BY position, path COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var candidates = new List<NewTabCandidateDto>();
        while (reader.Read())
        {
            candidates.Add(new NewTabCandidateDto(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return candidates;
    }

    public void SaveNewTabCandidate(string path, string label)
    {
        EnsureCreated();
        var normalizedPath = Path.GetFullPath(path);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException("フォルダが見つかりません。: " + normalizedPath);
        }

        var normalizedLabel = string.IsNullOrWhiteSpace(label)
            ? Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : label.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            normalizedLabel = normalizedPath;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO new_tab_candidates (path, label, position, kind)
            VALUES ($path, $label, (SELECT COALESCE(MAX(position), -1) + 1 FROM new_tab_candidates), 'folder')
            ON CONFLICT(path) DO UPDATE SET
                label = excluded.label,
                kind = 'folder',
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$path", normalizedPath);
        command.Parameters.AddWithValue("$label", normalizedLabel);
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public void DeleteNewTabCandidate(string path)
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM new_tab_candidates WHERE path = $path;";
        command.Parameters.AddWithValue("$path", NormalizeNewTabCandidateKey(path));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public void AddNewTabSeparator()
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO new_tab_candidates (path, label, position, kind)
            VALUES ($path, '', (SELECT COALESCE(MAX(position), -1) + 1 FROM new_tab_candidates), 'separator');
            """;
        command.Parameters.AddWithValue("$path", "__separator__" + Guid.NewGuid().ToString("N"));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public void SaveNewTabCandidateOrder(IReadOnlyList<string> paths)
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE new_tab_candidates SET position = $position WHERE path = $path;";
        var position = command.CreateParameter();
        position.ParameterName = "$position";
        command.Parameters.Add(position);
        var path = command.CreateParameter();
        path.ParameterName = "$path";
        command.Parameters.Add(path);

        var currentPosition = 0;
        foreach (var candidatePath in paths.Where(value => !string.IsNullOrWhiteSpace(value))
                                           .Select(NormalizeNewTabCandidateKey)
                                           .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            position.Value = currentPosition++;
            path.Value = candidatePath;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        PersistApplicationSettings();
    }

    private static string NormalizeNewTabCandidateKey(string value) =>
        value.StartsWith("__separator__", StringComparison.Ordinal)
            ? value
            : Path.GetFullPath(value);

    public void SaveFileSearchMetadata(IEnumerable<FileSearchMetadataDto> metadata)
    {
        var entries = metadata
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        EnsureCreated();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO file_metadata (path, file_name, romanized_name)
            VALUES ($path, $fileName, $romanizedName)
            ON CONFLICT(path) DO UPDATE SET
                file_name = excluded.file_name,
                romanized_name = excluded.romanized_name,
                updated_at = CURRENT_TIMESTAMP;
            """;
        var path = command.CreateParameter();
        path.ParameterName = "$path";
        command.Parameters.Add(path);
        var fileName = command.CreateParameter();
        fileName.ParameterName = "$fileName";
        command.Parameters.Add(fileName);
        var romanizedName = command.CreateParameter();
        romanizedName.ParameterName = "$romanizedName";
        command.Parameters.Add(romanizedName);

        foreach (var entry in entries)
        {
            path.Value = entry.Path;
            fileName.Value = entry.FileName;
            romanizedName.Value = entry.RomanizedName;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public UiNavigationStateDto GetUiNavigationState()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT active_view, explorer_bookmarks_expanded, explorer_detail_columns, mouse_gesture_settings, gallery_card_columns, explorer_card_columns, window_width, window_height, window_is_maximized, keyboard_shortcut_settings, gallery_filter_sorts, gallery_thumbnail_sorts, creator_tracking_tabs FROM ui_navigation_state WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new UiNavigationStateDto(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetInt64(8) != 0,
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12))
            : new UiNavigationStateDto("library", true, "icon,name,pages,gid,modified,type,size", string.Empty, "{}", 5, 0, 0, false, string.Empty, "[]", "[]", "{\"tabs\":[],\"activeIndex\":0}");
    }

    public void SaveUiNavigationState(
        string activeView,
        bool explorerBookmarksExpanded,
        string explorerDetailColumns,
        string mouseGestureSettings,
        string galleryCardColumns,
        int explorerCardColumns,
        string keyboardShortcutSettings,
        string galleryFilterSorts,
        string galleryThumbnailSorts,
        string creatorTrackingTabs)
    {
        EnsureCreated();

        var normalizedView = activeView is "library" or "bookmarks" or "creators" or "creatorTracking" or "explorer" or "filters" or "tags" or "userMetrics" or "board" or "calendar" or "settings" or "userGuide"
            ? activeView
            : "library";

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ui_navigation_state (id, active_view, explorer_bookmarks_expanded, explorer_detail_columns, mouse_gesture_settings, gallery_card_columns, explorer_card_columns, keyboard_shortcut_settings, gallery_filter_sorts, gallery_thumbnail_sorts, creator_tracking_tabs)
            VALUES (1, $activeView, $bookmarksExpanded, $detailColumns, $mouseGestureSettings, $galleryCardColumns, $explorerCardColumns, $keyboardShortcutSettings, $galleryFilterSorts, $galleryThumbnailSorts, $creatorTrackingTabs)
            ON CONFLICT(id) DO UPDATE SET
                active_view = excluded.active_view,
                explorer_bookmarks_expanded = excluded.explorer_bookmarks_expanded,
                explorer_detail_columns = excluded.explorer_detail_columns,
                mouse_gesture_settings = excluded.mouse_gesture_settings,
                gallery_card_columns = excluded.gallery_card_columns,
                explorer_card_columns = excluded.explorer_card_columns,
                keyboard_shortcut_settings = excluded.keyboard_shortcut_settings,
                gallery_filter_sorts = excluded.gallery_filter_sorts,
                gallery_thumbnail_sorts = excluded.gallery_thumbnail_sorts,
                creator_tracking_tabs = excluded.creator_tracking_tabs;
            """;
        command.Parameters.AddWithValue("$activeView", normalizedView);
        command.Parameters.AddWithValue("$bookmarksExpanded", explorerBookmarksExpanded ? 1 : 0);
        command.Parameters.AddWithValue("$detailColumns", string.IsNullOrWhiteSpace(explorerDetailColumns)
            ? "icon,name,pages,gid,modified,type,size"
            : explorerDetailColumns);
        command.Parameters.AddWithValue("$mouseGestureSettings", mouseGestureSettings ?? string.Empty);
        command.Parameters.AddWithValue("$galleryCardColumns", string.IsNullOrWhiteSpace(galleryCardColumns) ? "{}" : galleryCardColumns);
        command.Parameters.AddWithValue("$explorerCardColumns", Math.Clamp(explorerCardColumns, 4, 7));
        command.Parameters.AddWithValue("$keyboardShortcutSettings", keyboardShortcutSettings ?? string.Empty);
        command.Parameters.AddWithValue("$galleryFilterSorts", string.IsNullOrWhiteSpace(galleryFilterSorts) ? "[]" : galleryFilterSorts);
        command.Parameters.AddWithValue("$galleryThumbnailSorts", string.IsNullOrWhiteSpace(galleryThumbnailSorts) ? "[]" : galleryThumbnailSorts);
        command.Parameters.AddWithValue("$creatorTrackingTabs", string.IsNullOrWhiteSpace(creatorTrackingTabs) ? "{\"tabs\":[],\"activeIndex\":0}" : creatorTrackingTabs);
        command.ExecuteNonQuery();
        PersistUiState();
    }

    public SearchEngineSettingsDto GetSearchEngineSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider, google_search_url_template, brave_api_key, gemini_api_key FROM search_engine_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SearchEngineSettingsDto(
                NormalizeSearchEngineProvider(reader.GetString(0)),
                NormalizeGoogleSearchUrlTemplate(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3))
            : new SearchEngineSettingsDto("google", "https://www.google.com/search?q={query}", string.Empty, string.Empty);
    }

    public ThemeSettingsDto GetThemeSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT theme, main_color, sub_color FROM theme_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ThemeSettingsDto(
                NormalizeColorTheme(reader.GetString(0)),
                NormalizeThemeAccent(reader.GetString(1), "#caff19"),
                NormalizeThemeAccent(reader.GetString(2), "#ffb342"))
            : new ThemeSettingsDto("dark", "#caff19", "#ffb342");
    }

    public void SaveThemeSettings(string theme, string mainColor, string subColor)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO theme_settings (id, theme, main_color, sub_color)
            VALUES (1, $theme, $mainColor, $subColor)
            ON CONFLICT(id) DO UPDATE SET
                theme = excluded.theme,
                main_color = excluded.main_color,
                sub_color = excluded.sub_color,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$theme", NormalizeColorTheme(theme));
        command.Parameters.AddWithValue("$mainColor", NormalizeThemeAccent(mainColor, "#caff19"));
        command.Parameters.AddWithValue("$subColor", NormalizeThemeAccent(subColor, "#ffb342"));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public LanguageSettingsDto GetLanguageSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT language FROM language_settings WHERE id = 1;";
        var language = command.ExecuteScalar() as string;
        return new LanguageSettingsDto(NormalizeLanguage(language));
    }

    public void SaveLanguageSettings(string language)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO language_settings (id, language)
            VALUES (1, $language)
            ON CONFLICT(id) DO UPDATE SET
                language = excluded.language,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$language", NormalizeLanguage(language));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public CalendarSettingsDto GetCalendarSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT week_start_day FROM calendar_settings WHERE id = 1;";
        var value = command.ExecuteScalar();
        return new CalendarSettingsDto(NormalizeCalendarWeekStartDay(
            value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture)));
    }

    public void SaveCalendarSettings(int weekStartDay)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calendar_settings (id, week_start_day)
            VALUES (1, $weekStartDay)
            ON CONFLICT(id) DO UPDATE SET
                week_start_day = excluded.week_start_day,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$weekStartDay", NormalizeCalendarWeekStartDay(weekStartDay));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    private static int NormalizeCalendarWeekStartDay(int value) =>
        value is >= 0 and <= 6 ? value : 0;

    public void SaveSearchEngineSettings(string provider, string googleSearchUrlTemplate, string braveApiKey, string geminiApiKey)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO search_engine_settings (id, provider, google_search_url_template, brave_api_key, gemini_api_key)
            VALUES (1, $provider, $googleTemplate, $braveApiKey, $geminiApiKey)
            ON CONFLICT(id) DO UPDATE SET
                provider = excluded.provider,
                google_search_url_template = excluded.google_search_url_template,
                brave_api_key = excluded.brave_api_key,
                gemini_api_key = excluded.gemini_api_key,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$provider", NormalizeSearchEngineProvider(provider));
        command.Parameters.AddWithValue("$googleTemplate", NormalizeGoogleSearchUrlTemplate(googleSearchUrlTemplate));
        command.Parameters.AddWithValue("$braveApiKey", braveApiKey.Trim());
        command.Parameters.AddWithValue("$geminiApiKey", geminiApiKey.Trim());
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    public GidSettingsDto GetGidSettings()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_extensions, digit_count FROM gid_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new GidSettingsDto(
                NormalizeGidTargetExtensions(reader.GetString(0)),
                NormalizeGidDigitCount(reader.GetInt32(1)))
            : new GidSettingsDto(DefaultGidTargetExtensions, DefaultGidDigitCount);
    }

    public void SaveGidSettings(string targetExtensions, int digitCount)
    {
        EnsureCreated();
        digitCount = NormalizeGidDigitCount(digitCount);
        ValidateGidDigitCountChange(digitCount);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gid_settings (id, target_extensions, digit_count)
            VALUES (1, $targetExtensions, $digitCount)
            ON CONFLICT(id) DO UPDATE SET
                target_extensions = excluded.target_extensions,
                digit_count = excluded.digit_count,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$targetExtensions", NormalizeGidTargetExtensions(targetExtensions));
        command.Parameters.AddWithValue("$digitCount", digitCount);
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    private void ValidateGidDigitCountChange(int digitCount)
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM items WHERE length(gid) <> $digitCount;";
        command.Parameters.AddWithValue("$digitCount", digitCount);
        var mismatchedCount = Convert.ToInt32((long)(command.ExecuteScalar() ?? 0L));
        if (mismatchedCount > 0)
        {
            throw new InvalidOperationException(
                $"既存作品のGIDと異なる{digitCount}桁には直接変更できません。先にGID管理の一括移行を実行してください。");
        }
    }

    public GidMigrationPreviewDto PreviewGidMigration(int targetDigitCount = DefaultGidDigitCount)
    {
        EnsureExternalGalleryGidSchema();
        return BuildGidMigrationPlan(NormalizeGidDigitCount(targetDigitCount), inspectFiles: true).Preview;
    }

    public GidMigrationResultDto MigrateGids(
        int targetDigitCount = DefaultGidDigitCount,
        Action<GidMigrationProgressDto>? reportProgress = null)
    {
        targetDigitCount = NormalizeGidDigitCount(targetDigitCount);
        EnsureExternalGalleryGidSchema();
        EnsureExternalApplicationDataSchema();

        lock (_gidReservationLock)
        {
            reportProgress?.Invoke(new GidMigrationProgressDto("preflight", 0, 1, "GID移行対象を検証しています。"));
            var plan = BuildGidMigrationPlan(targetDigitCount, inspectFiles: true, reportProgress);
            reportProgress?.Invoke(new GidMigrationProgressDto("preflight", 1, 1, "GID移行対象の検証が完了しました。"));
            if (plan.Preview.IsAlreadyMigrated)
            {
                throw new InvalidOperationException($"GIDは既に{targetDigitCount}桁へ移行済みです。");
            }
            if (!plan.Preview.CanExecute)
            {
                throw new InvalidOperationException(
                    "GID移行の事前検証に失敗しました。\n" +
                    string.Join("\n", plan.Preview.Issues.Take(10)));
            }

            var localDataDirectory = Path.GetDirectoryName(_defaultCacheDatabasePath)
                ?? throw new InvalidOperationException("GalleryBrowserのローカルデータ保存先を取得できませんでした。");
            var backupDirectory = Path.Combine(localDataDirectory, "backups");
            Directory.CreateDirectory(backupDirectory);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(backupDirectory, $"GalleryBrowser_before_gid{targetDigitCount}_{timestamp}.sqlite");
            var mappingPath = Path.Combine(backupDirectory, $"GalleryBrowser_gid_mapping_{timestamp}.json");

            reportProgress?.Invoke(new GidMigrationProgressDto("backup", 0, 2, "Gallery本体DBをバックアップしています。"));
            BackupGalleryDatabase(backupPath);
            reportProgress?.Invoke(new GidMigrationProgressDto("backup", 1, 2, "旧GIDと新GIDの対応表を保存しています。"));
            File.WriteAllText(
                mappingPath,
                JsonSerializer.Serialize(new
                {
                    createdAt = DateTimeOffset.Now,
                    sourceDatabase = ExternalGalleryDatabasePath,
                    targetDigitCount,
                    entries = plan.Entries
                }, JsonOptions),
                Encoding.UTF8);
            reportProgress?.Invoke(new GidMigrationProgressDto("backup", 2, 2, "DBバックアップとGID対応表を保存しました。"));

            var renamedEntries = new List<GidMigrationEntry>(plan.Preview.FileRenameCount);
            try
            {
                var renameEntries = plan.Entries.Where(entry => entry.RenameFile && !entry.DeleteMissingItem).ToArray();
                for (var index = 0; index < renameEntries.Length; index++)
                {
                    var entry = renameEntries[index];
                    if (File.Exists(entry.OldPath) && !PathExists(entry.NewPath))
                    {
                        File.Move(entry.OldPath, entry.NewPath);
                    }
                    else if (!PathExists(entry.OldPath) && File.Exists(entry.NewPath))
                    {
                        // A previous interrupted run may already have renamed this file.
                    }
                    else
                    {
                        throw new IOException($"ファイル名を変更できる状態ではありません: {entry.OldPath}");
                    }
                    renamedEntries.Add(entry);

                    if (index == renameEntries.Length - 1 || (index + 1) % 100 == 0)
                    {
                        reportProgress?.Invoke(new GidMigrationProgressDto(
                            "files",
                            index + 1,
                            renameEntries.Length,
                            $"ファイル名を{targetDigitCount}桁GIDへ変更しています ({index + 1:N0}/{renameEntries.Length:N0})"));
                    }
                }

                reportProgress?.Invoke(new GidMigrationProgressDto(
                    "database",
                    0,
                    plan.Entries.Count,
                    plan.Preview.MissingFileCount > 0
                        ? $"見つからない{plan.Preview.MissingFileCount:N0}件をDBから抹消し、残りのGID参照を更新しています。"
                        : "DB内のGID参照を更新しています。"));
                ApplyGidMigrationToDatabase(plan.Entries, targetDigitCount, reportProgress);
            }
            catch (Exception migrationException)
            {
                var rollbackErrors = RollbackGidFileRenames(renamedEntries);
                var rollbackMessage = rollbackErrors.Count == 0
                    ? "変更済みのファイル名は元に戻しました。"
                    : $"ファイル名の復元に{rollbackErrors.Count}件失敗しました。対応表: {mappingPath}";
                throw new InvalidOperationException(
                    $"GID移行を完了できませんでした。{rollbackMessage} DBバックアップ: {backupPath}",
                    migrationException);
            }

            reportProgress?.Invoke(new GidMigrationProgressDto("finalize", 0, 3, "移行後のキャッシュを初期化しています。"));
            ResetCachesAfterGidMigration();
            reportProgress?.Invoke(new GidMigrationProgressDto("finalize", 1, 3, "gid設定を更新しています。"));
            var currentSettings = GetGidSettings();
            SaveGidSettings(currentSettings.TargetExtensions, targetDigitCount);
            reportProgress?.Invoke(new GidMigrationProgressDto("finalize", 2, 3, "完了記録を保存しています。"));
            File.WriteAllText(
                Path.ChangeExtension(mappingPath, ".completed.txt"),
                $"completed_at={DateTimeOffset.Now:O}{Environment.NewLine}database={ExternalGalleryDatabasePath}{Environment.NewLine}",
                Encoding.UTF8);
            var migratedItemCount = plan.Entries.Count - plan.Preview.MissingFileCount;
            reportProgress?.Invoke(new GidMigrationProgressDto(
                "completed",
                plan.Entries.Count,
                plan.Entries.Count,
                plan.Preview.MissingFileCount > 0
                    ? $"{migratedItemCount:N0}件を{targetDigitCount}桁GIDへ移行し、見つからない{plan.Preview.MissingFileCount:N0}件をDBから抹消しました。"
                    : $"{targetDigitCount}桁GIDへの移行が完了しました。"));

            return new GidMigrationResultDto(
                targetDigitCount,
                migratedItemCount,
                plan.Preview.FileRenameCount,
                plan.Preview.MissingFileCount,
                backupPath,
                mappingPath);
        }
    }

    private GidMigrationPlan BuildGidMigrationPlan(
        int targetDigitCount,
        bool inspectFiles,
        Action<GidMigrationProgressDto>? reportProgress = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        var rows = new List<(string Gid, string Path, string SourceZipName, bool Archived)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT gid, current_path, source_zip_name, COALESCE(archived_flg, 0) FROM items ORDER BY current_path COLLATE NOCASE, gid;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0).Trim(), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0));
            }
        }

        var issues = new List<string>();
        var invalidGids = rows.Count(row =>
            row.Gid.Length == 0 ||
            row.Gid.Any(character => GidAlphabet.IndexOf(char.ToUpperInvariant(character)) < 0));
        if (invalidGids > 0)
        {
            AddGidMigrationIssue(issues, $"英数字36進数として解釈できないGIDが{invalidGids:N0}件あります。");
        }

        var alreadyMigrated = rows.Count > 0 && rows.All(row => row.Gid.Length == targetDigitCount) && invalidGids == 0;
        var mixedTargetLengthCount = alreadyMigrated ? 0 : rows.Count(row => row.Gid.Length == targetDigitCount);
        if (mixedTargetLengthCount > 0)
        {
            AddGidMigrationIssue(issues, $"{targetDigitCount}桁とそれ以外のGIDが混在しています ({mixedTargetLengthCount:N0}件)。");
        }

        var archivedItemCount = rows.Count(row => row.Archived);
        if (!alreadyMigrated && archivedItemCount > 0)
        {
            AddGidMigrationIssue(
                issues,
                $"pCloudへアーカイブ済みの作品が{archivedItemCount:N0}件あります。リモート上のファイル名を安全に変更できないため、GID一括移行は実行できません。");
        }

        var capacity = BigInteger.Pow(36, targetDigitCount) - BigInteger.Pow(36, targetDigitCount - 1);
        if (new BigInteger(rows.Count) > capacity)
        {
            AddGidMigrationIssue(issues, $"{targetDigitCount}桁のGID空間に{rows.Count:N0}件を割り当てられません。");
        }

        var entries = new List<GidMigrationEntry>(rows.Count);
        var candidate = BigInteger.Pow(36, targetDigitCount - 1);
        var newPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readyFileCount = 0;
        var alreadyRenamedFileCount = 0;
        var missingFileCount = 0;
        var conflictingFileCount = 0;
        var mismatchedGidCount = invalidGids + mixedTargetLengthCount;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var newGid = FormatGidBase36(candidate, targetDigitCount);
            candidate += BigInteger.One;
            var newFileName = ReplaceMatchingGidTags(Path.GetFileName(row.Path), row.Gid, newGid, out var pathHasMatchingTag, out var pathHasMismatchedTag);
            var newPath = pathHasMatchingTag
                ? Path.Combine(Path.GetDirectoryName(row.Path) ?? string.Empty, newFileName)
                : row.Path;
            var newSourceZipName = ReplaceMatchingGidTags(row.SourceZipName, row.Gid, newGid, out _, out var sourceHasMismatchedTag);
            if (pathHasMismatchedTag || sourceHasMismatchedTag)
            {
                mismatchedGidCount++;
                AddGidMigrationIssue(issues, $"DBとファイル名のGIDが一致しません: {row.Path}");
            }

            var renameFile = pathHasMatchingTag && !string.Equals(row.Path, newPath, StringComparison.OrdinalIgnoreCase);
            var deleteMissingItem = false;
            if (inspectFiles && renameFile)
            {
                var oldExists = File.Exists(row.Path);
                var newExists = PathExists(newPath);
                if (oldExists && !newExists)
                {
                    readyFileCount++;
                }
                else if (!oldExists && File.Exists(newPath))
                {
                    alreadyRenamedFileCount++;
                }
                else if (!oldExists && !newExists)
                {
                    missingFileCount++;
                    deleteMissingItem = true;
                    AddGidMigrationIssue(issues, $"削除済みとしてDBから抹消: {row.Path}");
                }
                else
                {
                    conflictingFileCount++;
                    AddGidMigrationIssue(issues, $"移行先と同名の項目が既にあります: {newPath}");
                }
            }
            if (!deleteMissingItem && !newPaths.Add(newPath))
            {
                conflictingFileCount++;
                AddGidMigrationIssue(issues, $"移行後のパスが重複します: {newPath}");
            }

            entries.Add(new GidMigrationEntry(
                row.Gid,
                newGid,
                row.Path,
                newPath,
                row.SourceZipName,
                newSourceZipName,
                renameFile,
                deleteMissingItem));
            if (reportProgress is not null &&
                (rowIndex == rows.Count - 1 || (rowIndex + 1) % 100 == 0))
            {
                reportProgress(new GidMigrationProgressDto(
                    "preflight",
                    rowIndex + 1,
                    rows.Count,
                    $"GID移行対象を検証しています ({rowIndex + 1:N0}/{rows.Count:N0})"));
            }
        }

        var fileRenameCount = entries.Count(entry => entry.RenameFile && !entry.DeleteMissingItem);
        var databaseOnlyCount = entries.Count(entry => !entry.RenameFile && !entry.DeleteMissingItem);
        if (alreadyMigrated)
        {
            issues.Clear();
            AddGidMigrationIssue(issues, $"DB内のGIDは既にすべて{targetDigitCount}桁です。");
        }
        var canExecute = rows.Count > 0 &&
                         !alreadyMigrated &&
                         invalidGids == 0 &&
                         mixedTargetLengthCount == 0 &&
                         archivedItemCount == 0 &&
                         conflictingFileCount == 0 &&
                         mismatchedGidCount == 0 &&
                         new BigInteger(rows.Count) <= capacity;
        var preview = new GidMigrationPreviewDto(
            targetDigitCount,
            rows.Count,
            fileRenameCount,
            databaseOnlyCount,
            readyFileCount,
            alreadyRenamedFileCount,
            missingFileCount,
            conflictingFileCount,
            mismatchedGidCount,
            alreadyMigrated,
            canExecute,
            issues);
        return new GidMigrationPlan(entries, preview);
    }

    private void BackupGalleryDatabase(string backupPath)
    {
        using var source = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var destination = OpenStandaloneConnection(backupPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
        ValidateSqliteDatabase(destination, "GID移行前バックアップ");
    }

    private void ApplyGidMigrationToDatabase(
        IReadOnlyList<GidMigrationEntry> entries,
        int targetDigitCount,
        Action<GidMigrationProgressDto>? reportProgress)
    {
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        using (var deferForeignKeys = connection.CreateCommand())
        {
            deferForeignKeys.Transaction = transaction;
            deferForeignKeys.CommandText = "PRAGMA defer_foreign_keys = ON;";
            deferForeignKeys.ExecuteNonQuery();
        }
        using (var createMap = connection.CreateCommand())
        {
            createMap.Transaction = transaction;
            createMap.CommandText = """
                CREATE TEMP TABLE gid_migration_delete (
                    old_gid TEXT PRIMARY KEY
                );
                CREATE TEMP TABLE gid_migration_map (
                    old_gid TEXT PRIMARY KEY,
                    new_gid TEXT NOT NULL UNIQUE,
                    new_path TEXT NOT NULL UNIQUE,
                    new_source_zip_name TEXT NOT NULL
                );
                """;
            createMap.ExecuteNonQuery();
        }

        var preparationCompleted = 0;
        var preparationTotal = Math.Max(1, entries.Count);
        reportProgress?.Invoke(new GidMigrationProgressDto(
            "database_prepare",
            0,
            preparationTotal,
            "DB更新用のGID対応表を準備しています。"));
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO gid_migration_map (old_gid, new_gid, new_path, new_source_zip_name) VALUES ($old, $new, $path, $sourceName);";
            var oldParameter = insert.Parameters.Add("$old", SqliteType.Text);
            var newParameter = insert.Parameters.Add("$new", SqliteType.Text);
            var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
            var sourceNameParameter = insert.Parameters.Add("$sourceName", SqliteType.Text);
            insert.Prepare();
            foreach (var entry in entries)
            {
                if (entry.DeleteMissingItem)
                {
                    continue;
                }
                oldParameter.Value = entry.OldGid;
                newParameter.Value = entry.NewGid;
                pathParameter.Value = entry.NewPath;
                sourceNameParameter.Value = entry.NewSourceZipName;
                insert.ExecuteNonQuery();
                preparationCompleted++;
                if (preparationCompleted == preparationTotal || preparationCompleted % 500 == 0)
                {
                    reportProgress?.Invoke(new GidMigrationProgressDto(
                        "database_prepare",
                        preparationCompleted,
                        preparationTotal,
                        $"DB更新用のGID対応表を準備しています ({preparationCompleted:N0}/{preparationTotal:N0})"));
                }
            }
        }
        using (var insertDelete = connection.CreateCommand())
        {
            insertDelete.Transaction = transaction;
            insertDelete.CommandText = "INSERT INTO gid_migration_delete (old_gid) VALUES ($old);";
            var oldParameter = insertDelete.Parameters.Add("$old", SqliteType.Text);
            insertDelete.Prepare();
            foreach (var entry in entries.Where(entry => entry.DeleteMissingItem))
            {
                oldParameter.Value = entry.OldGid;
                insertDelete.ExecuteNonQuery();
                preparationCompleted++;
                if (preparationCompleted == preparationTotal || preparationCompleted % 500 == 0)
                {
                    reportProgress?.Invoke(new GidMigrationProgressDto(
                        "database_prepare",
                        preparationCompleted,
                        preparationTotal,
                        $"DB更新用のGID対応表を準備しています ({preparationCompleted:N0}/{preparationTotal:N0})"));
                }
            }
        }

        var keptItemCount = entries.Count(entry => !entry.DeleteMissingItem);
        var deletedItemCount = entries.Count - keptItemCount;
        var deletedEventCount = QueryGidMigrationCount(connection, transaction,
            "SELECT COUNT(*) FROM item_events WHERE gid IN (SELECT old_gid FROM gid_migration_delete);");
        var itemTagCount = QueryGidMigrationCount(connection, transaction,
            "SELECT COUNT(*) FROM item_tags WHERE gid IN (SELECT old_gid FROM gid_migration_map);");
        var itemFilterCount = QueryGidMigrationCount(connection, transaction,
            "SELECT COUNT(*) FROM gallery_item_filters WHERE gid IN (SELECT old_gid FROM gid_migration_map);");
        var itemCombinationCount = QueryGidMigrationCount(connection, transaction,
            "SELECT COUNT(*) FROM gallery_item_filter_combinations WHERE gid IN (SELECT old_gid FROM gid_migration_map);");
        var itemEventCount = QueryGidMigrationCount(connection, transaction,
            "SELECT COUNT(*) FROM item_events WHERE gid IN (SELECT old_gid FROM gid_migration_map);");
        var databaseTotal = Math.Max(
            1,
            deletedEventCount + deletedItemCount + keptItemCount + itemTagCount + itemFilterCount +
            itemCombinationCount + itemEventCount + keptItemCount + 2);
        var databaseCompleted = 0;

        void ReportDatabaseProgress(string message)
        {
            reportProgress?.Invoke(new GidMigrationProgressDto(
                "database",
                Math.Min(databaseCompleted, databaseTotal),
                databaseTotal,
                message));
        }

        ReportDatabaseProgress("イベント履歴の参照インデックスを準備しています。");
        ExecuteGidMigrationCommand(connection, transaction,
            "CREATE INDEX IF NOT EXISTS idx_item_events_gid ON item_events(gid);");
        ReportDatabaseProgress("削除済み作品をDBから抹消しています。");

        // These files were already removed outside the application. Remove their history as well as
        // the item itself so the backup is the only retained copy of the obsolete records.
        ExecuteGidMigrationCommand(connection, transaction, """
            DELETE FROM item_events
            WHERE gid IN (SELECT old_gid FROM gid_migration_delete);
            DELETE FROM items
            WHERE gid IN (SELECT old_gid FROM gid_migration_delete);
            DELETE FROM gid_registry
            WHERE gid IN (SELECT old_gid FROM gid_migration_delete);
            """);
        databaseCompleted += deletedEventCount + deletedItemCount;
        ReportDatabaseProgress("作品テーブルのGIDを更新しています。");

        ExecuteGidMigrationBatchCommand(connection, transaction, """
            UPDATE items
            SET current_path = (SELECT new_path FROM gid_migration_map WHERE old_gid = items.gid),
                source_zip_name = (SELECT new_source_zip_name FROM gid_migration_map WHERE old_gid = items.gid),
                gid = (SELECT new_gid FROM gid_migration_map WHERE old_gid = items.gid)
            WHERE gid IN (
                SELECT old_gid FROM gid_migration_map
                WHERE old_gid IN (SELECT gid FROM items)
                LIMIT $batchSize
            );
            """, 250, keptItemCount, processed =>
            {
                databaseCompleted += processed;
                ReportDatabaseProgress($"作品テーブルのGIDを更新しています ({databaseCompleted:N0}/{databaseTotal:N0})");
            });
        ReportDatabaseProgress("Tag割り当てのGIDを更新しています。");
        ExecuteGidMigrationBatchCommand(connection, transaction, """
            UPDATE item_tags
            SET gid = (SELECT new_gid FROM gid_migration_map WHERE old_gid = item_tags.gid)
            WHERE rowid IN (
                SELECT item_tags.rowid FROM item_tags
                JOIN gid_migration_map ON old_gid = item_tags.gid
                LIMIT $batchSize
            );
            """, 1_000, itemTagCount, processed =>
            {
                databaseCompleted += processed;
                ReportDatabaseProgress("Tag割り当てのGIDを更新しています。");
            });
        ReportDatabaseProgress("フィルター割り当てのGIDを更新しています。");
        ExecuteGidMigrationBatchCommand(connection, transaction, """
            UPDATE gallery_item_filters
            SET gid = (SELECT new_gid FROM gid_migration_map WHERE old_gid = gallery_item_filters.gid)
            WHERE rowid IN (
                SELECT gallery_item_filters.rowid FROM gallery_item_filters
                JOIN gid_migration_map ON old_gid = gallery_item_filters.gid
                LIMIT $batchSize
            );
            """, 1_000, itemFilterCount, processed =>
            {
                databaseCompleted += processed;
                ReportDatabaseProgress("フィルター割り当てのGIDを更新しています。");
            });
        ExecuteGidMigrationBatchCommand(connection, transaction, """
            UPDATE gallery_item_filter_combinations
            SET gid = (SELECT new_gid FROM gid_migration_map WHERE old_gid = gallery_item_filter_combinations.gid)
            WHERE rowid IN (
                SELECT gallery_item_filter_combinations.rowid FROM gallery_item_filter_combinations
                JOIN gid_migration_map ON old_gid = gallery_item_filter_combinations.gid
                LIMIT $batchSize
            );
            """, 1_000, itemCombinationCount, processed =>
            {
                databaseCompleted += processed;
                ReportDatabaseProgress("フィルター組み合わせのGIDを更新しています。");
            });
        ReportDatabaseProgress("イベント履歴のGIDを更新しています。");
        ExecuteGidMigrationBatchCommand(connection, transaction, """
            UPDATE item_events
            SET detail_json = replace(
                    detail_json,
                    (SELECT old_gid FROM gid_migration_map WHERE old_gid = item_events.gid),
                    (SELECT new_gid FROM gid_migration_map WHERE old_gid = item_events.gid)),
                gid = (SELECT new_gid FROM gid_migration_map WHERE old_gid = item_events.gid)
            WHERE rowid IN (
                SELECT item_events.rowid FROM item_events
                JOIN gid_migration_map ON old_gid = item_events.gid
                LIMIT $batchSize
            );
            """, 1_000, itemEventCount, processed =>
            {
                databaseCompleted += processed;
                ReportDatabaseProgress($"イベント履歴のGIDを更新しています ({databaseCompleted:N0}/{databaseTotal:N0})");
            });
        ReportDatabaseProgress("GID発番台帳を再構築しています。");
        ExecuteGidMigrationCommand(connection, transaction, """
            DELETE FROM gid_registry;
            INSERT INTO gid_registry (gid, source_path)
            SELECT new_gid, new_path FROM gid_migration_map;
            """);
        databaseCompleted += keptItemCount;
        ReportDatabaseProgress("移行後のGID形式を検証しています。");

        using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT COUNT(*) FROM items WHERE length(gid) <> $digitCount OR gid <> upper(gid);";
            verify.Parameters.AddWithValue("$digitCount", targetDigitCount);
            var invalidCount = Convert.ToInt32((long)(verify.ExecuteScalar() ?? 0L));
            if (invalidCount != 0)
            {
                throw new InvalidDataException($"移行後も不正なGIDが{invalidCount:N0}件残っています。");
            }
        }
        databaseCompleted++;
        ReportDatabaseProgress("外部キーの整合性を検証しています。");
        using (var foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.Transaction = transaction;
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            using var reader = foreignKeyCheck.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException($"GID移行後の外部キー整合性に問題があります: {reader.GetString(0)}");
            }
        }
        databaseCompleted++;
        ReportDatabaseProgress("DB更新をコミットしています。");
        transaction.Commit();
        reportProgress?.Invoke(new GidMigrationProgressDto(
            "database",
            databaseTotal,
            databaseTotal,
            "データベースのGID更新が完了しました。"));
    }

    private static int QueryGidMigrationCount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return checked(Convert.ToInt32((long)(command.ExecuteScalar() ?? 0L)));
    }

    private static void ExecuteGidMigrationBatchCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        int batchSize,
        int expectedCount,
        Action<int> reportBatch)
    {
        if (expectedCount == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$batchSize", batchSize);
        var processedCount = 0;
        while (true)
        {
            var updatedCount = command.ExecuteNonQuery();
            if (updatedCount <= 0)
            {
                break;
            }
            processedCount += updatedCount;
            reportBatch(updatedCount);
        }

        if (processedCount != expectedCount)
        {
            throw new InvalidDataException(
                $"GID移行対象件数が処理中に変化しました。想定: {expectedCount:N0}件 / 実績: {processedCount:N0}件");
        }
    }

    private static void ExecuteGidMigrationCommand(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private void ResetCachesAfterGidMigration()
    {
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM file_metadata;
                DELETE FROM thumbnail_cache_entries;
                DELETE FROM creator_summary_cache;
                DELETE FROM derived_data_cache;
                """;
            command.ExecuteNonQuery();
        }
        lock (_creatorSummaryCacheLock)
        {
            _creatorSummaryMemorySignature = null;
            _creatorSummaryMemorySnapshot = null;
        }
        lock (_derivedDataCacheLock)
        {
            _derivedDataMemoryCache.Clear();
        }
        _galleryWorksCache.Clear();
        _galleryFiltersCache.Clear();
    }

    private static IReadOnlyList<string> RollbackGidFileRenames(IReadOnlyList<GidMigrationEntry> renamedEntries)
    {
        var errors = new List<string>();
        for (var index = renamedEntries.Count - 1; index >= 0; index--)
        {
            var entry = renamedEntries[index];
            try
            {
                if (!PathExists(entry.OldPath) && File.Exists(entry.NewPath))
                {
                    File.Move(entry.NewPath, entry.OldPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{entry.NewPath}: {ex.Message}");
            }
        }
        return errors;
    }

    private static string ReplaceMatchingGidTags(
        string value,
        string oldGid,
        string newGid,
        out bool hasMatchingTag,
        out bool hasMismatchedTag)
    {
        hasMatchingTag = false;
        hasMismatchedTag = false;
        foreach (Match match in GidTagRegex.Matches(value))
        {
            if (string.Equals(match.Groups["gid"].Value.Trim(), oldGid, StringComparison.OrdinalIgnoreCase))
            {
                hasMatchingTag = true;
            }
            else
            {
                hasMismatchedTag = true;
            }
        }
        return GidTagRegex.Replace(value, match =>
            string.Equals(match.Groups["gid"].Value.Trim(), oldGid, StringComparison.OrdinalIgnoreCase)
                ? $"{{gid={newGid}}}"
                : match.Value);
    }

    private static void AddGidMigrationIssue(ICollection<string> issues, string message)
    {
        if (issues.Count < 20)
        {
            issues.Add(message);
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    public IReadOnlyList<string> ReserveGids(
        IReadOnlyList<string> sourcePaths,
        int digitCount,
        IReadOnlyCollection<string>? additionalKnownGids = null)
    {
        if (sourcePaths.Count == 0)
        {
            return [];
        }

        digitCount = NormalizeGidDigitCount(digitCount);
        EnsureExternalGalleryGidSchema();
        EnsureExternalApplicationDataSchema();

        lock (_gidReservationLock)
        {
            using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
            using var transaction = connection.BeginTransaction(deferred: false);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT gid FROM gid_registry UNION SELECT gid FROM items;";
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        known.Add(reader.GetString(0).Trim().ToUpperInvariant());
                    }
                }
            }

            foreach (var gid in additionalKnownGids ?? [])
            {
                if (!string.IsNullOrWhiteSpace(gid))
                {
                    known.Add(gid.Trim().ToUpperInvariant());
                }
            }

            var minimum = BigInteger.Pow(36, digitCount - 1);
            var maximum = BigInteger.Pow(36, digitCount) - BigInteger.One;
            var capacity = maximum - minimum + BigInteger.One;
            var issuedValues = new HashSet<BigInteger>();
            foreach (var gid in known)
            {
                if (gid.Length == digitCount &&
                    TryParseGidBase36(gid, out var value) &&
                    value >= minimum &&
                    value <= maximum)
                {
                    issuedValues.Add(value);
                }
            }
            if (new BigInteger(issuedValues.Count + sourcePaths.Count) > capacity)
            {
                throw new InvalidOperationException($"{digitCount}桁のgid空間に空きがありません。Settings > Configuration > gid管理で桁数を増やしてください。");
            }

            var highestIssued = issuedValues.Count == 0 ? minimum - BigInteger.One : issuedValues.Max();
            var candidate = highestIssued < maximum ? highestIssued + BigInteger.One : minimum;
            var examined = BigInteger.Zero;
            var reserved = new List<string>(sourcePaths.Count);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO gid_registry (gid, source_path) VALUES ($gid, $sourcePath);";
            var gidParameter = insert.CreateParameter();
            gidParameter.ParameterName = "$gid";
            insert.Parameters.Add(gidParameter);
            var sourcePathParameter = insert.CreateParameter();
            sourcePathParameter.ParameterName = "$sourcePath";
            insert.Parameters.Add(sourcePathParameter);

            while (reserved.Count < sourcePaths.Count && examined < capacity)
            {
                var gid = FormatGidBase36(candidate, digitCount);
                if (known.Add(gid))
                {
                    gidParameter.Value = gid;
                    sourcePathParameter.Value = sourcePaths[reserved.Count];
                    insert.ExecuteNonQuery();
                    reserved.Add(gid);
                }
                candidate = candidate >= maximum ? minimum : candidate + BigInteger.One;
                examined += BigInteger.One;
            }

            if (reserved.Count != sourcePaths.Count)
            {
                throw new InvalidOperationException($"{digitCount}桁の重複しないgidを確保できませんでした。gid桁数を増やしてください。");
            }

            transaction.Commit();
            return reserved;
        }
    }

    public IReadOnlyDictionary<string, string> GetExistingGidsForPaths(
        IReadOnlyCollection<string> sourcePaths,
        int digitCount)
    {
        digitCount = NormalizeGidDigitCount(digitCount);
        var normalizedPaths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (normalizedPaths.Length == 0 || !File.Exists(ExternalGalleryDatabasePath))
        {
            return result;
        }

        EnsureExternalGalleryGidSchema();
        EnsureExternalApplicationDataSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        foreach (var pathBatch in normalizedPaths.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var parameters = pathBatch
                .Select((_, index) => $"$path{index}")
                .ToArray();
            command.CommandText = $"""
                SELECT source_path, gid, source_priority
                FROM (
                    SELECT current_path AS source_path, gid, 0 AS source_priority
                    FROM items
                    WHERE current_path COLLATE NOCASE IN ({string.Join(", ", parameters)})
                      AND length(gid) = $digitCount
                    UNION ALL
                    SELECT source_path, gid, 1 AS source_priority
                    FROM gid_registry
                    WHERE source_path COLLATE NOCASE IN ({string.Join(", ", parameters)})
                      AND length(gid) = $digitCount
                )
                ORDER BY source_priority;
                """;
            command.Parameters.AddWithValue("$digitCount", digitCount);
            for (var index = 0; index < pathBatch.Length; index++)
            {
                command.Parameters.AddWithValue(parameters[index], pathBatch[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sourcePath = reader.GetString(0);
                var gid = reader.GetString(1).Trim().ToUpperInvariant();
                if (!result.ContainsKey(sourcePath) &&
                    gid.Length == digitCount &&
                    TryParseGidBase36(gid, out _))
                {
                    result[sourcePath] = gid;
                }
            }
        }

        return result;
    }

    private static bool TryParseGidBase36(string value, out BigInteger result)
    {
        result = BigInteger.Zero;
        foreach (var character in value)
        {
            var digit = GidAlphabet.IndexOf(char.ToUpperInvariant(character));
            if (digit < 0)
            {
                result = BigInteger.Zero;
                return false;
            }
            result = result * 36 + digit;
        }
        return value.Length > 0;
    }

    private static string FormatGidBase36(BigInteger value, int digitCount)
    {
        var characters = new char[digitCount];
        for (var index = digitCount - 1; index >= 0; index--)
        {
            value = BigInteger.DivRem(value, 36, out var remainder);
            characters[index] = GidAlphabet[(int)remainder];
        }
        if (value != BigInteger.Zero)
        {
            throw new InvalidOperationException($"{digitCount}桁を超えるgidを生成しようとしました。");
        }
        return new string(characters);
    }

    private static string NormalizeGidTargetExtensions(string value)
    {
        var rawExtensions = (value ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rawExtensions.Length == 0)
        {
            throw new ArgumentException("gid発番対象拡張子をセミコロン区切りで1件以上入力してください。", nameof(value));
        }

        var extensions = new List<string>();
        foreach (var rawExtension in rawExtensions)
        {
            var extension = rawExtension.TrimStart('.').ToLowerInvariant();
            if (extension.Length is < 1 or > 31 ||
                extension.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            {
                throw new ArgumentException($"gid発番対象拡張子「{rawExtension}」が不正です。", nameof(value));
            }
            if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                extensions.Add(extension);
            }
            if (extensions.Count >= 50)
            {
                break;
            }
        }
        return string.Join(';', extensions);
    }

    private static int NormalizeGidDigitCount(int value) => Math.Clamp(value, MinimumGidDigitCount, MaximumGidDigitCount);

    public IReadOnlyList<CreatorTrackingActivityPlaceSettingDto> GetCreatorTrackingActivityPlaces()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT activity_places_json FROM creator_tracking_settings WHERE id = 1;";
        var rawValue = command.ExecuteScalar() as string;
        if (rawValue is null)
        {
            return DefaultCreatorTrackingActivityPlaces;
        }

        try
        {
            var places = JsonSerializer.Deserialize<CreatorTrackingActivityPlaceSettingDto[]>(rawValue, JsonOptions);
            return places is null
                ? DefaultCreatorTrackingActivityPlaces
                : NormalizeCreatorTrackingActivityPlaces(places);
        }
        catch (JsonException)
        {
            return DefaultCreatorTrackingActivityPlaces;
        }
    }

    public int GetCreatorTrackingCompositionLabelLimit()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT composition_label_limit FROM creator_tracking_settings WHERE id = 1;";
        return NormalizeCreatorTrackingCompositionLabelLimit(command.ExecuteScalar());
    }

    public IReadOnlyList<string> GetCreatorTrackingFollowPolicyOptions() =>
        GetCreatorTrackingStringOptions("follow_policy_options_json", DefaultCreatorTrackingFollowPolicyOptions);

    public IReadOnlyList<CreatorTrackingMetricSettingDto> GetCreatorTrackingMetricSettings()
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT metric_definitions_json FROM creator_tracking_settings WHERE id = 1;";
        var rawValue = command.ExecuteScalar();
        if (rawValue is null)
        {
            return _creatorTrackingMetricDefaults;
        }
        if (rawValue is DBNull)
        {
            return _creatorTrackingMetricDefaults;
        }

        try
        {
            return NormalizeCreatorTrackingMetricSettings(
                JsonSerializer.Deserialize<CreatorTrackingMetricSettingDto[]>((string)rawValue, JsonOptions));
        }
        catch (JsonException)
        {
            return CreateBlankCreatorTrackingMetricSettings();
        }
    }

    private IReadOnlyList<string> GetCreatorTrackingStringOptions(string columnName, IReadOnlyList<string> defaults)
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {columnName} FROM creator_tracking_settings WHERE id = 1;";
        var rawValue = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaults.ToArray();
        }
        try
        {
            return NormalizeCreatorTrackingStringOptions(
                JsonSerializer.Deserialize<string[]>(rawValue, JsonOptions),
                defaults);
        }
        catch (JsonException)
        {
            return defaults.ToArray();
        }
    }

    public void SaveCreatorTrackingSettings(
        IReadOnlyList<CreatorTrackingActivityPlaceSettingDto>? activityPlaces,
        int compositionLabelLimit,
        IReadOnlyList<string>? followPolicyOptions,
        IReadOnlyList<CreatorTrackingMetricSettingDto>? metricSettings)
    {
        EnsureCreated();

        var normalized = NormalizeCreatorTrackingActivityPlaces(activityPlaces);
        var normalizedFollowPolicies = NormalizeCreatorTrackingStringOptions(followPolicyOptions, DefaultCreatorTrackingFollowPolicyOptions);
        var normalizedMetricSettings = NormalizeCreatorTrackingMetricSettings(metricSettings);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creator_tracking_settings (
                id, activity_places_json, composition_label_limit,
                follow_policy_options_json, metric_definitions_json)
            VALUES (1, $activityPlaces, $compositionLabelLimit, $followPolicies, $metricDefinitions)
            ON CONFLICT(id) DO UPDATE SET
                activity_places_json = excluded.activity_places_json,
                composition_label_limit = excluded.composition_label_limit,
                follow_policy_options_json = excluded.follow_policy_options_json,
                metric_definitions_json = excluded.metric_definitions_json,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$activityPlaces", JsonSerializer.Serialize(normalized, JsonOptions));
        command.Parameters.AddWithValue("$compositionLabelLimit", NormalizeCreatorTrackingCompositionLabelLimit(compositionLabelLimit));
        command.Parameters.AddWithValue("$followPolicies", JsonSerializer.Serialize(normalizedFollowPolicies, JsonOptions));
        command.Parameters.AddWithValue("$metricDefinitions", JsonSerializer.Serialize(normalizedMetricSettings, JsonOptions));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    private static IReadOnlyList<string> NormalizeCreatorTrackingStringOptions(
        IReadOnlyList<string>? values,
        IReadOnlyList<string> defaults)
    {
        var normalized = (values ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        return values is null ? defaults.ToArray() : normalized;
    }

    private static IReadOnlyList<CreatorTrackingMetricSettingDto> CreateBlankCreatorTrackingMetricSettings() =>
        CreatorTrackingMetricKeys
            .Select(key => new CreatorTrackingMetricSettingDto(key, string.Empty, null))
            .ToArray();

    private static IReadOnlyList<CreatorTrackingMetricSettingDto> LoadCreatorTrackingMetricDefaults(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, CreatorTrackingMetricDefaultsFileName);
        if (!File.Exists(path))
        {
            return CreateBlankCreatorTrackingMetricSettings();
        }

        try
        {
            return NormalizeCreatorTrackingMetricSettings(
                JsonSerializer.Deserialize<CreatorTrackingMetricSettingDto[]>(File.ReadAllText(path), JsonOptions));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return CreateBlankCreatorTrackingMetricSettings();
        }
    }

    private static IReadOnlyList<CreatorTrackingMetricSettingDto> NormalizeCreatorTrackingMetricSettings(
        IReadOnlyList<CreatorTrackingMetricSettingDto>? settings)
    {
        var byKey = (settings ?? [])
            .Where(setting => !string.IsNullOrWhiteSpace(setting.Key))
            .GroupBy(setting => setting.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return CreatorTrackingMetricKeys.Select(key =>
        {
            if (!byKey.TryGetValue(key, out var setting))
            {
                return new CreatorTrackingMetricSettingDto(key, string.Empty, null);
            }
            double? weight = setting.WeightPercent is null
                ? null
                : Math.Clamp(setting.WeightPercent.Value, 0, 100);
            return new CreatorTrackingMetricSettingDto(key, (setting.Label ?? string.Empty).Trim(), weight);
        }).ToArray();
    }

    private static int NormalizeCreatorTrackingCompositionLabelLimit(object? value) =>
        value is not null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var parsed)
            ? Math.Clamp(parsed, 1, 8)
            : DefaultCreatorTrackingCompositionLabelLimit;

    private static IReadOnlyList<CreatorTrackingActivityPlaceSettingDto> NormalizeCreatorTrackingActivityPlaces(
        IReadOnlyList<CreatorTrackingActivityPlaceSettingDto>? activityPlaces) =>
        (activityPlaces ?? [])
            .Take(100)
            .Select(place => new CreatorTrackingActivityPlaceSettingDto(
                (place.Label ?? string.Empty).Trim(),
                (place.Placeholder ?? string.Empty).Trim(),
                NormalizeCreatorTrackingIconDataUri(place.IconDataUri)))
            .ToArray();

    private static string NormalizeCreatorTrackingIconDataUri(string? value)
    {
        var iconDataUri = value?.Trim() ?? string.Empty;
        return iconDataUri.Length <= 1_500_000 && iconDataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            ? iconDataUri
            : string.Empty;
    }

    public (CreatorTrackingDto Tracking, bool Created) GetOrCreateCreatorTracking(
        string creator,
        CreatorTrackingTemplateContextDto? templateContext)
    {
        EnsureCreated();

        var normalizedCreator = creator.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCreator))
        {
            throw new ArgumentException("Creatorが指定されていません。", nameof(creator));
        }

        using (var connection = OpenApplicationDataConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM creator_tracking WHERE creator = $creator COLLATE NOCASE);";
            command.Parameters.AddWithValue("$creator", normalizedCreator);
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            {
                return (GetCreatorTracking(normalizedCreator), false);
            }
        }

        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var template = CreateDefaultCreatorTracking(normalizedCreator);
        template = template with
        {
            DisplayName = !string.IsNullOrWhiteSpace(templateContext?.DisplayName)
                ? templateContext.DisplayName.Trim()
                : template.DisplayName,
            MainStoragePath = !string.IsNullOrWhiteSpace(templateContext?.MainStoragePath)
                ? templateContext.MainStoragePath.Trim()
                : template.MainStoragePath,
            LastCheckedOn = string.IsNullOrWhiteSpace(template.LastCheckedOn) ? today : template.LastCheckedOn,
            LastActivityOn = string.IsNullOrWhiteSpace(template.LastActivityOn) ? today : template.LastActivityOn
        };
        SaveCreatorTracking(template);
        return (GetCreatorTracking(normalizedCreator), true);
    }

    public CreatorTrackingDto GetCreatorTracking(string creator)
    {
        EnsureCreated();

        var normalizedCreator = creator.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCreator))
        {
            throw new ArgumentException("Creatorが指定されていません。", nameof(creator));
        }

        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                creator,
                display_name,
                alternate_name,
                tracking_status,
                activity_status,
                follow_up_status,
                last_checked_on,
                last_activity_on,
                activity_summary,
                activity_links_json,
                main_storage_path,
                work_storage_path,
                unsorted_storage_path,
                evaluation_metrics_json,
                personal_rating,
                evaluation_memo,
                subscription_history_json,
                purchase_history_json,
                monthly_support_amount,
                lifetime_spend,
                currency,
                support_started_on,
                support_ended_on,
                support_memo,
                updated_at,
                storage_locations_json
            FROM creator_tracking
            WHERE creator = $creator COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$creator", normalizedCreator);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return CreateDefaultCreatorTracking(normalizedCreator);
        }

        var activityLinks = DeserializeCreatorTrackingActivityLinks(reader.GetString(9));

        IReadOnlyDictionary<string, int>? storedMetrics;
        try
        {
            storedMetrics = JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(13), JsonOptions);
        }
        catch (JsonException)
        {
            storedMetrics = null;
        }

        var evaluationMetrics = NormalizeCreatorTrackingMetrics(storedMetrics, reader.GetDouble(14));
        var personalRating = CalculateCreatorTrackingRating(evaluationMetrics);
        var (displayName, alternateName) = SplitLegacyCreatorTrackingDisplayName(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2));
        var subscriptionHistory = DeserializeCreatorTrackingSubscriptions(reader.GetString(16));
        var purchaseHistory = DeserializeCreatorTrackingPurchases(reader.GetString(17));
        if (subscriptionHistory.Count == 0 && HasLegacyCreatorTrackingSubscription(reader))
        {
            var platform = activityLinks.FirstOrDefault(link =>
                link.Status.Contains("購読", StringComparison.OrdinalIgnoreCase))?.Label ?? string.Empty;
            subscriptionHistory =
            [
                new CreatorTrackingSubscriptionDto(
                    Guid.NewGuid().ToString("N"),
                    platform,
                    string.Empty,
                    reader.GetString(20),
                    reader.GetDouble(18),
                    "monthly",
                    reader.GetString(21),
                    reader.GetString(22),
                    !string.IsNullOrWhiteSpace(reader.GetString(22)),
                    false)
            ];
        }

        return new CreatorTrackingDto(
            reader.GetString(0),
            displayName,
            alternateName,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            activityLinks,
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            evaluationMetrics,
            personalRating,
            reader.GetString(15),
            subscriptionHistory,
            purchaseHistory,
            reader.GetDouble(18),
            reader.GetDouble(19),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            reader.GetString(24))
        {
            StorageLocations = DeserializeCreatorTrackingStorageLocations(
                reader.GetString(25),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12))
        };
    }

    public IReadOnlyList<CalendarSubscriptionEventDto> ListCalendarSubscriptionEvents()
    {
        EnsureCreated();

        var events = new List<CalendarSubscriptionEventDto>();
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                creator,
                display_name,
                alternate_name,
                subscription_history_json,
                monthly_support_amount,
                currency,
                support_started_on,
                support_ended_on
            FROM creator_tracking
            ORDER BY creator COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var creator = reader.GetString(0).Trim();
            if (string.IsNullOrWhiteSpace(creator))
            {
                continue;
            }

            var (displayName, _) = SplitLegacyCreatorTrackingDisplayName(
                creator,
                reader.GetString(1),
                reader.GetString(2));
            var subscriptions = DeserializeCreatorTrackingSubscriptions(reader.GetString(3));
            if (subscriptions.Count == 0 && HasLegacyCreatorTrackingSubscription(
                    reader.GetDouble(4),
                    reader.GetString(6),
                    reader.GetString(7)))
            {
                subscriptions =
                [
                    new CreatorTrackingSubscriptionDto(
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        reader.GetString(5),
                        reader.GetDouble(4),
                        "monthly",
                        reader.GetString(6),
                        reader.GetString(7),
                        !string.IsNullOrWhiteSpace(reader.GetString(7)),
                        false)
                ];
            }

            foreach (var subscription in subscriptions)
            {
                if (subscription.Wishlist ||
                    !subscription.IsActive ||
                    string.IsNullOrWhiteSpace(subscription.RenewalOn) ||
                    !TryParseCreatorTrackingDate(subscription.RenewalOn, out _))
                {
                    continue;
                }

                events.Add(new CalendarSubscriptionEventDto(
                    string.IsNullOrWhiteSpace(subscription.Id)
                        ? $"{creator}|{subscription.Platform}|{subscription.RenewalOn}"
                        : subscription.Id,
                    creator,
                    string.IsNullOrWhiteSpace(displayName) ? creator : displayName,
                    subscription.Platform,
                    subscription.Plan,
                    subscription.Currency,
                    subscription.Amount,
                    subscription.RenewalOn,
                    subscription.EndingPlanned,
                    subscription.Reminder));
            }
        }

        return events
            .OrderBy(entry => entry.RenewalOn, StringComparer.Ordinal)
            .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Platform, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CreatorTrackingDashboardDto> GetCreatorTrackingDashboardAsync(
        CreatorTrackingDto tracking,
        CreatorTrackingDashboardContextDto context,
        string displayCurrency = DefaultCreatorTrackingDisplayCurrency,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        EnsureCreated();

        var normalizedCreator = tracking.Creator.Trim();
        var normalizedDisplayCurrency = NormalizeCreatorTrackingCurrency(displayCurrency);
        SaveCreatorTrackingArchiveSnapshot(
            normalizedCreator,
            context.Category,
            context.FileCount,
            context.TotalImageCount);
        var cacheKey = CreateDerivedDataCacheKey("creator-tracking-dashboard", new
        {
            Creator = normalizedCreator.ToUpperInvariant(),
            Category = context.Category.Trim().ToLowerInvariant(),
            Currency = normalizedDisplayCurrency
        });
        var sourceSignature = CombineSourceSignatures(
            CreateCreatorTrackingDashboardSourceSignature(normalizedCreator, context.Category),
            CreateDerivedDataCacheKey("context", context));
        if (!forceRefresh && TryReadDerivedDataCache(cacheKey, sourceSignature, out CreatorTrackingDashboardDto cachedDashboard))
        {
            return cachedDashboard;
        }

        var creators = (context.Creators ?? [])
            .Append(normalizedCreator)
            .Select(creator => creator.Trim())
            .Where(creator => !string.IsNullOrWhiteSpace(creator))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var creatorCount = Math.Max(1, Math.Max(context.CreatorCount, creators.Length));
        var aggregate = await GetCreatorTrackingDashboardAggregateAsync(
            creators,
            normalizedDisplayCurrency,
            cancellationToken);
        var trackingDaysByCreator = aggregate.TrackingDaysByCreator;
        var spendByCreator = aggregate.SpendByCreator;
        var currentTrackingDays = CalculateCreatorTrackingDays(tracking.LastCheckedOn, tracking.LastActivityOn);
        var currentSpend = spendByCreator.GetValueOrDefault(normalizedCreator)
            ?? new CreatorTrackingSpendSummary(0, 0, false);

        var archiveMonths = ListCreatorTrackingArchiveMonths(
            normalizedCreator,
            context.Category,
            tracking.LastCheckedOn);
        var archiveSnapshots = ListCreatorTrackingArchiveSnapshots(
            normalizedCreator,
            context.Category,
            tracking.LastCheckedOn);

        var dashboard = new CreatorTrackingDashboardDto(
            context.Category,
            creatorCount,
            Math.Max(0, context.FileCount),
            NormalizeCreatorTrackingRank(context.FileRank, creatorCount),
            Math.Max(0, context.TotalImageCount),
            NormalizeCreatorTrackingRank(context.TotalImageCountRank, creatorCount),
            Math.Max(0, context.TotalRating),
            NormalizeCreatorTrackingRank(context.TotalRatingRank, creatorCount),
            currentTrackingDays,
            CalculateCreatorTrackingRank(trackingDaysByCreator.Values, currentTrackingDays),
            currentSpend.Total,
            CalculateCreatorTrackingRank(spendByCreator.Values.Select(value => value.Total), currentSpend.Total),
            currentSpend.RecentThreeMonthTotal,
            CalculateCreatorTrackingRank(
                spendByCreator.Values.Select(value => value.RecentThreeMonthTotal),
                currentSpend.RecentThreeMonthTotal),
            normalizedDisplayCurrency,
            currentSpend.HasMissingRates,
            archiveMonths,
            archiveSnapshots);
        WriteDerivedDataCache(
            cacheKey,
            CombineSourceSignatures(
                CreateCreatorTrackingDashboardSourceSignature(normalizedCreator, context.Category),
                CreateDerivedDataCacheKey("context", context)),
            dashboard);
        return dashboard;
    }

    private async Task<CreatorTrackingDashboardAggregate> GetCreatorTrackingDashboardAggregateAsync(
        IReadOnlyList<string> creators,
        string displayCurrency,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateDerivedDataCacheKey("creator-tracking-dashboard-aggregate", new
        {
            Creators = creators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            Currency = displayCurrency
        });
        var sourceSignature = CreateCreatorTrackingDataSignature();
        if (TryReadDerivedDataCache(cacheKey, sourceSignature, out CreatorTrackingDashboardAggregate cachedAggregate))
        {
            return cachedAggregate;
        }

        var trackingDaysByCreator = creators.ToDictionary(
            creator => creator,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);
        var chargesByCreator = creators.ToDictionary(
            creator => creator,
            _ => (IReadOnlyList<CreatorTrackingCharge>)[],
            StringComparer.OrdinalIgnoreCase);

        using (var connection = OpenApplicationDataConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    creator,
                    last_checked_on,
                    last_activity_on,
                    subscription_history_json,
                    purchase_history_json,
                    monthly_support_amount,
                    currency,
                    support_started_on,
                    support_ended_on
                FROM creator_tracking;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var creator = reader.GetString(0);
                if (!trackingDaysByCreator.ContainsKey(creator))
                {
                    continue;
                }

                var subscriptions = DeserializeCreatorTrackingSubscriptions(reader.GetString(3));
                if (subscriptions.Count == 0 && HasLegacyCreatorTrackingSubscription(
                        reader.GetDouble(5),
                        reader.GetString(7),
                        reader.GetString(8)))
                {
                    subscriptions =
                    [
                        new CreatorTrackingSubscriptionDto(
                            Guid.NewGuid().ToString("N"),
                            string.Empty,
                            string.Empty,
                            reader.GetString(6),
                            reader.GetDouble(5),
                            "monthly",
                            reader.GetString(7),
                            reader.GetString(8),
                            !string.IsNullOrWhiteSpace(reader.GetString(8)),
                            false)
                    ];
                }

                trackingDaysByCreator[creator] = CalculateCreatorTrackingDays(
                    reader.GetString(1),
                    reader.GetString(2));
                chargesByCreator[creator] = ListCreatorTrackingCharges(
                    subscriptions,
                    DeserializeCreatorTrackingPurchases(reader.GetString(4)));
            }
        }

        var exchangeRates = await EnsureCreatorTrackingExchangeRatesAsync(
            chargesByCreator.Values.SelectMany(charges => charges),
            displayCurrency,
            cancellationToken);
        var spendByCreator = creators.ToDictionary(
            creator => creator,
            creator => CalculateCreatorTrackingSpend(
                chargesByCreator[creator],
                displayCurrency,
                exchangeRates),
            StringComparer.OrdinalIgnoreCase);

        var aggregate = new CreatorTrackingDashboardAggregate(trackingDaysByCreator, spendByCreator);
        WriteDerivedDataCache(
            cacheKey,
            CreateCreatorTrackingDataSignature(),
            aggregate);
        return aggregate;
    }

    private string CreateCreatorTrackingDataSignature()
    {
        using var connection = OpenApplicationDataConnection();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        Append(CreatorTrackingDashboardCacheVersion);
        Append(DateOnly.FromDateTime(DateTime.Today).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT creator, last_checked_on, last_activity_on,
                       subscription_history_json, purchase_history_json,
                       monthly_support_amount, currency, support_started_on, support_ended_on
                FROM creator_tracking
                ORDER BY creator COLLATE NOCASE;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    Append(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT rate_date, base_currency, quote_currency, rate
                FROM creator_tracking_exchange_rates
                ORDER BY rate_date, base_currency, quote_currency;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    Append(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string CreateCreatorTrackingDashboardSourceSignature(string creator, string category)
    {
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT snapshot_date, file_count, image_count
            FROM creator_tracking_archive_snapshots
            WHERE creator = $creator COLLATE NOCASE
              AND category = $category
            ORDER BY snapshot_date;
            """;
        command.Parameters.AddWithValue("$creator", creator.Trim());
        command.Parameters.AddWithValue("$category", category.Trim());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(CreateCreatorTrackingDataSignature()));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                hash.AppendData(Encoding.UTF8.GetBytes(
                    Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty));
                hash.AppendData([0]);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void SaveCreatorTracking(CreatorTrackingDto tracking)
    {
        EnsureCreated();

        var creator = tracking.Creator.Trim();
        if (string.IsNullOrWhiteSpace(creator))
        {
            throw new ArgumentException("Creatorが指定されていません。", nameof(tracking));
        }

        using var connection = OpenApplicationDataConnection();
        var evaluationMetrics = NormalizeCreatorTrackingMetrics(tracking.EvaluationMetrics, tracking.PersonalRating);
        var personalRating = CalculateCreatorTrackingRating(evaluationMetrics);
        var subscriptionHistory = NormalizeCreatorTrackingSubscriptions(tracking.SubscriptionHistory);
        var purchaseHistory = NormalizeCreatorTrackingPurchases(tracking.PurchaseHistory);
        var storageLocations = NormalizeCreatorTrackingStorageLocations(
            tracking.StorageLocations,
            tracking.MainStoragePath,
            tracking.WorkStoragePath,
            tracking.UnsortedStoragePath);
        var mainStoragePath = storageLocations.FirstOrDefault(location => location.Usage == "Gallery")?.Path ?? string.Empty;
        var workStoragePath = storageLocations.FirstOrDefault(location => location.Usage == "Stockroom")?.Path ?? string.Empty;
        var primarySubscription = subscriptionHistory.FirstOrDefault(subscription => !subscription.Wishlist);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creator_tracking (
                creator,
                display_name,
                alternate_name,
                tracking_status,
                activity_status,
                follow_up_status,
                last_checked_on,
                last_activity_on,
                activity_summary,
                activity_links_json,
                main_storage_path,
                work_storage_path,
                unsorted_storage_path,
                storage_locations_json,
                evaluation_metrics_json,
                personal_rating,
                evaluation_memo,
                subscription_history_json,
                purchase_history_json,
                monthly_support_amount,
                lifetime_spend,
                currency,
                support_started_on,
                support_ended_on,
                support_memo)
            VALUES (
                $creator,
                $displayName,
                $alternateName,
                $trackingStatus,
                $activityStatus,
                $followUpStatus,
                $lastCheckedOn,
                $lastActivityOn,
                $activitySummary,
                $activityLinksJson,
                $mainStoragePath,
                $workStoragePath,
                $unsortedStoragePath,
                $storageLocationsJson,
                $evaluationMetricsJson,
                $personalRating,
                $evaluationMemo,
                $subscriptionHistoryJson,
                $purchaseHistoryJson,
                $monthlySupportAmount,
                $lifetimeSpend,
                $currency,
                $supportStartedOn,
                $supportEndedOn,
                $supportMemo)
            ON CONFLICT(creator) DO UPDATE SET
                display_name = excluded.display_name,
                alternate_name = excluded.alternate_name,
                tracking_status = excluded.tracking_status,
                activity_status = excluded.activity_status,
                follow_up_status = excluded.follow_up_status,
                last_checked_on = excluded.last_checked_on,
                last_activity_on = excluded.last_activity_on,
                activity_summary = excluded.activity_summary,
                activity_links_json = excluded.activity_links_json,
                main_storage_path = excluded.main_storage_path,
                work_storage_path = excluded.work_storage_path,
                unsorted_storage_path = excluded.unsorted_storage_path,
                storage_locations_json = excluded.storage_locations_json,
                evaluation_metrics_json = excluded.evaluation_metrics_json,
                personal_rating = excluded.personal_rating,
                evaluation_memo = excluded.evaluation_memo,
                subscription_history_json = excluded.subscription_history_json,
                purchase_history_json = excluded.purchase_history_json,
                monthly_support_amount = excluded.monthly_support_amount,
                lifetime_spend = excluded.lifetime_spend,
                currency = excluded.currency,
                support_started_on = excluded.support_started_on,
                support_ended_on = excluded.support_ended_on,
                support_memo = excluded.support_memo,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$creator", creator);
        command.Parameters.AddWithValue("$displayName", tracking.DisplayName.Trim());
        command.Parameters.AddWithValue("$alternateName", tracking.AlternateName.Trim());
        command.Parameters.AddWithValue("$trackingStatus", tracking.TrackingStatus.Trim());
        command.Parameters.AddWithValue("$activityStatus", tracking.ActivityStatus.Trim());
        command.Parameters.AddWithValue("$followUpStatus", tracking.FollowUpStatus.Trim());
        command.Parameters.AddWithValue("$lastCheckedOn", tracking.LastCheckedOn.Trim());
        command.Parameters.AddWithValue("$lastActivityOn", tracking.LastActivityOn.Trim());
        command.Parameters.AddWithValue("$activitySummary", tracking.ActivitySummary.Trim());
        command.Parameters.AddWithValue("$activityLinksJson", JsonSerializer.Serialize(
            NormalizeCreatorTrackingActivityLinks(tracking.ActivityLinks),
            JsonOptions));
        command.Parameters.AddWithValue("$mainStoragePath", mainStoragePath);
        command.Parameters.AddWithValue("$workStoragePath", workStoragePath);
        command.Parameters.AddWithValue("$unsortedStoragePath", tracking.UnsortedStoragePath.Trim());
        command.Parameters.AddWithValue("$storageLocationsJson", JsonSerializer.Serialize(storageLocations, JsonOptions));
        command.Parameters.AddWithValue("$evaluationMetricsJson", JsonSerializer.Serialize(evaluationMetrics, JsonOptions));
        command.Parameters.AddWithValue("$personalRating", personalRating);
        command.Parameters.AddWithValue("$evaluationMemo", tracking.EvaluationMemo.Trim());
        command.Parameters.AddWithValue("$subscriptionHistoryJson", JsonSerializer.Serialize(subscriptionHistory, JsonOptions));
        command.Parameters.AddWithValue("$purchaseHistoryJson", JsonSerializer.Serialize(purchaseHistory, JsonOptions));
        command.Parameters.AddWithValue("$monthlySupportAmount", primarySubscription?.Amount ?? Math.Max(0, tracking.MonthlySupportAmount));
        command.Parameters.AddWithValue("$lifetimeSpend", Math.Max(0, tracking.LifetimeSpend));
        command.Parameters.AddWithValue("$currency", primarySubscription?.Currency ?? NormalizeCreatorTrackingCurrency(tracking.Currency));
        command.Parameters.AddWithValue("$supportStartedOn", primarySubscription?.StartedOn ?? tracking.SupportStartedOn.Trim());
        command.Parameters.AddWithValue("$supportEndedOn", primarySubscription?.RenewalOn ?? tracking.SupportEndedOn.Trim());
        command.Parameters.AddWithValue("$supportMemo", tracking.SupportMemo.Trim());
        command.ExecuteNonQuery();
    }

    public CreatorDataDeleteResultDto DeleteCreatorData(string creator)
    {
        EnsureCreated();

        var normalizedCreator = creator.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCreator))
        {
            throw new ArgumentException("Creatorが指定されていません。", nameof(creator));
        }

        using var connection = OpenApplicationDataConnection();
        using var transaction = connection.BeginTransaction();
        using (var createTemp = connection.CreateCommand())
        {
            createTemp.Transaction = transaction;
            createTemp.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS creator_data_delete_gids (
                    gid TEXT PRIMARY KEY
                );
                DELETE FROM creator_data_delete_gids;
                INSERT INTO creator_data_delete_gids (gid)
                SELECT gid
                FROM items
                WHERE creator = $creator COLLATE NOCASE;
                """;
            createTemp.Parameters.AddWithValue("$creator", normalizedCreator);
            createTemp.ExecuteNonQuery();
        }

        var itemTagsDeleted = ExecuteCreatorDataDelete(connection, transaction, """
            DELETE FROM item_tags
            WHERE gid IN (SELECT gid FROM creator_data_delete_gids);
            """);
        var itemEventsDeleted = ExecuteCreatorDataDelete(connection, transaction, """
            DELETE FROM item_events
            WHERE gid IN (SELECT gid FROM creator_data_delete_gids);
            """);
        var worksDeleted = ExecuteCreatorDataDelete(connection, transaction, """
            DELETE FROM items
            WHERE gid IN (SELECT gid FROM creator_data_delete_gids);
            """);
        var archiveSnapshotsDeleted = ExecuteCreatorDataDelete(
            connection,
            transaction,
            "DELETE FROM creator_tracking_archive_snapshots WHERE creator = $creator COLLATE NOCASE;",
            normalizedCreator);
        var creatorTrackingRowsDeleted = ExecuteCreatorDataDelete(
            connection,
            transaction,
            "DELETE FROM creator_tracking WHERE creator = $creator COLLATE NOCASE;",
            normalizedCreator);

        var stickyNoteIds = new List<long>();
        using (var selectStickyNotes = connection.CreateCommand())
        {
            selectStickyNotes.Transaction = transaction;
            selectStickyNotes.CommandText = """
                SELECT note_id
                FROM sticky_notes
                WHERE view_type = 'creatorTracking'
                  AND context_key = $contextKey COLLATE NOCASE;
                """;
            selectStickyNotes.Parameters.AddWithValue("$contextKey", normalizedCreator.ToLowerInvariant());
            using var reader = selectStickyNotes.ExecuteReader();
            while (reader.Read())
            {
                stickyNoteIds.Add(reader.GetInt64(0));
            }
        }

        foreach (var stickyNoteId in stickyNoteIds)
        {
            UpdateBookmarkStickyNoteSnapshots(connection, transaction, new StickyNoteDto(
                stickyNoteId,
                "creatorTracking",
                normalizedCreator,
                normalizedCreator,
                string.Empty,
                0,
                0,
                250,
                200,
                "amber",
                "plain",
                string.Empty,
                string.Empty), delete: true);
        }

        var stickyNotesDeleted = ExecuteCreatorDataDelete(
            connection,
            transaction,
            """
            DELETE FROM sticky_notes
            WHERE view_type = 'creatorTracking'
              AND context_key = $contextKey COLLATE NOCASE;
            """,
            parameterName: "$contextKey",
            parameterValue: normalizedCreator.ToLowerInvariant());

        transaction.Commit();
        ClearGalleryDerivedCaches();

        return new CreatorDataDeleteResultDto(
            normalizedCreator,
            creatorTrackingRowsDeleted,
            worksDeleted,
            itemTagsDeleted,
            itemEventsDeleted,
            archiveSnapshotsDeleted,
            stickyNotesDeleted);
    }

    private static int ExecuteCreatorDataDelete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        string? creator = null,
        string parameterName = "$creator",
        string? parameterValue = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        var value = parameterValue ?? creator;
        if (value is not null)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }
        return command.ExecuteNonQuery();
    }

    private static bool HasLegacyCreatorTrackingSubscription(SqliteDataReader reader)
    {
        return reader.GetDouble(18) > 0 ||
               reader.GetDouble(19) > 0 ||
               !string.IsNullOrWhiteSpace(reader.GetString(21)) ||
               !string.IsNullOrWhiteSpace(reader.GetString(22)) ||
               !string.IsNullOrWhiteSpace(reader.GetString(23));
    }

    private static bool HasLegacyCreatorTrackingSubscription(
        double amount,
        string startedOn,
        string renewalOn)
    {
        return amount > 0 ||
               !string.IsNullOrWhiteSpace(startedOn) ||
               !string.IsNullOrWhiteSpace(renewalOn);
    }

    private static int CalculateCreatorTrackingDays(string startedOn, string checkedOn)
    {
        return TryParseCreatorTrackingDate(startedOn, out var started) &&
               TryParseCreatorTrackingDate(checkedOn, out var checkedDate)
            ? Math.Max(0, checkedDate.DayNumber - started.DayNumber)
            : 0;
    }

    private static IReadOnlyList<CreatorTrackingCharge> ListCreatorTrackingCharges(
        IReadOnlyList<CreatorTrackingSubscriptionDto>? subscriptions,
        IReadOnlyList<CreatorTrackingPurchaseDto>? purchases)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var charges = new List<CreatorTrackingCharge>();

        foreach (var purchase in NormalizeCreatorTrackingPurchases(purchases))
        {
            if (!purchase.Wishlist &&
                purchase.Amount > 0 &&
                TryParseCreatorTrackingDate(purchase.PurchasedOn, out var purchasedOn) &&
                purchasedOn <= today)
            {
                charges.Add(new CreatorTrackingCharge(purchasedOn, purchase.Amount, purchase.Currency));
            }
        }

        foreach (var subscription in NormalizeCreatorTrackingSubscriptions(subscriptions))
        {
            if (subscription.Wishlist ||
                subscription.Amount <= 0 ||
                !TryParseCreatorTrackingDate(subscription.StartedOn, out var startedOn) ||
                startedOn > today)
            {
                continue;
            }

            var intervalMonths = subscription.BillingFrequency switch
            {
                "quarterly" => 3,
                "semiannual" => 6,
                "annual" => 12,
                _ => 1
            };
            var lastChargeDate = today;
            if (subscription.EndingPlanned)
            {
                lastChargeDate = TryParseCreatorTrackingDate(subscription.RenewalOn, out var renewalOn)
                    ? DateOnly.FromDayNumber(Math.Max(startedOn.DayNumber, renewalOn.DayNumber - 1))
                    : startedOn;
            }

            for (var cycle = 0; cycle < 1200; cycle++)
            {
                var chargeDate = AddCreatorTrackingBillingMonths(startedOn, cycle * intervalMonths);
                if (chargeDate > lastChargeDate || chargeDate > today)
                {
                    break;
                }

                charges.Add(new CreatorTrackingCharge(chargeDate, subscription.Amount, subscription.Currency));
            }
        }

        return charges;
    }

    private static CreatorTrackingSpendSummary CalculateCreatorTrackingSpend(
        IReadOnlyList<CreatorTrackingCharge> charges,
        string displayCurrency,
        IReadOnlyDictionary<string, double> exchangeRates)
    {
        var currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        var recentCutoff = currentMonth.AddMonths(-2);
        var total = 0d;
        var recentTotal = 0d;
        var hasMissingRates = false;

        foreach (var charge in charges)
        {
            var rate = 1d;
            if (!charge.Currency.Equals(displayCurrency, StringComparison.OrdinalIgnoreCase) &&
                !exchangeRates.TryGetValue(
                    CreateCreatorTrackingExchangeRateKey(charge.Date, charge.Currency, displayCurrency),
                    out rate))
            {
                hasMissingRates = true;
                continue;
            }

            var convertedAmount = charge.Amount * rate;
            total += convertedAmount;
            if (charge.Date >= recentCutoff)
            {
                recentTotal += convertedAmount;
            }
        }

        return new CreatorTrackingSpendSummary(total, recentTotal, hasMissingRates);
    }

    private async Task<IReadOnlyDictionary<string, double>> EnsureCreatorTrackingExchangeRatesAsync(
        IEnumerable<CreatorTrackingCharge> charges,
        string displayCurrency,
        CancellationToken cancellationToken)
    {
        var requiredRates = charges
            .Where(charge => !charge.Currency.Equals(displayCurrency, StringComparison.OrdinalIgnoreCase))
            .Select(charge => (charge.Date, Base: charge.Currency, Quote: displayCurrency))
            .Distinct()
            .ToArray();
        var rates = LoadCreatorTrackingExchangeRates();

        foreach (var requiredRate in requiredRates)
        {
            var key = CreateCreatorTrackingExchangeRateKey(
                requiredRate.Date,
                requiredRate.Base,
                requiredRate.Quote);
            if (rates.ContainsKey(key))
            {
                continue;
            }

            var rate = await FetchCreatorTrackingExchangeRateAsync(
                requiredRate.Date,
                requiredRate.Base,
                requiredRate.Quote,
                cancellationToken);
            if (rate is not > 0)
            {
                continue;
            }

            SaveCreatorTrackingExchangeRate(
                requiredRate.Date,
                requiredRate.Base,
                requiredRate.Quote,
                rate.Value);
            rates[key] = rate.Value;
        }

        return rates;
    }

    private Dictionary<string, double> LoadCreatorTrackingExchangeRates()
    {
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rate_date, base_currency, quote_currency, rate
            FROM creator_tracking_exchange_rates;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (TryParseCreatorTrackingDate(reader.GetString(0), out var date))
            {
                rates[CreateCreatorTrackingExchangeRateKey(date, reader.GetString(1), reader.GetString(2))] = reader.GetDouble(3);
            }
        }
        return rates;
    }

    private void SaveCreatorTrackingExchangeRate(DateOnly date, string baseCurrency, string quoteCurrency, double rate)
    {
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creator_tracking_exchange_rates (
                rate_date, base_currency, quote_currency, rate, provider)
            VALUES ($date, $baseCurrency, $quoteCurrency, $rate, 'Frankfurter/ECB')
            ON CONFLICT(rate_date, base_currency, quote_currency) DO UPDATE SET
                rate = excluded.rate,
                provider = excluded.provider,
                fetched_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$baseCurrency", NormalizeCreatorTrackingCurrency(baseCurrency));
        command.Parameters.AddWithValue("$quoteCurrency", NormalizeCreatorTrackingCurrency(quoteCurrency));
        command.Parameters.AddWithValue("$rate", rate);
        command.ExecuteNonQuery();
    }

    private static async Task<double?> FetchCreatorTrackingExchangeRateAsync(
        DateOnly requestedDate,
        string baseCurrency,
        string quoteCurrency,
        CancellationToken cancellationToken)
    {
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var rateDate = requestedDate.AddDays(-dayOffset);
            var url = $"https://api.frankfurter.dev/v2/rate/{NormalizeCreatorTrackingCurrency(baseCurrency)}/{NormalizeCreatorTrackingCurrency(quoteCurrency)}?date={rateDate:yyyy-MM-dd}&providers=ECB";
            try
            {
                using var response = await CreatorTrackingExchangeRateHttpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (document.RootElement.TryGetProperty("rate", out var rateProperty) &&
                    rateProperty.TryGetDouble(out var rate) &&
                    rate > 0)
                {
                    return rate;
                }
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        return null;
    }

    private static HttpClient CreateCreatorTrackingExchangeRateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GalleryBrowser/0.1");
        return client;
    }

    private static string CreateCreatorTrackingExchangeRateKey(
        DateOnly date,
        string baseCurrency,
        string quoteCurrency) =>
        $"{date:yyyy-MM-dd}|{NormalizeCreatorTrackingCurrency(baseCurrency)}|{NormalizeCreatorTrackingCurrency(quoteCurrency)}";

    private void SaveCreatorTrackingArchiveSnapshot(
        string creator,
        string category,
        int fileCount,
        int imageCount)
    {
        using var connection = OpenApplicationDataConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO creator_tracking_archive_snapshots (
                creator, category, snapshot_date, file_count, image_count)
            VALUES ($creator, $category, $snapshotDate, $fileCount, $imageCount)
            ON CONFLICT(creator, category, snapshot_date) DO UPDATE SET
                file_count = excluded.file_count,
                image_count = excluded.image_count
            WHERE creator_tracking_archive_snapshots.file_count <> excluded.file_count
               OR creator_tracking_archive_snapshots.image_count <> excluded.image_count;
            """;
        command.Parameters.AddWithValue("$creator", creator.Trim());
        command.Parameters.AddWithValue("$category", category.Trim());
        command.Parameters.AddWithValue("$snapshotDate", DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$fileCount", Math.Max(0, fileCount));
        command.Parameters.AddWithValue("$imageCount", Math.Max(0, imageCount));
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<CreatorTrackingArchiveMonthDto> ListCreatorTrackingArchiveMonths(
        string creator,
        string category,
        string startedOn)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var startMonth = TryParseCreatorTrackingDate(startedOn, out var startedDate)
            ? new DateOnly(startedDate.Year, startedDate.Month, 1)
            : currentMonth;
        if (startMonth > currentMonth)
        {
            startMonth = currentMonth;
        }

        var snapshotsByMonth = new Dictionary<string, (int FileCount, int ImageCount)>(StringComparer.Ordinal);
        var baseline = (FileCount: 0, ImageCount: 0);
        using (var connection = OpenApplicationDataConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT snapshot_date, file_count, image_count
                FROM creator_tracking_archive_snapshots
                WHERE creator = $creator COLLATE NOCASE
                  AND category = $category
                  AND snapshot_date <= $today
                ORDER BY snapshot_date;
                """;
            command.Parameters.AddWithValue("$creator", creator.Trim());
            command.Parameters.AddWithValue("$category", category.Trim());
            command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryParseCreatorTrackingDate(reader.GetString(0), out var snapshotDate))
                {
                    continue;
                }

                var snapshot = (reader.GetInt32(1), reader.GetInt32(2));
                if (snapshotDate < startMonth)
                {
                    baseline = snapshot;
                    continue;
                }

                snapshotsByMonth[snapshotDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)] = snapshot;
            }
        }

        var result = new List<CreatorTrackingArchiveMonthDto>();
        var current = baseline;
        for (var month = startMonth; month <= currentMonth; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (snapshotsByMonth.TryGetValue(key, out var snapshot))
            {
                current = snapshot;
            }
            result.Add(new CreatorTrackingArchiveMonthDto(key, current.FileCount, current.ImageCount));
        }
        return result;
    }

    private IReadOnlyList<CreatorTrackingArchiveSnapshotDto> ListCreatorTrackingArchiveSnapshots(
        string creator,
        string category,
        string startedOn)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var startDate = TryParseCreatorTrackingDate(startedOn, out var parsedStartDate)
            ? parsedStartDate
            : new DateOnly(today.Year, today.Month, 1);
        if (startDate > today)
        {
            startDate = today;
        }

        var snapshotsByDate = new Dictionary<DateOnly, (int FileCount, int ImageCount)>();
        var baseline = (FileCount: 0, ImageCount: 0);
        using (var connection = OpenApplicationDataConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT snapshot_date, file_count, image_count
                FROM creator_tracking_archive_snapshots
                WHERE creator = $creator COLLATE NOCASE
                  AND category = $category
                  AND snapshot_date <= $today
                ORDER BY snapshot_date;
                """;
            command.Parameters.AddWithValue("$creator", creator.Trim());
            command.Parameters.AddWithValue("$category", category.Trim());
            command.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryParseCreatorTrackingDate(reader.GetString(0), out var snapshotDate))
                {
                    continue;
                }

                var snapshot = (reader.GetInt32(1), reader.GetInt32(2));
                if (snapshotDate < startDate)
                {
                    baseline = snapshot;
                    continue;
                }

                snapshotsByDate[snapshotDate] = snapshot;
            }
        }

        var result = new List<CreatorTrackingArchiveSnapshotDto>();
        var current = baseline;
        for (var date = startDate; date <= today; date = date.AddDays(1))
        {
            if (snapshotsByDate.TryGetValue(date, out var snapshot))
            {
                current = snapshot;
            }
            result.Add(new CreatorTrackingArchiveSnapshotDto(
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                current.FileCount,
                current.ImageCount));
        }
        return result;
    }

    private static DateOnly AddCreatorTrackingBillingMonths(DateOnly date, int months)
    {
        var firstDay = new DateOnly(date.Year, date.Month, 1).AddMonths(months);
        return new DateOnly(
            firstDay.Year,
            firstDay.Month,
            Math.Min(date.Day, DateTime.DaysInMonth(firstDay.Year, firstDay.Month)));
    }

    private static bool TryParseCreatorTrackingDate(string value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static int CalculateCreatorTrackingRank(IEnumerable<int> values, int currentValue) =>
        1 + values.Count(value => value > currentValue);

    private static int CalculateCreatorTrackingRank(IEnumerable<double> values, double currentValue) =>
        1 + values.Count(value => value > currentValue);

    private static int NormalizeCreatorTrackingRank(int rank, int creatorCount) =>
        Math.Clamp(rank <= 0 ? creatorCount : rank, 1, creatorCount);

    private static IReadOnlyList<CreatorTrackingSubscriptionDto> DeserializeCreatorTrackingSubscriptions(string json)
    {
        try
        {
            var subscriptions = JsonSerializer.Deserialize<CreatorTrackingSubscriptionDto[]>(json, JsonOptions);
            return NormalizeCreatorTrackingSubscriptions(subscriptions);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CreatorTrackingActivityLinkDto> DeserializeCreatorTrackingActivityLinks(string json)
    {
        try
        {
            var links = JsonSerializer.Deserialize<CreatorTrackingActivityLinkDto[]>(json, JsonOptions);
            return NormalizeCreatorTrackingActivityLinks(links);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CreatorTrackingActivityLinkDto> NormalizeCreatorTrackingActivityLinks(
        IReadOnlyList<CreatorTrackingActivityLinkDto>? links)
    {
        return (links ?? [])
            .Select(link => new CreatorTrackingActivityLinkDto(
                link.Label.Trim(),
                link.Url.Trim(),
                string.IsNullOrWhiteSpace(link.Status) ? "確認中" : link.Status.Trim(),
                link.Note.Trim(),
                link.FollowUpEnabled,
                Math.Clamp(link.FollowUpDays, 1, 99)))
            .ToArray();
    }

    private static IReadOnlyList<CreatorTrackingStorageLocationDto> DeserializeCreatorTrackingStorageLocations(
        string json,
        string mainStoragePath,
        string workStoragePath,
        string unsortedStoragePath)
    {
        try
        {
            var locations = JsonSerializer.Deserialize<CreatorTrackingStorageLocationDto[]>(json, JsonOptions);
            return NormalizeCreatorTrackingStorageLocations(
                locations,
                mainStoragePath,
                workStoragePath,
                unsortedStoragePath);
        }
        catch (JsonException)
        {
            return NormalizeCreatorTrackingStorageLocations(
                null,
                mainStoragePath,
                workStoragePath,
                unsortedStoragePath);
        }
    }

    private static IReadOnlyList<CreatorTrackingStorageLocationDto> NormalizeCreatorTrackingStorageLocations(
        IReadOnlyList<CreatorTrackingStorageLocationDto>? locations,
        string? mainStoragePath,
        string? workStoragePath,
        string? unsortedStoragePath)
    {
        var source = locations?.Count > 0
            ? locations
            : CreateLegacyCreatorTrackingStorageLocations(mainStoragePath, workStoragePath, unsortedStoragePath);
        var normalized = new List<CreatorTrackingStorageLocationDto>();
        var singletonUsages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in source)
        {
            var usage = location.Usage?.Trim().ToLowerInvariant() switch
            {
                "gallery" => "Gallery",
                "stockroom" => "Stockroom",
                "temporary" => "Temporary",
                _ => string.Empty
            };
            if (string.IsNullOrEmpty(usage) ||
                (usage != "Temporary" && !singletonUsages.Add(usage)))
            {
                continue;
            }

            normalized.Add(new CreatorTrackingStorageLocationDto(
                string.IsNullOrWhiteSpace(location.Id) ? Guid.NewGuid().ToString("N") : location.Id.Trim(),
                usage,
                location.Path?.Trim() ?? string.Empty));
        }

        if (!normalized.Any(location => location.Usage == "Gallery"))
        {
            normalized.Insert(0, new CreatorTrackingStorageLocationDto(
                Guid.NewGuid().ToString("N"),
                "Gallery",
                mainStoragePath?.Trim() ?? string.Empty));
        }
        return normalized;
    }

    private static IReadOnlyList<CreatorTrackingStorageLocationDto> CreateLegacyCreatorTrackingStorageLocations(
        string? mainStoragePath,
        string? workStoragePath,
        string? unsortedStoragePath)
    {
        var locations = new List<CreatorTrackingStorageLocationDto>
        {
            new(Guid.NewGuid().ToString("N"), "Gallery", mainStoragePath?.Trim() ?? string.Empty)
        };
        if (!string.IsNullOrWhiteSpace(workStoragePath))
        {
            locations.Add(new CreatorTrackingStorageLocationDto(
                Guid.NewGuid().ToString("N"),
                "Stockroom",
                workStoragePath.Trim()));
        }
        if (!string.IsNullOrWhiteSpace(unsortedStoragePath))
        {
            locations.Add(new CreatorTrackingStorageLocationDto(
                Guid.NewGuid().ToString("N"),
                "Temporary",
                unsortedStoragePath.Trim()));
        }
        return locations;
    }

    private static IReadOnlyList<CreatorTrackingPurchaseDto> DeserializeCreatorTrackingPurchases(string json)
    {
        try
        {
            var purchases = JsonSerializer.Deserialize<CreatorTrackingPurchaseDto[]>(json, JsonOptions);
            return NormalizeCreatorTrackingPurchases(purchases);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<CreatorTrackingSubscriptionDto> NormalizeCreatorTrackingSubscriptions(
        IReadOnlyList<CreatorTrackingSubscriptionDto>? subscriptions)
    {
        return (subscriptions ?? [])
            .Select(subscription => new CreatorTrackingSubscriptionDto(
                string.IsNullOrWhiteSpace(subscription.Id) ? Guid.NewGuid().ToString("N") : subscription.Id.Trim(),
                subscription.Platform.Trim(),
                subscription.Plan?.Trim() ?? string.Empty,
                NormalizeCreatorTrackingCurrency(subscription.Currency),
                Math.Max(0, subscription.Amount),
                NormalizeCreatorTrackingBillingFrequency(subscription.BillingFrequency),
                subscription.StartedOn.Trim(),
                subscription.RenewalOn.Trim(),
                !subscription.Wishlist && subscription.EndingPlanned,
                !subscription.Wishlist && subscription.Reminder)
            {
                Wishlist = subscription.Wishlist,
                IsActive = !subscription.Wishlist && subscription.IsActive
            })
            .ToArray();
    }

    private static IReadOnlyList<CreatorTrackingPurchaseDto> NormalizeCreatorTrackingPurchases(
        IReadOnlyList<CreatorTrackingPurchaseDto>? purchases)
    {
        return (purchases ?? [])
            .Select(purchase => new CreatorTrackingPurchaseDto(
                string.IsNullOrWhiteSpace(purchase.Id) ? Guid.NewGuid().ToString("N") : purchase.Id.Trim(),
                purchase.Platform.Trim(),
                purchase.ProductName.Trim(),
                NormalizeCreatorTrackingCurrency(purchase.Currency),
                Math.Max(0, purchase.Amount),
                purchase.PurchasedOn.Trim())
            {
                Wishlist = purchase.Wishlist
            })
            .ToArray();
    }

    private static string NormalizeCreatorTrackingCurrency(string currency)
    {
        return string.IsNullOrWhiteSpace(currency) ? "JPY" : currency.Trim().ToUpperInvariant();
    }

    private static string NormalizeCreatorTrackingBillingFrequency(string frequency)
    {
        return frequency.Trim().ToLowerInvariant() switch
        {
            "quarterly" => "quarterly",
            "semiannual" => "semiannual",
            "annual" => "annual",
            _ => "monthly"
        };
    }

    private static (string DisplayName, string AlternateName) SplitLegacyCreatorTrackingDisplayName(
        string creator,
        string displayName,
        string alternateName)
    {
        var normalizedDisplayName = displayName.Trim();
        var normalizedAlternateName = alternateName.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedAlternateName))
        {
            return (normalizedDisplayName, normalizedAlternateName);
        }

        foreach (var (open, close) in new[] { ('(', ')'), ('（', '）') })
        {
            var openIndex = normalizedDisplayName.LastIndexOf(open);
            if (openIndex <= 0 || normalizedDisplayName[^1] != close)
            {
                continue;
            }

            var baseName = normalizedDisplayName[..openIndex].Trim();
            var alias = normalizedDisplayName[(openIndex + 1)..^1].Trim();
            if (baseName.Equals(creator, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(alias))
            {
                return (baseName, alias);
            }
        }

        return (normalizedDisplayName, normalizedAlternateName);
    }

    private static IReadOnlyDictionary<string, int> CreateDefaultCreatorTrackingMetrics()
    {
        return CreatorTrackingMetricKeys.ToDictionary(key => key, _ => 1, StringComparer.OrdinalIgnoreCase);
    }

    private static CreatorTrackingDto CreateDefaultCreatorTracking(string creator)
    {
        return new CreatorTrackingDto(
            creator,
            creator,
            string.Empty,
            "未設定",
            "不明",
            "未確認",
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            CreateDefaultCreatorTrackingMetrics(),
            1.25,
            string.Empty,
            [],
            [],
            0,
            0,
            "JPY",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
    }

    private static IReadOnlyDictionary<string, int> NormalizeCreatorTrackingMetrics(
        IReadOnlyDictionary<string, int>? metrics,
        double legacyRating = 0)
    {
        var fallback = legacyRating > 0
            ? Math.Clamp((int)Math.Round(legacyRating / 1.25, MidpointRounding.AwayFromZero), 1, 4)
            : 1;

        return CreatorTrackingMetricKeys.ToDictionary(
            key => key,
            key => metrics is not null && metrics.TryGetValue(key, out var value)
                ? Math.Clamp(value, 1, 4)
                : fallback,
            StringComparer.OrdinalIgnoreCase);
    }

    private double CalculateCreatorTrackingRating(IReadOnlyDictionary<string, int> metrics)
    {
        var weightedRating = GetCreatorTrackingMetricSettings()
            .Where(metric => metric.WeightPercent is > 0)
            .Sum(metric => metrics[metric.Key] * metric.WeightPercent!.Value / 100d);
        return Math.Floor((weightedRating * 1.25 + 1e-9) * 10) / 10;
    }

    public void SaveWindowLayout(double width, double height, bool isMaximized)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ui_navigation_state (id, window_width, window_height, window_is_maximized)
            VALUES (1, $width, $height, $isMaximized)
            ON CONFLICT(id) DO UPDATE SET
                window_width = excluded.window_width,
                window_height = excluded.window_height,
                window_is_maximized = excluded.window_is_maximized;
            """;
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$isMaximized", isMaximized ? 1 : 0);
        command.ExecuteNonQuery();
        PersistUiState();
    }

    public IReadOnlyList<ExplorerTabStateDto> ListExplorerTabs()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT position, path, label, is_active FROM explorer_tabs ORDER BY position;";

        using var reader = command.ExecuteReader();
        var tabs = new List<ExplorerTabStateDto>();
        while (reader.Read())
        {
            tabs.Add(new ExplorerTabStateDto(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0));
        }

        return tabs;
    }

    public void SaveExplorerTabs(IReadOnlyList<ExplorerTabStateDto> tabs)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM explorer_tabs;";
            clear.ExecuteNonQuery();
        }

        foreach (var tab in tabs)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO explorer_tabs (position, path, label, is_active)
                VALUES ($position, $path, $label, $is_active);
                """;
            insert.Parameters.AddWithValue("$position", tab.Position);
            insert.Parameters.AddWithValue("$path", Path.GetFullPath(tab.Path));
            insert.Parameters.AddWithValue("$label", tab.Label);
            insert.Parameters.AddWithValue("$is_active", tab.IsActive ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        PersistUiState();
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }

        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }

    public IReadOnlyList<GalleryItemDto> ListItems()
    {
        if (File.Exists(ExternalGalleryDatabasePath))
        {
            var externalItems = TryListExternalItems();
            if (externalItems.Count > 0)
            {
                return externalItems;
            }
        }

        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                i.id,
                i.title,
                i.kind,
                i.path,
                i.item_count,
                i.accent,
                COALESCE(group_concat(t.tag, '|'), '') AS tags
            FROM gallery_items AS i
            LEFT JOIN gallery_item_tags AS t ON t.item_id = i.id
            GROUP BY i.id
            ORDER BY i.updated_at DESC, i.id DESC;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<GalleryItemDto>();
        while (reader.Read())
        {
            var tags = reader.GetString(6)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            items.Add(new GalleryItemDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                tags,
                reader.GetString(5),
                null));
        }

        return items;
    }

    public GalleryWorksPageDto ListGalleryWorks(
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        string sort,
        string filterSort,
        int offset,
        int pageSize)
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryWorksPageDto([], 0, [], [], [], [], []);
        }

        EnsureExternalGalleryGidSchema();

        try
        {
            category = category.Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                return new GalleryWorksPageDto([], 0, [], [], [], [], []);
            }

            offset = Math.Max(0, offset);
            pageSize = Math.Clamp(pageSize, 1, 200);
            var targetPaths = ListGalleryScanTargets(category)
                .Select(target => NormalizeGalleryTargetPath(target.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var supportedExtensions = GetGalleryScanSettings(category).SupportedExtensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cacheKey = CreateDerivedDataCacheKey("gallery-works", new
            {
                Version = CreateExternalDataSignature(GalleryWorksCacheVersion),
                Category = category.ToLowerInvariant(),
                TargetPaths = targetPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                SupportedExtensions = supportedExtensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Ratings = ratings.Order().ToArray(),
                Tags = tags.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Creators = creators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Titles = titles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Characters = characters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Sort = sort,
                Offset = offset,
                PageSize = pageSize
            });
            if (_galleryWorksCache.TryGet(cacheKey, out var cachedPage))
            {
                return cachedPage;
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ExternalGalleryDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ConnectionString);
            connection.Open();

            var where = BuildGalleryWorkWhere(
                ratings,
                tags,
                creators,
                titles,
                characters,
                targetPaths,
                supportedExtensions);
            var orderBy = BuildGalleryWorkOrderBy(sort);
            var titleSelect = BuildGalleryWorkAttributeSelectExpression("title", "title");
            var characterSelect = BuildGalleryWorkAttributeSelectExpression("character", "character");

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText = $"SELECT COUNT(*) FROM items AS i WHERE {where};";
            AddGalleryWorkParameters(countCommand, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions);
            var total = Convert.ToInt32((long)(countCommand.ExecuteScalar() ?? 0));

            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT
                    i.gid AS id,
                    i.current_path,
                    COALESCE(i.source_zip_name, ''),
                    COALESCE(i.category, ''),
                    COALESCE(i.top_folder, ''),
                    COALESCE(i.creator, ''),
                    {titleSelect},
                    {characterSelect},
                    COALESCE(i.rating, 0),
                    COALESCE(i.image_count, 0),
                    i.duration_seconds,
                    COALESCE(i.last_access_time, ''),
                    COALESCE(i.last_write_time, ''),
                    COALESCE(group_concat(t.tag, '|'), '') AS tags
                FROM items AS i
                LEFT JOIN item_tags AS it ON it.gid = i.gid
                LEFT JOIN tags AS t ON t.tag_id = it.tag_id
                WHERE {where}
                GROUP BY i.gid
                ORDER BY {orderBy}
                LIMIT $limit OFFSET $offset;
                """;
            AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions);
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", offset);

            using var reader = command.ExecuteReader();
            var items = new List<GalleryWorkDto>();
            while (reader.Read())
            {
                items.Add(new GalleryWorkDto(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetInt32(8),
                    reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.GetString(13).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }

            var page = new GalleryWorksPageDto(
                items,
                total,
                [],
                [],
                [],
                [],
                []);
            _galleryWorksCache.Set(cacheKey, page);
            return page;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Gallery用SQLiteDBの評価フィルタを読み込めませんでした。", ex);
        }
    }

    public GalleryWorkFiltersDto ListGalleryWorkFilters(
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        string filterSort,
        IReadOnlyList<string>? requestedParts = null)
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryWorkFiltersDto([], [], [], [], []);
        }

        EnsureExternalGalleryGidSchema();

        try
        {
            category = category.Trim();
            if (string.IsNullOrWhiteSpace(category))
            {
                return new GalleryWorkFiltersDto([], [], [], [], []);
            }

            var targetPaths = ListGalleryScanTargets(category)
                .Select(target => NormalizeGalleryTargetPath(target.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var supportedExtensions = GetGalleryScanSettings(category).SupportedExtensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(NormalizeExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var cacheKey = CreateDerivedDataCacheKey("gallery-filters", new
            {
                Version = CreateExternalDataSignature(GalleryFiltersCacheVersion),
                Category = category.ToLowerInvariant(),
                TargetPaths = targetPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                SupportedExtensions = supportedExtensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Ratings = ratings.Order().ToArray(),
                Tags = tags.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Creators = creators.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Titles = titles.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Characters = characters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                FilterSort = filterSort,
                RequestedParts = (requestedParts ?? []).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            });
            if (_galleryFiltersCache.TryGet(cacheKey, out var cachedFilters))
            {
                return cachedFilters;
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ExternalGalleryDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ConnectionString);
            connection.Open();

            var parts = requestedParts is { Count: > 0 }
                ? requestedParts.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            bool Includes(string part) => parts is null || parts.Contains(part);

            var filters = new GalleryWorkFiltersDto(
                Includes("ratings") ? ListGalleryRatingOptions(connection, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions) : [],
                Includes("tags") ? ListGalleryTagOptions(connection, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, filterSort) : [],
                Includes("creators") ? ListGalleryFilterOptions(connection, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "creator", "COALESCE(i.creator, '')", "creator", filterSort) : [],
                Includes("titles") ? ListGalleryTitleOptions(connection, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, filterSort) : [],
                Includes("characters") && titles.Count == 1
                    ? ListGalleryCharacterOptions(connection, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, filterSort)
                    : []);
            _galleryFiltersCache.Set(cacheKey, filters);
            return filters;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Gallery用SQLiteDBのフィルタ候補を読み込めませんでした。", ex);
        }
    }

    private IReadOnlyDictionary<string, CreatorTrackingSummaryFacts> ListCreatorTrackingSummaryFacts(
        SqliteConnection connection)
    {
        var exchangeRates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using (var rateCommand = connection.CreateCommand())
        {
            rateCommand.CommandText = "SELECT rate_date, base_currency, quote_currency, rate FROM creator_tracking_exchange_rates;";
            using var rateReader = rateCommand.ExecuteReader();
            while (rateReader.Read())
            {
                if (TryParseCreatorTrackingDate(rateReader.GetString(0), out var rateDate))
                {
                    exchangeRates[CreateCreatorTrackingExchangeRateKey(
                        rateDate,
                        rateReader.GetString(1),
                        rateReader.GetString(2))] = rateReader.GetDouble(3);
                }
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var facts = new Dictionary<string, CreatorTrackingSummaryFacts>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                creator,
                last_checked_on,
                last_activity_on,
                activity_links_json,
                evaluation_metrics_json,
                personal_rating,
                subscription_history_json,
                purchase_history_json,
                monthly_support_amount,
                currency,
                support_started_on,
                support_ended_on
            FROM creator_tracking;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var creator = reader.GetString(0).Trim();
            if (string.IsNullOrWhiteSpace(creator))
            {
                continue;
            }

            var lastCheckedOn = reader.GetString(1);
            var lastActivityOn = reader.GetString(2);
            var activityLinks = DeserializeCreatorTrackingActivityLinks(reader.GetString(3));
            IReadOnlyDictionary<string, int>? storedMetrics;
            try
            {
                storedMetrics = JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(4), JsonOptions);
            }
            catch (JsonException)
            {
                storedMetrics = null;
            }

            var evaluationMetrics = NormalizeCreatorTrackingMetrics(storedMetrics, reader.GetDouble(5));
            var subscriptions = DeserializeCreatorTrackingSubscriptions(reader.GetString(6));
            if (subscriptions.Count == 0 && HasLegacyCreatorTrackingSubscription(
                    reader.GetDouble(8),
                    reader.GetString(10),
                    reader.GetString(11)))
            {
                var platform = activityLinks.FirstOrDefault(link =>
                    link.Status.Contains("購読", StringComparison.OrdinalIgnoreCase))?.Label ?? string.Empty;
                subscriptions =
                [
                    new CreatorTrackingSubscriptionDto(
                        Guid.NewGuid().ToString("N"),
                        platform,
                        string.Empty,
                        reader.GetString(9),
                        reader.GetDouble(8),
                        "monthly",
                        reader.GetString(10),
                        reader.GetString(11),
                        !string.IsNullOrWhiteSpace(reader.GetString(11)),
                        false)
                ];
            }

            var spend = CalculateCreatorTrackingSpend(
                ListCreatorTrackingCharges(subscriptions, DeserializeCreatorTrackingPurchases(reader.GetString(7))),
                DefaultCreatorTrackingDisplayCurrency,
                exchangeRates);
            var sinceLastCheckDays = TryParseCreatorTrackingDate(lastActivityOn, out var lastActivityDate)
                ? Math.Max(0, today.DayNumber - lastActivityDate.DayNumber)
                : -1;
            var followUpDays = activityLinks
                .Where(link => link.FollowUpEnabled)
                .Select(link => Math.Clamp(link.FollowUpDays, 1, 99))
                .DefaultIfEmpty(0)
                .Min();
            var followWarnFlg = followUpDays > 0 && sinceLastCheckDays >= followUpDays;
            var followAlertDays = followUpDays > 0
                ? (int)Math.Ceiling(followUpDays * 1.5d)
                : 0;
            var followAlertFlg = followAlertDays > 0 && sinceLastCheckDays >= followAlertDays;
            var sites = activityLinks
                .Select(link => link.Label.Trim())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            facts[creator] = new CreatorTrackingSummaryFacts(
                lastActivityOn.Trim(),
                sinceLastCheckDays,
                followWarnFlg,
                followAlertFlg,
                CalculateCreatorTrackingDays(lastCheckedOn, lastActivityOn),
                spend.Total,
                sites,
                evaluationMetrics,
                CalculateCreatorTrackingRating(evaluationMetrics));
        }

        return facts;
    }

    public GalleryCreatorSummarySnapshotDto ListGalleryCreatorSummaries(bool forceRefresh = false)
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryCreatorSummarySnapshotDto([]);
        }

        try
        {
            var sourceSignature = CreateGalleryCreatorSummarySourceSignature();
            if (!forceRefresh && TryReadGalleryCreatorSummaryCache(sourceSignature, out var cachedSnapshot))
            {
                return cachedSnapshot;
            }

            var compositionLabelLimit = GetCreatorTrackingCompositionLabelLimit();
            var scopes = ListGallerySections()
                .Select(section =>
                {
                    var category = section.Id;
                    var targetPaths = ListGalleryScanTargets(category)
                        .Select(target => NormalizeGalleryTargetPath(target.Path))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var supportedExtensions = GetGalleryScanSettings(category).SupportedExtensions
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(NormalizeExtension)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return (Category: category, TargetPaths: targetPaths, SupportedExtensions: supportedExtensions);
                })
                .ToArray();

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ExternalGalleryDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ConnectionString);
            connection.Open();

            var creatorTrackingFacts = ListCreatorTrackingSummaryFacts(connection);
            var summaries = new Dictionary<string, GalleryCreatorSummaryAccumulator>(StringComparer.OrdinalIgnoreCase);
            var tagsByGid = ListGalleryCreatorTagsByGid(connection);
            var titlesByGid = ListGalleryAssignedFilterValuesByGid(connection, "title");
            var emptyRatings = Array.Empty<int>();
            var emptyStrings = Array.Empty<string>();

            foreach (var scope in scopes)
            {
                var where = BuildGalleryWorkWhere(
                    emptyRatings,
                    emptyStrings,
                    emptyStrings,
                    emptyStrings,
                    emptyStrings,
                    scope.TargetPaths,
                    scope.SupportedExtensions);
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT
                        i.gid,
                        COALESCE(i.current_path, ''),
                        COALESCE(TRIM(i.creator), ''),
                        COALESCE(TRIM(i.title), ''),
                        COALESCE(i.rating, 0),
                        COALESCE(i.image_count, 0),
                        COALESCE(i.last_access_time, ''),
                        COALESCE(i.last_write_time, '')
                    FROM items AS i
                    WHERE {where};
                    """;
                AddGalleryWorkParameters(
                    command,
                    scope.Category,
                    emptyRatings,
                    emptyStrings,
                    emptyStrings,
                    emptyStrings,
                    emptyStrings,
                    scope.TargetPaths,
                    scope.SupportedExtensions);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var gid = reader.GetString(0);
                    var path = reader.GetString(1);
                    var creator = reader.GetString(2);
                    var title = reader.GetString(3);
                    var rating = reader.GetInt32(4);
                    var imageCount = reader.GetInt32(5);
                    var lastAccessTime = reader.GetString(6);
                    var lastWriteTime = reader.GetString(7);

                    if (string.IsNullOrWhiteSpace(creator))
                    {
                        continue;
                    }

                    var creatorFolder = FindGalleryCreatorFolder(path, creator);
                    if (string.IsNullOrWhiteSpace(creatorFolder))
                    {
                        continue;
                    }

                    var key = $"{scope.Category}\u001f{creator}\u001f{creatorFolder}";
                    if (!summaries.TryGetValue(key, out var summary))
                    {
                        summary = new GalleryCreatorSummaryAccumulator(scope.Category, creator, creatorFolder);
                        summaries[key] = summary;
                    }

                    summary.TotalRating += rating;
                    summary.FileCount += 1;
                    if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        summary.ZipFileCount += 1;
                        summary.TotalImageCount += imageCount;
                    }
                    if (rating > 0)
                    {
                        summary.RatedFileCount += 1;
                    }
                    summary.MaxRating = Math.Max(summary.MaxRating, rating);
                    summary.LastAccessTime = SelectLatestGalleryTimestamp(summary.LastAccessTime, lastAccessTime);
                    summary.LastUpdatedTime = SelectLatestGalleryTimestamp(summary.LastUpdatedTime, lastWriteTime);
                    if (titlesByGid.TryGetValue(gid, out var assignedTitles) && assignedTitles.Count > 0)
                    {
                        foreach (var assignedTitle in assignedTitles)
                        {
                            summary.TitleCounts[assignedTitle] = summary.TitleCounts.GetValueOrDefault(assignedTitle) + 1;
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(title))
                    {
                        summary.TitleCounts[title] = summary.TitleCounts.GetValueOrDefault(title) + 1;
                    }
                    if (tagsByGid.TryGetValue(gid, out var itemTags))
                    {
                        foreach (var tag in itemTags)
                        {
                            summary.TagCounts[tag] = summary.TagCounts.GetValueOrDefault(tag) + 1;
                        }
                    }
                }
            }

            var categoryOrder = scopes
                .Select((scope, index) => (scope.Category, index))
                .ToDictionary(entry => entry.Category, entry => entry.index, StringComparer.OrdinalIgnoreCase);
            var items = summaries.Values
                .Select(summary =>
                {
                    var coreTitles = ListGalleryCreatorCoreValues(summary.TitleCounts, summary.FileCount);
                    var coreTags = ListGalleryCreatorCoreValues(summary.TagCounts, summary.FileCount);
                    var titleComposition = ListGalleryCreatorComposition(summary.TitleCounts, coreTitles, compositionLabelLimit);
                    var tagComposition = ListGalleryCreatorComposition(summary.TagCounts, coreTags, compositionLabelLimit);
                    creatorTrackingFacts.TryGetValue(summary.Creator, out var trackingFacts);
                    var folderLastWriteTime = GetGalleryCreatorFolderLastWriteTime(summary.CreatorFolder);
                    var lastUpdatedTime = !string.IsNullOrWhiteSpace(folderLastWriteTime)
                        ? folderLastWriteTime
                        : string.IsNullOrWhiteSpace(summary.LastUpdatedTime)
                            ? summary.LastAccessTime
                            : summary.LastUpdatedTime;
                    var idSource = $"creator-summary|{summary.Category}|{summary.Creator}|{summary.CreatorFolder}";
                    var searchSource = $"{summary.Creator} {string.Join(' ', coreTitles)} {string.Join(' ', coreTags)} {string.Join(' ', trackingFacts?.Sites ?? [])}";
                    return new GalleryCreatorSummaryDto(
                        "creator-summary-" + ComputeHash(idSource, SHA1.HashData),
                        summary.Category,
                        summary.Creator,
                        summary.CreatorFolder,
                        coreTitles,
                        coreTags,
                        titleComposition,
                        tagComposition,
                        summary.TotalRating,
                        summary.FileCount,
                        summary.ZipFileCount,
                        summary.TotalImageCount,
                        summary.ZipFileCount > 0 ? summary.TotalImageCount / summary.ZipFileCount : 0,
                        summary.RatedFileCount,
                        summary.MaxRating,
                        summary.LastAccessTime,
                        lastUpdatedTime,
                        trackingFacts?.LastActivityOn ?? string.Empty,
                        trackingFacts is not null,
                        trackingFacts?.SinceLastCheckDays ?? -1,
                        trackingFacts?.FollowWarnFlg ?? false,
                        trackingFacts?.FollowAlertFlg ?? false,
                        trackingFacts?.TrackingDays ?? -1,
                        trackingFacts?.TotalSpend ?? 0,
                        DefaultCreatorTrackingDisplayCurrency,
                        trackingFacts?.Sites ?? [],
                        trackingFacts?.EvaluationMetrics ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                        trackingFacts?.PersonalRating ?? 0,
                        GetGalleryCreatorRatingBucket(summary.TotalRating),
                        $"{searchSource} {FileNameRomanizer.Romanize(searchSource)}".ToLowerInvariant());
                })
                .OrderBy(item => categoryOrder.GetValueOrDefault(item.Category, int.MaxValue))
                .ThenByDescending(item => item.TotalRating)
                .ThenBy(item => item.Creator, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            var snapshot = new GalleryCreatorSummarySnapshotDto(items);
            WriteGalleryCreatorSummaryCache(sourceSignature, snapshot);
            return snapshot;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Creatorsを読み込めませんでした。", ex);
        }
    }

    private string CreateGalleryCreatorSummarySourceSignature() =>
        CreateExternalDataSignature(CreatorSummaryCacheVersion);

    private string CreateExternalDataSignature(string cacheVersion)
    {
        static string GetStamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
            }
            catch
            {
                return "unknown";
            }
        }

        var fullPath = Path.GetFullPath(ExternalGalleryDatabasePath);
        return string.Join('|',
            cacheVersion,
            DateOnly.FromDateTime(DateTime.Today).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            fullPath.ToUpperInvariant(),
            GetStamp(fullPath),
            GetStamp(fullPath + "-wal"));
    }

    private static string CreateDerivedDataCacheKey(string prefix, object value)
    {
        var payload = JsonSerializer.Serialize(value, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return $"{prefix}:{hash}";
    }

    private static string CombineSourceSignatures(params string[] values)
    {
        var payload = string.Join('\0', values);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private bool TryReadGalleryCreatorSummaryCache(
        string sourceSignature,
        out GalleryCreatorSummarySnapshotDto snapshot)
    {
        lock (_creatorSummaryCacheLock)
        {
            if (string.Equals(_creatorSummaryMemorySignature, sourceSignature, StringComparison.Ordinal) &&
                _creatorSummaryMemorySnapshot is not null)
            {
                snapshot = _creatorSummaryMemorySnapshot;
                return true;
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM creator_summary_cache WHERE id = 1 AND source_signature = $signature;";
            command.Parameters.AddWithValue("$signature", sourceSignature);
            var payload = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(payload))
            {
                snapshot = null!;
                return false;
            }

            try
            {
                snapshot = JsonSerializer.Deserialize<GalleryCreatorSummarySnapshotDto>(payload, JsonOptions)
                    ?? throw new JsonException("Creators cache is empty.");
                _creatorSummaryMemorySignature = sourceSignature;
                _creatorSummaryMemorySnapshot = snapshot;
                return true;
            }
            catch (JsonException)
            {
                snapshot = null!;
                return false;
            }
        }
    }

    private void WriteGalleryCreatorSummaryCache(
        string sourceSignature,
        GalleryCreatorSummarySnapshotDto snapshot)
    {
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        lock (_creatorSummaryCacheLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO creator_summary_cache (id, source_signature, payload_json)
                VALUES (1, $signature, $payload)
                ON CONFLICT(id) DO UPDATE SET
                    source_signature = excluded.source_signature,
                    payload_json = excluded.payload_json,
                    updated_at = CURRENT_TIMESTAMP;
                """;
            command.Parameters.AddWithValue("$signature", sourceSignature);
            command.Parameters.AddWithValue("$payload", payload);
            command.ExecuteNonQuery();
            _creatorSummaryMemorySignature = sourceSignature;
            _creatorSummaryMemorySnapshot = snapshot;
        }
    }

    private bool TryReadDerivedDataCache<T>(
        string cacheKey,
        string sourceSignature,
        out T value)
    {
        lock (_derivedDataCacheLock)
        {
            if (_derivedDataMemoryCache.TryGetValue(cacheKey, out var memoryEntry) &&
                string.Equals(memoryEntry.SourceSignature, sourceSignature, StringComparison.Ordinal) &&
                memoryEntry.Value is T typedValue)
            {
                value = typedValue;
                return true;
            }

            try
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT payload_json FROM derived_data_cache WHERE cache_key = $key AND source_signature = $signature;";
                command.Parameters.AddWithValue("$key", cacheKey);
                command.Parameters.AddWithValue("$signature", sourceSignature);
                var payload = command.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(payload))
                {
                    value = default!;
                    return false;
                }

                value = JsonSerializer.Deserialize<T>(payload, JsonOptions)
                    ?? throw new JsonException("Derived data cache is empty.");
                _derivedDataMemoryCache[cacheKey] = new DerivedDataMemoryCacheEntry(sourceSignature, value);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or SqliteException or IOException)
            {
                value = default!;
                return false;
            }
        }
    }

    private void WriteDerivedDataCache<T>(
        string cacheKey,
        string sourceSignature,
        T value)
    {
        lock (_derivedDataCacheLock)
        {
            _derivedDataMemoryCache[cacheKey] = new DerivedDataMemoryCacheEntry(sourceSignature, value!);
            try
            {
                var payload = JsonSerializer.Serialize(value, JsonOptions);
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO derived_data_cache (cache_key, source_signature, payload_json)
                    VALUES ($key, $signature, $payload)
                    ON CONFLICT(cache_key) DO UPDATE SET
                        source_signature = excluded.source_signature,
                        payload_json = excluded.payload_json,
                        updated_at = CURRENT_TIMESTAMP;
                    """;
                command.Parameters.AddWithValue("$key", cacheKey);
                command.Parameters.AddWithValue("$signature", sourceSignature);
                command.Parameters.AddWithValue("$payload", payload);
                command.ExecuteNonQuery();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or SqliteException or IOException)
            {
                // Derived caches are optional; a cache write failure must not block the source view.
            }
        }
    }

    public UserMetricsDashboardDto GetUserMetricsDashboard(string category, bool forceRefresh = false)
    {
        var sections = ListGallerySections();
        var normalizedCategory = sections.FirstOrDefault(section =>
            section.Id.Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase))?.Id
            ?? sections.FirstOrDefault()?.Id
            ?? DefaultGallerySections[0].Id;
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return CreateEmptyUserMetricsDashboard(normalizedCategory);
        }

        var cacheKey = $"user-metrics:{normalizedCategory}";
        var targetPaths = ListGalleryScanTargets(normalizedCategory)
            .Select(target => NormalizeGalleryTargetPath(target.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var supportedExtensions = GetGalleryScanSettings(normalizedCategory).SupportedExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string CreateSourceSignature() => CombineSourceSignatures(
            CreateExternalDataSignature(UserMetricsCacheVersion),
            CreateDerivedDataCacheKey("targets", new
            {
                Paths = targetPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                Extensions = supportedExtensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
            }));
        var sourceSignature = CreateSourceSignature();
        if (!forceRefresh && TryReadDerivedDataCache(cacheKey, sourceSignature, out UserMetricsDashboardDto cachedDashboard))
        {
            return cachedDashboard;
        }

        EnsureExternalGalleryGidSchema();
        try
        {
            var emptyRatings = Array.Empty<int>();
            var emptyStrings = Array.Empty<string>();
            var where = BuildGalleryWorkWhere(
                emptyRatings,
                emptyStrings,
                emptyStrings,
                emptyStrings,
                emptyStrings,
                targetPaths,
                supportedExtensions);

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ExternalGalleryDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ConnectionString);
            connection.Open();

            const string targetTag = "輪姦";
            var tagsByGid = ListGalleryCreatorTagsByGid(connection);
            var titlesByGid = ListGalleryAssignedFilterValuesByGid(connection, "title");
            var charactersByGid = ListGalleryAssignedFilterValuesByGid(connection, "character");
            var trackingFacts = ListCreatorTrackingSummaryFacts(connection);
            var creators = new Dictionary<string, UserMetricsEntityAccumulator>(StringComparer.OrdinalIgnoreCase);
            var titles = new Dictionary<string, UserMetricsEntityAccumulator>(StringComparer.OrdinalIgnoreCase);
            var characters = new Dictionary<string, UserMetricsEntityAccumulator>(StringComparer.OrdinalIgnoreCase);
            var tags = new Dictionary<string, UserMetricsEntityAccumulator>(StringComparer.OrdinalIgnoreCase);
            var trends = new Dictionary<string, UserMetricsTrendAccumulator>(StringComparer.Ordinal);
            var currentMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
            var recentCutoff = DateTime.Today.AddDays(-89);
            var totalFiles = 0;
            var totalImages = 0;
            var totalRating = 0;
            var ratedFiles = 0;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT
                        i.gid,
                        COALESCE(TRIM(i.creator), ''),
                        COALESCE(TRIM(i.title), ''),
                        COALESCE(TRIM(i.character), ''),
                        COALESCE(i.rating, 0),
                        COALESCE(i.image_count, 0),
                        COALESCE(i.last_write_time, '')
                    FROM items AS i
                    WHERE {where};
                    """;
                AddGalleryWorkParameters(
                    command,
                    normalizedCategory,
                    emptyRatings,
                    emptyStrings,
                    emptyStrings,
                    emptyStrings,
                    emptyStrings,
                    targetPaths,
                    supportedExtensions);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var gid = reader.GetString(0);
                    var creator = reader.GetString(1);
                    var title = reader.GetString(2);
                    var character = reader.GetString(3);
                    var rating = Math.Max(0, reader.GetInt32(4));
                    var imageCount = Math.Max(0, reader.GetInt32(5));
                    var lastWriteTime = reader.GetString(6);
                    var parsedLastWrite = DateTime.TryParse(lastWriteTime, out var parsedWriteTime)
                        ? parsedWriteTime
                        : DateTime.Today;
                    var isRecent = parsedLastWrite >= recentCutoff;
                    var itemMonth = new DateOnly(parsedLastWrite.Year, parsedLastWrite.Month, 1);
                    if (itemMonth > currentMonth)
                    {
                        itemMonth = currentMonth;
                    }
                    var monthKey = itemMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                    if (!trends.TryGetValue(monthKey, out var trend))
                    {
                        trend = new UserMetricsTrendAccumulator();
                        trends[monthKey] = trend;
                    }
                    trend.AddedFiles += 1;
                    trend.AddedImages += imageCount;

                    tagsByGid.TryGetValue(gid, out var itemTags);
                    var hasTargetTag = itemTags?.Any(tag => tag.Equals(targetTag, StringComparison.OrdinalIgnoreCase)) == true;
                    UpdateUserMetricsEntity(creators, creator, rating, imageCount, isRecent, false);
                    var itemTitles = titlesByGid.TryGetValue(gid, out var assignedTitles) && assignedTitles.Count > 0
                        ? assignedTitles
                        : SplitUserMetricsValues(title).ToArray();
                    foreach (var value in itemTitles)
                    {
                        UpdateUserMetricsEntity(titles, value, rating, imageCount, isRecent, hasTargetTag);
                    }
                    var itemCharacters = charactersByGid.TryGetValue(gid, out var assignedCharacters) && assignedCharacters.Count > 0
                        ? assignedCharacters
                        : SplitUserMetricsValues(character).ToArray();
                    foreach (var value in itemCharacters)
                    {
                        UpdateUserMetricsEntity(characters, value, rating, imageCount, isRecent, false);
                    }
                    foreach (var tag in itemTags ?? [])
                    {
                        UpdateUserMetricsEntity(tags, tag, rating, imageCount, isRecent, false);
                    }

                    totalFiles += 1;
                    totalImages += imageCount;
                    totalRating += rating;
                    if (rating > 0)
                    {
                        ratedFiles += 1;
                    }
                }
            }

            AddUserMetricsSpendTrends(connection, creators.Keys, trends);
            var trendPoints = BuildUserMetricsTrendPoints(trends, currentMonth);
            var fileRankings = CreateUserMetricsRankingSet(creators, titles, characters, tags, trackingFacts, "files");
            var imageRankings = CreateUserMetricsRankingSet(creators, titles, characters, tags, trackingFacts, "images");
            var ratingRankings = CreateUserMetricsRankingSet(creators, titles, characters, tags, trackingFacts, "rating");
            var metricRankings = CreateUserMetricsMetricRankings(creators.Keys, trackingFacts);
            var siteRankings = creators.Keys
                .Where(trackingFacts.ContainsKey)
                .SelectMany(creator => trackingFacts[creator].Sites)
                .Where(site => !string.IsNullOrWhiteSpace(site))
                .GroupBy(site => site.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new UserMetricsSiteRankItemDto(group.Key, group.Count()))
                .OrderByDescending(item => item.CreatorCount)
                .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(12)
                .ToArray();
            var spendRankings = CreateUserMetricsRankItems(creators, trackingFacts, "spend", 10);
            var followDayRankings = CreateUserMetricsRankItems(creators, trackingFacts, "trackingDays", 10);
            var bubbles = titles.Values
                .Where(item => item.FileCount > 0)
                .OrderByDescending(item => item.FileCount)
                .ThenByDescending(item => item.TotalRating)
                .Take(24)
                .Select(item => new UserMetricsBubbleItemDto(
                    item.Label,
                    item.TotalRating,
                    item.TargetTagFileCount,
                    item.FileCount,
                    item.FileCount > 0 ? Math.Round((double)item.TotalRating / item.FileCount, 2) : 0))
                .ToArray();
            var insights = CreateUserMetricsInsights(titles, characters, tags, creators, trackingFacts);

            var dashboard = new UserMetricsDashboardDto(
                normalizedCategory,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                totalFiles,
                totalImages,
                totalRating,
                ratedFiles,
                creators.Count,
                titles.Count,
                characters.Count,
                tags.Count,
                fileRankings,
                imageRankings,
                ratingRankings,
                trendPoints,
                metricRankings,
                siteRankings,
                spendRankings,
                followDayRankings,
                bubbles,
                insights,
                DefaultCreatorTrackingDisplayCurrency,
                targetTag);
            WriteDerivedDataCache(cacheKey, CreateSourceSignature(), dashboard);
            return dashboard;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("User Metricsを集計できませんでした。", ex);
        }
    }

    private static UserMetricsDashboardDto CreateEmptyUserMetricsDashboard(string category) =>
        new(
            category,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            0, 0, 0, 0, 0, 0, 0, 0,
            new UserMetricsRankingSetDto([], [], [], []),
            new UserMetricsRankingSetDto([], [], [], []),
            new UserMetricsRankingSetDto([], [], [], []),
            [], [], [], [], [], [], [],
            DefaultCreatorTrackingDisplayCurrency,
            "輪姦");

    private static void UpdateUserMetricsEntity(
        IDictionary<string, UserMetricsEntityAccumulator> entities,
        string label,
        int rating,
        int imageCount,
        bool isRecent,
        bool hasTargetTag)
    {
        var normalizedLabel = label.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            return;
        }
        if (!entities.TryGetValue(normalizedLabel, out var entity))
        {
            entity = new UserMetricsEntityAccumulator(normalizedLabel);
            entities[normalizedLabel] = entity;
        }
        entity.FileCount += 1;
        entity.ImageCount += Math.Max(0, imageCount);
        entity.TotalRating += Math.Max(0, rating);
        if (isRecent)
        {
            entity.RecentFileCount += 1;
        }
        if (hasTargetTag)
        {
            entity.TargetTagFileCount += 1;
        }
    }

    private static IEnumerable<string> SplitUserMetricsValues(string value) =>
        value.Split(['|', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static UserMetricsRankingSetDto CreateUserMetricsRankingSet(
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> creators,
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> titles,
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> characters,
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> tags,
        IReadOnlyDictionary<string, CreatorTrackingSummaryFacts> trackingFacts,
        string metric) =>
        new(
            CreateUserMetricsRankItems(creators, trackingFacts, metric, 12),
            CreateUserMetricsRankItems(titles, null, metric, 12),
            CreateUserMetricsRankItems(characters, null, metric, 12),
            CreateUserMetricsRankItems(tags, null, metric, 12));

    private static IReadOnlyList<UserMetricsRankItemDto> CreateUserMetricsRankItems(
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> entities,
        IReadOnlyDictionary<string, CreatorTrackingSummaryFacts>? trackingFacts,
        string metric,
        int take)
    {
        double MetricValue(UserMetricsEntityAccumulator item)
        {
            CreatorTrackingSummaryFacts? facts = null;
            trackingFacts?.TryGetValue(item.Label, out facts);
            return metric switch
            {
                "images" => item.ImageCount,
                "rating" => item.TotalRating,
                "spend" => facts?.TotalSpend ?? 0,
                "trackingDays" => facts?.TrackingDays ?? -1,
                _ => item.FileCount
            };
        }

        return entities.Values
            .Where(item => MetricValue(item) > 0)
            .OrderByDescending(MetricValue)
            .ThenByDescending(item => item.FileCount)
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .Take(take)
            .Select(item =>
            {
                CreatorTrackingSummaryFacts? facts = null;
                trackingFacts?.TryGetValue(item.Label, out facts);
                return new UserMetricsRankItemDto(
                    item.Label,
                    item.FileCount,
                    item.ImageCount,
                    item.TotalRating,
                    item.FileCount > 0 ? Math.Round((double)item.TotalRating / item.FileCount, 2) : 0,
                    item.RecentFileCount,
                    facts?.TotalSpend ?? 0,
                    facts?.TrackingDays ?? -1);
            })
            .ToArray();
    }

    private static IReadOnlyList<UserMetricsMetricRankingDto> CreateUserMetricsMetricRankings(
        IEnumerable<string> creators,
        IReadOnlyDictionary<string, CreatorTrackingSummaryFacts> trackingFacts)
    {
        var creatorList = creators.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rankings = new List<UserMetricsMetricRankingDto>
        {
            new("overall", creatorList
                .Where(trackingFacts.ContainsKey)
                .Select(creator => new UserMetricsMetricRankItemDto(creator, trackingFacts[creator].PersonalRating))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Creator, StringComparer.CurrentCultureIgnoreCase)
                .Take(12)
                .ToArray())
        };
        rankings.AddRange(CreatorTrackingMetricKeys.Select(metricKey =>
            new UserMetricsMetricRankingDto(
                metricKey,
                creatorList
                    .Where(trackingFacts.ContainsKey)
                    .Select(creator => new UserMetricsMetricRankItemDto(
                        creator,
                        trackingFacts[creator].EvaluationMetrics.GetValueOrDefault(metricKey)))
                    .Where(item => item.Score > 0)
                    .OrderByDescending(item => item.Score)
                    .ThenBy(item => item.Creator, StringComparer.CurrentCultureIgnoreCase)
                    .Take(12)
                    .ToArray())));
        return rankings;
    }

    private static IReadOnlyList<UserMetricsTrendPointDto> BuildUserMetricsTrendPoints(
        IReadOnlyDictionary<string, UserMetricsTrendAccumulator> trends,
        DateOnly currentMonth)
    {
        var parsedMonths = trends.Keys
            .Select(key => DateOnly.TryParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month)
                ? month
                : currentMonth)
            .Append(currentMonth)
            .Order()
            .ToArray();
        var startMonth = parsedMonths.FirstOrDefault(currentMonth);
        var points = new List<UserMetricsTrendPointDto>();
        var fileCount = 0;
        var imageCount = 0;
        var cumulativeSpend = 0d;
        for (var month = startMonth; month <= currentMonth; month = month.AddMonths(1))
        {
            var key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            trends.TryGetValue(key, out var trend);
            fileCount += trend?.AddedFiles ?? 0;
            imageCount += trend?.AddedImages ?? 0;
            cumulativeSpend += trend?.Spend ?? 0;
            points.Add(new UserMetricsTrendPointDto(
                key,
                fileCount,
                imageCount,
                Math.Round(trend?.Spend ?? 0, 0),
                Math.Round(cumulativeSpend, 0)));
        }
        return points.TakeLast(24).ToArray();
    }

    private static void AddUserMetricsSpendTrends(
        SqliteConnection connection,
        IEnumerable<string> categoryCreators,
        IDictionary<string, UserMetricsTrendAccumulator> trends)
    {
        var creators = categoryCreators.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (creators.Count == 0)
        {
            return;
        }
        var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using (var rateCommand = connection.CreateCommand())
        {
            rateCommand.CommandText = "SELECT rate_date, base_currency, quote_currency, rate FROM creator_tracking_exchange_rates;";
            using var rateReader = rateCommand.ExecuteReader();
            while (rateReader.Read())
            {
                if (TryParseCreatorTrackingDate(rateReader.GetString(0), out var rateDate))
                {
                    rates[CreateCreatorTrackingExchangeRateKey(rateDate, rateReader.GetString(1), rateReader.GetString(2))] = rateReader.GetDouble(3);
                }
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT creator, subscription_history_json, purchase_history_json,
                   activity_links_json, monthly_support_amount, currency,
                   support_started_on, support_ended_on
            FROM creator_tracking;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var creator = reader.GetString(0).Trim();
            if (!creators.Contains(creator))
            {
                continue;
            }
            var subscriptions = DeserializeCreatorTrackingSubscriptions(reader.GetString(1));
            if (subscriptions.Count == 0 && HasLegacyCreatorTrackingSubscription(
                    reader.GetDouble(4), reader.GetString(6), reader.GetString(7)))
            {
                var platform = DeserializeCreatorTrackingActivityLinks(reader.GetString(3))
                    .FirstOrDefault(link => link.Status.Contains("購読", StringComparison.OrdinalIgnoreCase))?.Label ?? string.Empty;
                subscriptions =
                [
                    new CreatorTrackingSubscriptionDto(
                        Guid.NewGuid().ToString("N"), platform, string.Empty,
                        reader.GetString(5), reader.GetDouble(4), "monthly", reader.GetString(6), reader.GetString(7),
                        !string.IsNullOrWhiteSpace(reader.GetString(7)), false)
                ];
            }
            foreach (var charge in ListCreatorTrackingCharges(subscriptions, DeserializeCreatorTrackingPurchases(reader.GetString(2))))
            {
                var rate = 1d;
                if (!charge.Currency.Equals(DefaultCreatorTrackingDisplayCurrency, StringComparison.OrdinalIgnoreCase) &&
                    !rates.TryGetValue(CreateCreatorTrackingExchangeRateKey(
                        charge.Date, charge.Currency, DefaultCreatorTrackingDisplayCurrency), out rate))
                {
                    continue;
                }
                var key = charge.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (!trends.TryGetValue(key, out var trend))
                {
                    trend = new UserMetricsTrendAccumulator();
                    trends[key] = trend;
                }
                trend.Spend += charge.Amount * rate;
            }
        }
    }

    private static IReadOnlyList<UserMetricsInsightDto> CreateUserMetricsInsights(
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> titles,
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> characters,
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> tags,
        IReadOnlyDictionary<string, UserMetricsEntityAccumulator> creators,
        IReadOnlyDictionary<string, CreatorTrackingSummaryFacts> trackingFacts)
    {
        var insights = new List<UserMetricsInsightDto>();
        var strongestTitle = titles.Values
            .Where(item => item.FileCount >= 3)
            .OrderByDescending(item => (double)item.TotalRating / item.FileCount)
            .ThenByDescending(item => item.TotalRating)
            .FirstOrDefault();
        if (strongestTitle is not null)
        {
            insights.Add(new UserMetricsInsightDto(
                "評価効率が高いTitle",
                strongestTitle.Label,
                $"1作品あたり平均 {(double)strongestTitle.TotalRating / strongestTitle.FileCount:0.00}★ / {strongestTitle.FileCount:N0}作品",
                "lime"));
        }
        var growingCharacter = characters.Values
            .Where(item => item.RecentFileCount > 0)
            .OrderByDescending(item => item.RecentFileCount)
            .ThenByDescending(item => item.TotalRating)
            .FirstOrDefault();
        if (growingCharacter is not null)
        {
            insights.Add(new UserMetricsInsightDto(
                "直近90日の伸長Character",
                growingCharacter.Label,
                $"直近90日で {growingCharacter.RecentFileCount:N0}作品増加",
                "cyan"));
        }
        var strongestTag = tags.Values
            .Where(item => item.FileCount >= 5)
            .OrderByDescending(item => (double)item.TotalRating / item.FileCount)
            .ThenByDescending(item => item.TotalRating)
            .FirstOrDefault();
        if (strongestTag is not null)
        {
            insights.Add(new UserMetricsInsightDto(
                "評価を受けやすいTag",
                strongestTag.Label,
                $"平均 {(double)strongestTag.TotalRating / strongestTag.FileCount:0.00}★ / {strongestTag.FileCount:N0}作品",
                "amber"));
        }
        var correlationPairs = creators.Values
            .Where(item => trackingFacts.TryGetValue(item.Label, out var facts) && facts.TotalSpend > 0)
            .Select(item => (Files: (double)item.FileCount, Spend: trackingFacts[item.Label].TotalSpend))
            .ToArray();
        if (correlationPairs.Length >= 3)
        {
            var correlation = CalculateUserMetricsCorrelation(
                correlationPairs.Select(pair => pair.Files).ToArray(),
                correlationPairs.Select(pair => pair.Spend).ToArray());
            insights.Add(new UserMetricsInsightDto(
                "課金額と保有作品数",
                $"相関 {correlation:+0.00;-0.00;0.00}",
                correlation >= 0.45
                    ? "課金額が高い作者ほど保有作品数も多い傾向があります"
                    : correlation <= -0.45
                        ? "課金額が高い作者ほど保有作品数は少ない傾向があります"
                        : "現時点では明確な相関は見られません",
                "violet"));
        }
        return insights;
    }

    private static double CalculateUserMetricsCorrelation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2)
        {
            return 0;
        }
        var averageX = x.Average();
        var averageY = y.Average();
        var numerator = 0d;
        var denominatorX = 0d;
        var denominatorY = 0d;
        for (var index = 0; index < x.Count; index++)
        {
            var deltaX = x[index] - averageX;
            var deltaY = y[index] - averageY;
            numerator += deltaX * deltaY;
            denominatorX += deltaX * deltaX;
            denominatorY += deltaY * deltaY;
        }
        var denominator = Math.Sqrt(denominatorX * denominatorY);
        return denominator > 0 ? numerator / denominator : 0;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ListGalleryCreatorTagsByGid(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_tag.gid, TRIM(tag.tag)
            FROM item_tags AS item_tag
            JOIN tags AS tag ON tag.tag_id = item_tag.tag_id
            WHERE tag.use_flg = 1
              AND TRIM(tag.tag) <> ''
            ORDER BY tag.position, tag.tag COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var tagsByGid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var gid = reader.GetString(0);
            if (!tagsByGid.TryGetValue(gid, out var tags))
            {
                tags = [];
                tagsByGid[gid] = tags;
            }
            tags.Add(reader.GetString(1));
        }

        return tagsByGid.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ListGalleryAssignedFilterValuesByGid(
        SqliteConnection connection,
        string filterType)
    {
        var filterIdColumn = filterType switch
        {
            "title" => "title_filter_id",
            "character" => "character_filter_id",
            _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "対応していない属性種別です。")
        };
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT combination_map.gid, TRIM(filter.canonical_name)
            FROM gallery_item_filter_combinations AS combination_map
            JOIN gallery_filter_combinations AS combination
              ON combination.combination_id = combination_map.combination_id
             AND combination.use_flg <> 0
            JOIN gallery_filters AS filter
              ON filter.filter_id = combination.{filterIdColumn}
             AND filter.filter_type = $filterType
            WHERE TRIM(filter.canonical_name) <> ''
            GROUP BY combination_map.gid, filter.filter_id, filter.canonical_name
            ORDER BY combination_map.gid, filter.canonical_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$filterType", filterType);
        using var reader = command.ExecuteReader();
        var valuesByGid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var gid = reader.GetString(0);
            if (!valuesByGid.TryGetValue(gid, out var values))
            {
                values = [];
                valuesByGid[gid] = values;
            }
            values.Add(reader.GetString(1));
        }

        return valuesByGid.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string[] ListGalleryCreatorCoreValues(IReadOnlyDictionary<string, int> counts, int totalFileCount)
    {
        if (totalFileCount <= 0)
        {
            return [];
        }

        return counts
            .Where(entry => entry.Value * 100 >= totalFileCount * 30)
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry.Key)
            .ToArray();
    }

    private static IReadOnlyList<GalleryCreatorCompositionSliceDto> ListGalleryCreatorComposition(
        IReadOnlyDictionary<string, int> counts,
        IReadOnlyList<string> coreValues,
        int labelLimit)
    {
        var positiveCounts = counts
            .Where(entry => entry.Value > 0 && !string.IsNullOrWhiteSpace(entry.Key))
            .ToArray();
        if (positiveCounts.Length == 0)
        {
            return [];
        }

        var coreSet = coreValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = positiveCounts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(NormalizeCreatorTrackingCompositionLabelLimit(labelLimit))
            .Select(entry => new GalleryCreatorCompositionSliceDto(entry.Key, entry.Value, coreSet.Contains(entry.Key)))
            .ToList();

        var visibleLabels = visible
            .Select(slice => slice.Label)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var otherCount = positiveCounts
            .Where(entry => !visibleLabels.Contains(entry.Key))
            .Sum(entry => entry.Value);
        if (otherCount > 0)
        {
            visible.Add(new GalleryCreatorCompositionSliceDto("その他", otherCount, false));
        }
        return visible;
    }

    private static string FindGalleryCreatorFolder(string fullPath, string creator)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(creator))
        {
            return string.Empty;
        }

        var directoryPath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return string.Empty;
        }

        try
        {
            var exactFolderName = $"【{creator}】";
            for (var directory = new DirectoryInfo(directoryPath); directory is not null; directory = directory.Parent)
            {
                if (string.Equals(directory.Name, exactFolderName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directory.Name, creator, StringComparison.OrdinalIgnoreCase))
                {
                    return directory.FullName;
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        return string.Empty;
    }

    private static string SelectLatestGalleryTimestamp(string current, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return current;
        }
        if (string.IsNullOrWhiteSpace(current))
        {
            return candidate;
        }
        if (DateTime.TryParse(current, out var currentDate) && DateTime.TryParse(candidate, out var candidateDate))
        {
            return candidateDate > currentDate ? candidate : current;
        }
        return string.Compare(candidate, current, StringComparison.Ordinal) > 0 ? candidate : current;
    }

    private static string GetGalleryCreatorFolderLastWriteTime(string folderPath)
    {
        try
        {
            return Directory.Exists(folderPath)
                ? Directory.GetLastWriteTime(folderPath).ToString("yyyy-MM-dd HH:mm")
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetGalleryCreatorRatingBucket(int totalRating) => totalRating switch
    {
        >= 10 => "10plus",
        >= 7 => "7-9",
        >= 4 => "4-6",
        >= 3 => "3",
        >= 2 => "2",
        >= 1 => "1",
        _ => "0"
    };

    public GalleryFilterEditorSnapshotDto ListGalleryFilterDefinitions()
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryFilterEditorSnapshotDto([], [], []);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        var aliasesByFilter = new Dictionary<long, List<string>>();
        using (var aliases = connection.CreateCommand())
        {
            aliases.CommandText = "SELECT filter_id, alias FROM gallery_filter_aliases ORDER BY alias COLLATE NOCASE;";
            using var reader = aliases.ExecuteReader();
            while (reader.Read())
            {
                var filterId = reader.GetInt64(0);
                if (!aliasesByFilter.TryGetValue(filterId, out var values))
                {
                    values = [];
                    aliasesByFilter[filterId] = values;
                }
                values.Add(reader.GetString(1));
            }
        }

        var visibilityByFilter = new Dictionary<long, List<string>>();
        using (var visibility = connection.CreateCommand())
        {
            visibility.CommandText = "SELECT filter_id, gallery_category FROM gallery_filter_section_visibility WHERE is_visible <> 0 ORDER BY gallery_category;";
            using var reader = visibility.ExecuteReader();
            while (reader.Read())
            {
                var filterId = reader.GetInt64(0);
                if (!visibilityByFilter.TryGetValue(filterId, out var values))
                {
                    values = [];
                    visibilityByFilter[filterId] = values;
                }
                values.Add(reader.GetString(1));
            }
        }

        var categories = ListGalleryFilterCategories(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                f.filter_id,
                f.filter_type,
                f.canonical_name,
                COALESCE(category.name, '未分類'),
                f.parent_filter_id,
                COALESCE(parent.canonical_name, ''),
                COUNT(DISTINCT item_filter.gid)
            FROM gallery_filters AS f
            LEFT JOIN filter_categories AS category ON category.category_id = f.filter_category_id
            LEFT JOIN gallery_filters AS parent ON parent.filter_id = f.parent_filter_id
            LEFT JOIN gallery_item_filters AS item_filter ON item_filter.filter_id = f.filter_id
            WHERE EXISTS (
                SELECT 1
                FROM gallery_filter_combinations AS combination
                WHERE combination.use_flg <> 0
                  AND (
                    (f.filter_type = 'title' AND combination.title_filter_id = f.filter_id)
                    OR (f.filter_type = 'character' AND combination.character_filter_id = f.filter_id)
                  )
            )
            GROUP BY f.filter_id
            ORDER BY f.filter_type, COALESCE(category.name, '未分類') COLLATE NOCASE, f.canonical_name COLLATE NOCASE;
            """;
        using var definitionReader = command.ExecuteReader();
        var titles = new List<GalleryFilterDefinitionDto>();
        var characters = new List<GalleryFilterDefinitionDto>();
        while (definitionReader.Read())
        {
            var id = definitionReader.GetInt64(0);
            var definition = new GalleryFilterDefinitionDto(
                id,
                definitionReader.GetString(1),
                definitionReader.GetString(2),
                definitionReader.GetString(3),
                definitionReader.IsDBNull(4) ? null : definitionReader.GetInt64(4),
                definitionReader.GetString(5),
                aliasesByFilter.TryGetValue(id, out var aliases) ? aliases : [],
                visibilityByFilter.TryGetValue(id, out var visibility) ? visibility : [],
                definitionReader.GetInt32(6));
            if (string.Equals(definition.FilterType, "title", StringComparison.Ordinal))
            {
                titles.Add(definition);
            }
            else
            {
                characters.Add(definition);
            }
        }

        return new GalleryFilterEditorSnapshotDto(categories, titles, characters);
    }

    public GalleryTagManagementSnapshotDto ListGalleryTagDefinitions()
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryTagManagementSnapshotDto([]);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        var categoriesByTag = new Dictionary<long, List<string>>();
        using (var categories = connection.CreateCommand())
        {
            categories.CommandText = "SELECT tag_id, category FROM category_tags ORDER BY category;";
            using var categoryReader = categories.ExecuteReader();
            while (categoryReader.Read())
            {
                var tagId = categoryReader.GetInt64(0);
                if (!categoriesByTag.TryGetValue(tagId, out var values))
                {
                    values = [];
                    categoriesByTag[tagId] = values;
                }
                values.Add(categoryReader.GetString(1));
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tag.tag_id,
                   tag.tag,
                   tag.use_flg,
                   tag.position,
                   COUNT(DISTINCT item_tag.gid)
            FROM tags AS tag
            LEFT JOIN item_tags AS item_tag ON item_tag.tag_id = tag.tag_id
            WHERE tag.use_flg <> 0
            GROUP BY tag.tag_id, tag.tag, tag.use_flg, tag.position
            ORDER BY tag.position, tag.tag COLLATE NOCASE, tag.tag_id;
            """;
        using var reader = command.ExecuteReader();
        var tags = new List<GalleryTagDefinitionDto>();
        while (reader.Read())
        {
            var tagId = reader.GetInt64(0);
            tags.Add(new GalleryTagDefinitionDto(
                tagId,
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                categoriesByTag.TryGetValue(tagId, out var categories) ? categories : [],
                reader.GetInt32(4)));
        }

        return new GalleryTagManagementSnapshotDto(tags);
    }

    public long SaveGalleryTagDefinition(long? tagId, string tag, int useFlag)
    {
        tag = tag.Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag名を入力してください。", nameof(tag));
        }
        if (useFlag is not 0 and not 1)
        {
            throw new ArgumentOutOfRangeException(nameof(useFlag));
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        if (tagId is { } existingId)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE tags SET tag = $tag, use_flg = $useFlag WHERE tag_id = $id;";
            update.Parameters.AddWithValue("$tag", tag);
            update.Parameters.AddWithValue("$useFlag", useFlag);
            update.Parameters.AddWithValue("$id", existingId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("編集対象のTagが見つかりません。");
            }
            return existingId;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO tags (tag, use_flg, position) VALUES ($tag, 1, (SELECT COALESCE(MAX(position), -1) + 1 FROM tags)); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$tag", tag);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    public void DisableGalleryTag(long tagId)
    {
        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tags SET use_flg = 0 WHERE tag_id = $id;";
        command.Parameters.AddWithValue("$id", tagId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("無効化するTagが見つかりません。");
        }
    }

    public void SaveGalleryTagOrder(IReadOnlyList<long> tagIds)
    {
        var ids = tagIds.Distinct().Where(id => id > 0).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE tags SET position = $position WHERE tag_id = $id AND use_flg <> 0;";
        var position = update.CreateParameter();
        position.ParameterName = "$position";
        update.Parameters.Add(position);
        var id = update.CreateParameter();
        id.ParameterName = "$id";
        update.Parameters.Add(id);
        for (var index = 0; index < ids.Length; index++)
        {
            position.Value = index;
            id.Value = ids[index];
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SetGalleryTagCategoryMapping(IReadOnlyList<long> tagIds, string galleryCategory, bool isEnabled)
    {
        var ids = tagIds.Where(id => id > 0).Distinct().ToArray();
        galleryCategory = galleryCategory.Trim();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(galleryCategory))
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = isEnabled
            ? "INSERT OR IGNORE INTO category_tags (category, tag_id) VALUES ($category, $tagId);"
            : "DELETE FROM category_tags WHERE category = $category AND tag_id = $tagId;";
        command.Parameters.AddWithValue("$category", galleryCategory);
        var tagIdParameter = command.CreateParameter();
        tagIdParameter.ParameterName = "$tagId";
        command.Parameters.Add(tagIdParameter);
        foreach (var id in ids)
        {
            tagIdParameter.Value = id;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<GalleryTagExportRowDto> ListGalleryTagExportRows()
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return [];
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag_id, tag, use_flg FROM tags ORDER BY position, tag COLLATE NOCASE, tag_id;";
        using var reader = command.ExecuteReader();
        var rows = new List<GalleryTagExportRowDto>();
        while (reader.Read())
        {
            rows.Add(new GalleryTagExportRowDto(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return rows;
    }

    public int ImportGalleryTags(IReadOnlyList<GalleryTagImportRowDto> rows)
    {
        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var importedCount = 0;
        foreach (var row in rows)
        {
            var currentTag = row.Tag.Trim();
            var newTag = row.NewTag.Trim();
            var isNew = row.TagId is null && string.IsNullOrWhiteSpace(currentTag) && !string.IsNullOrWhiteSpace(newTag) && row.UseFlag is null;
            var isUpdate = row.TagId is not null && !string.IsNullOrWhiteSpace(currentTag) && !string.IsNullOrWhiteSpace(newTag);
            var isDisable = row.TagId is not null && !string.IsNullOrWhiteSpace(currentTag) && string.IsNullOrWhiteSpace(newTag) && row.UseFlag == 0;
            var isUnchanged = row.TagId is not null && !string.IsNullOrWhiteSpace(currentTag) && string.IsNullOrWhiteSpace(newTag) && row.UseFlag is 1 or null;

            if (isUnchanged)
            {
                continue;
            }
            if (isNew)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO tags (tag, use_flg, position) VALUES ($tag, 1, (SELECT COALESCE(MAX(position), -1) + 1 FROM tags));";
                insert.Parameters.AddWithValue("$tag", newTag);
                insert.ExecuteNonQuery();
                importedCount++;
                continue;
            }
            if (!isUpdate && !isDisable)
            {
                throw new InvalidDataException($"{row.RowNumber}行目はTag更新・新規追加・無効化のいずれの条件にも一致しません。");
            }

            var tagId = row.TagId!.Value;
            using (var current = connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT tag FROM tags WHERE tag_id = $id;";
                current.Parameters.AddWithValue("$id", tagId);
                var databaseTag = current.ExecuteScalar() as string;
                if (databaseTag is null)
                {
                    throw new InvalidDataException($"{row.RowNumber}行目のtag_id {tagId} は存在しません。");
                }
                if (!string.Equals(databaseTag, currentTag, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{row.RowNumber}行目のtag_idとtagが一致しません。");
                }
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = isUpdate
                ? "UPDATE tags SET tag = $tag, use_flg = $useFlag, position = CASE WHEN use_flg = 0 AND $useFlag = 1 THEN (SELECT COALESCE(MAX(position), -1) + 1 FROM tags) ELSE position END WHERE tag_id = $id;"
                : "UPDATE tags SET use_flg = 0 WHERE tag_id = $id;";
            update.Parameters.AddWithValue("$id", tagId);
            if (isUpdate)
            {
                if (row.UseFlag is not 0 and not 1)
                {
                    throw new InvalidDataException($"{row.RowNumber}行目はTag更新時にuse_flgの0または1が必要です。");
                }
                update.Parameters.AddWithValue("$tag", newTag);
                update.Parameters.AddWithValue("$useFlag", row.UseFlag.Value);
            }
            update.ExecuteNonQuery();
            importedCount++;
        }

        transaction.Commit();
        return importedCount;
    }

    public IReadOnlyList<GalleryFilterCombinationExportRowDto> ListGalleryFilterCombinationExportRows()
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return [];
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT combination.combination_id,
                   combination.filter_category_id,
                   COALESCE(category.name, ''),
                   combination.title_filter_id,
                   title_filter.canonical_name,
                   combination.character_filter_id,
                   COALESCE(character_filter.canonical_name, ''),
                   combination.use_flg
            FROM gallery_filter_combinations AS combination
            JOIN gallery_filters AS title_filter ON title_filter.filter_id = combination.title_filter_id
            LEFT JOIN gallery_filters AS character_filter ON character_filter.filter_id = combination.character_filter_id
            LEFT JOIN filter_categories AS category ON category.category_id = combination.filter_category_id
            ORDER BY category.position, category.name COLLATE NOCASE, title_filter.canonical_name COLLATE NOCASE,
                     character_filter.canonical_name COLLATE NOCASE, combination.combination_id;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<GalleryFilterCombinationExportRowDto>();
        while (reader.Read())
        {
            rows.Add(new GalleryFilterCombinationExportRowDto(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetString(6),
                reader.GetInt32(7)));
        }
        return rows;
    }

    public IReadOnlyList<GalleryFilterCategoryExportRowDto> ListGalleryFilterCategoryExportRows()
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return [];
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT category_id, name FROM filter_categories ORDER BY position, name COLLATE NOCASE, category_id;";
        using var reader = command.ExecuteReader();
        var rows = new List<GalleryFilterCategoryExportRowDto>();
        while (reader.Read())
        {
            rows.Add(new GalleryFilterCategoryExportRowDto(reader.GetInt64(0), reader.GetString(1)));
        }
        return rows;
    }

    public long SaveGalleryFilterCategory(long? categoryId, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Category名を入力してください。", nameof(name));
        }
        if (string.Equals(name, "未分類", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("「未分類」は予約済みのためCategory名には使用できません。", nameof(name));
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        if (categoryId is { } existingId)
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE filter_categories SET name = $name WHERE category_id = $id;";
            update.Parameters.AddWithValue("$name", name);
            update.Parameters.AddWithValue("$id", existingId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("編集対象のCategoryが見つかりません。");
            }
            return existingId;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO filter_categories (name, position) VALUES ($name, (SELECT COALESCE(MAX(position), -1) + 1 FROM filter_categories)); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    public void SaveGalleryFilterCategoryOrder(IReadOnlyList<long> categoryIds)
    {
        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE filter_categories SET position = $position WHERE category_id = $id;";
        var position = update.CreateParameter();
        position.ParameterName = "$position";
        update.Parameters.Add(position);
        var id = update.CreateParameter();
        id.ParameterName = "$id";
        update.Parameters.Add(id);
        var nextPosition = 0;
        foreach (var categoryId in categoryIds.Distinct().Where(categoryId => categoryId > 0))
        {
            position.Value = nextPosition++;
            id.Value = categoryId;
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteGalleryFilterCategory(long categoryId)
    {
        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using (var detach = connection.CreateCommand())
        {
            detach.Transaction = transaction;
            detach.CommandText = "UPDATE gallery_filters SET filter_category_id = NULL, category_id = NULL, updated_at = CURRENT_TIMESTAMP WHERE filter_category_id = $id OR category_id = $id;";
            detach.Parameters.AddWithValue("$id", categoryId);
            detach.ExecuteNonQuery();
        }
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM filter_categories WHERE category_id = $id;";
            delete.Parameters.AddWithValue("$id", categoryId);
            delete.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public int ImportGalleryFilterCategories(IReadOnlyList<GalleryFilterCategoryImportRowDto> rows)
    {
        var imports = rows
            .Where(row => row.FilterCategoryId is not null || row.Add || row.MergeFilterCategoryId is not null || !string.IsNullOrWhiteSpace(row.NewCategoryName))
            .ToArray();
        if (imports.Length == 0)
        {
            throw new ArgumentException("登録・更新できるCategoryの行がありません。");
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var importedCount = 0;
        foreach (var row in imports)
        {
            var applied = true;
            if (row.Add)
            {
                if (row.MergeFilterCategoryId is not null)
                {
                    throw new InvalidDataException($"{row.RowNumber}行目は Add と Merge を同時に指定できません。");
                }
                var categoryName = NormalizeFilterCategoryName(row.NewCategoryName);
                if (string.IsNullOrWhiteSpace(categoryName))
                {
                    throw new InvalidDataException($"{row.RowNumber}行目は Add の場合 New_Category が必要です。");
                }
                if (FilterCategoryExistsByName(connection, transaction, categoryName))
                {
                    applied = false;
                }
                else
                {
                    _ = GetOrCreateFilterCategoryId(connection, transaction, categoryName);
                }
            }
            else if (row.FilterCategoryId is { } categoryId)
            {
                EnsureGalleryFilterCategoryExists(connection, transaction, categoryId, row.RowNumber);
                if (row.MergeFilterCategoryId is { } mergeTargetId)
                {
                    MergeGalleryFilterCategory(connection, transaction, categoryId, mergeTargetId, row.RowNumber);
                }
                else if (!string.IsNullOrWhiteSpace(row.NewCategoryName))
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE filter_categories SET name = $name WHERE category_id = $id;";
                    update.Parameters.AddWithValue("$name", row.NewCategoryName.Trim());
                    update.Parameters.AddWithValue("$id", categoryId);
                    update.ExecuteNonQuery();
                }
            }
            else
            {
                throw new InvalidDataException($"{row.RowNumber}行目は filter_category_id を指定するか、Add を True にしてください。");
            }
            if (applied)
            {
                importedCount++;
            }
        }

        SynchronizeGalleryFilterCombinationRecords(connection, transaction);
        SynchronizeGalleryItemFilterCombinationRecords(connection, transaction);
        transaction.Commit();
        return importedCount;
    }

    private static void EnsureGalleryFilterCategoryExists(SqliteConnection connection, SqliteTransaction transaction, long categoryId, int rowNumber)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM filter_categories WHERE category_id = $id;";
        command.Parameters.AddWithValue("$id", categoryId);
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            throw new InvalidDataException($"{rowNumber}行目の filter_category_id {categoryId} は存在しません。");
        }
    }

    private static bool FilterCategoryExistsByName(SqliteConnection connection, SqliteTransaction transaction, string categoryName)
    {
        categoryName = NormalizeFilterCategoryName(categoryName);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM filter_categories WHERE name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", categoryName.Trim());
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void MergeGalleryFilterCategory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceCategoryId,
        long targetCategoryId,
        int rowNumber)
    {
        if (sourceCategoryId == targetCategoryId)
        {
            throw new InvalidDataException($"{rowNumber}行目は自分自身へは統合できません。");
        }

        EnsureGalleryFilterCategoryExists(connection, transaction, targetCategoryId, rowNumber);
        using (var moveFilters = connection.CreateCommand())
        {
            moveFilters.Transaction = transaction;
            moveFilters.CommandText = """
                UPDATE gallery_filters
                SET filter_category_id = $targetId,
                    category_id = $targetId,
                    updated_at = CURRENT_TIMESTAMP
                WHERE filter_category_id = $sourceId OR category_id = $sourceId;
                """;
            moveFilters.Parameters.AddWithValue("$targetId", targetCategoryId);
            moveFilters.Parameters.AddWithValue("$sourceId", sourceCategoryId);
            moveFilters.ExecuteNonQuery();
        }
        using (var moveCombinations = connection.CreateCommand())
        {
            moveCombinations.Transaction = transaction;
            moveCombinations.CommandText = "UPDATE gallery_filter_combinations SET filter_category_id = $targetId, updated_at = CURRENT_TIMESTAMP WHERE filter_category_id = $sourceId;";
            moveCombinations.Parameters.AddWithValue("$targetId", targetCategoryId);
            moveCombinations.Parameters.AddWithValue("$sourceId", sourceCategoryId);
            moveCombinations.ExecuteNonQuery();
        }
        using var deleteSource = connection.CreateCommand();
        deleteSource.Transaction = transaction;
        deleteSource.CommandText = "DELETE FROM filter_categories WHERE category_id = $sourceId;";
        deleteSource.Parameters.AddWithValue("$sourceId", sourceCategoryId);
        deleteSource.ExecuteNonQuery();
    }

    public int ImportGalleryFilterCombinations(IReadOnlyList<GalleryFilterCombinationImportRowDto> rows)
    {
        var imports = rows
            .Where(HasGalleryFilterCombinationImportContent)
            .ToArray();
        if (imports.Length == 0)
        {
            throw new ArgumentException("登録・更新できるフィルタ項目の行がありません。");
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var plan = BuildGalleryFilterCombinationImportPlan(connection, transaction, imports);

        foreach (var state in plan.Updates.Where(state => state.Row.MergeCombinationId is { }))
        {
            MergeGalleryFilterCombination(
                connection,
                transaction,
                state.Current.CombinationId,
                state.Row.MergeCombinationId!.Value,
                state.Row.RowNumber);
        }

        ApplyAutomaticTitleMerges(connection, transaction, plan);

        var importedCount = plan.Updates.Count;
        foreach (var row in plan.AddRows)
        {
            if (AddGalleryFilterCombination(connection, transaction, row))
            {
                importedCount++;
            }
        }

        SynchronizeGalleryFilterCombinationRecords(connection, transaction);
        SynchronizeGalleryItemFilterCombinationRecords(connection, transaction);
        ApplyImportedCombinationUseFlags(connection, transaction, plan);
        transaction.Commit();
        return importedCount;
    }

    public GalleryFilterCombinationImportPreviewDto PreviewGalleryFilterCombinationImport(IReadOnlyList<GalleryFilterCombinationImportRowDto> rows)
    {
        var imports = rows
            .Where(HasGalleryFilterCombinationImportContent)
            .ToArray();
        if (imports.Length == 0)
        {
            throw new ArgumentException("登録・更新できるフィルタ項目の行がありません。");
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var plan = BuildGalleryFilterCombinationImportPlan(connection, transaction, imports);

        var updateRows = plan.Updates
            .Where(state => state.Row.MergeCombinationId is null)
            .Select(state => CreateGalleryFilterCombinationPreviewRow(state, "更新", string.Empty))
            .ToArray();
        var explicitMergeRows = plan.Updates
            .Where(state => state.Row.MergeCombinationId is not null)
            .Select(state => CreateGalleryFilterCombinationPreviewRow(
                state,
                "明示Merge",
                $"Combination {state.Current.CombinationId} を {state.Row.MergeCombinationId} へ統合"))
            .ToArray();
        var automaticTitleMergeRows = plan.Updates
            .Where(state => state.Row.MergeCombinationId is null && state.Current.TitleFilterId != state.TargetTitleId)
            .GroupBy(state => state.Current.TitleFilterId)
            .Select(group => CreateGalleryFilterCombinationPreviewRow(
                group.First(),
                "自動Title統合",
                $"Title {group.Key} を {group.First().TargetTitleId} へ統合"))
            .ToArray();
        var automaticCharacterMoveRows = plan.Updates
            .Where(state => state.Row.MergeCombinationId is null && state.Current.CharacterFilterId is not null && state.Current.TitleFilterId != state.TargetTitleId)
            .GroupBy(state => state.Current.CharacterFilterId!.Value)
            .Select(group => CreateGalleryFilterCombinationPreviewRow(
                group.First(),
                "Character移動・統合",
                $"Character {group.Key} を Title {group.First().TargetTitleId} へ移動または統合"))
            .ToArray();
        var addRows = new List<GalleryFilterCombinationImportPreviewRowDto>();
        var duplicateAddRows = new List<GalleryFilterCombinationImportPreviewRowDto>();
        foreach (var row in plan.AddRows)
        {
            var categoryName = FirstNonEmpty(row.NewCategoryName, row.IsLegacyRow ? row.CategoryName : string.Empty);
            var titleName = FirstNonEmpty(row.NewTitleName, row.IsLegacyRow ? row.TitleName : string.Empty);
            var characterName = NormalizeImportCharacterName(FirstNonEmpty(row.NewCharacterName, row.IsLegacyRow ? row.CharacterName : string.Empty));
            var isDuplicate = FindGalleryFilterCombinationIdByNames(connection, transaction, categoryName, titleName, characterName) is not null;
            var previewRow = CreateGalleryFilterCombinationAddPreviewRow(row, categoryName, titleName, characterName, isDuplicate);
            if (isDuplicate)
            {
                duplicateAddRows.Add(previewRow);
            }
            else
            {
                addRows.Add(previewRow);
            }
        }

        var cases = new Dictionary<string, IReadOnlyList<GalleryFilterCombinationImportPreviewRowDto>>(StringComparer.Ordinal)
        {
            ["update"] = updateRows.Take(30).ToArray(),
            ["automaticTitleMerge"] = automaticTitleMergeRows.Take(30).ToArray(),
            ["automaticCharacterMove"] = automaticCharacterMoveRows.Take(30).ToArray(),
            ["explicitMerge"] = explicitMergeRows.Take(30).ToArray(),
            ["add"] = addRows.Take(30).ToArray(),
            ["duplicateAddSkip"] = duplicateAddRows.Take(30).ToArray()
        };

        return new GalleryFilterCombinationImportPreviewDto(
            imports.Length,
            addRows.Count,
            updateRows.Length,
            explicitMergeRows.Length,
            automaticTitleMergeRows.Length,
            automaticCharacterMoveRows.Length,
            duplicateAddRows.Count,
            cases["update"],
            cases);
    }

    private static GalleryFilterCombinationImportPreviewRowDto CreateGalleryFilterCombinationPreviewRow(
        GalleryFilterCombinationImportState state,
        string action,
        string detail) =>
        new(
            state.Row.RowNumber,
            action,
            DisplayImportValue(state.Current.CategoryName),
            state.Current.TitleName,
            DisplayImportCharacter(state.Current.CharacterName),
            DisplayImportValue(state.TargetCategoryName),
            state.TargetTitleName,
            DisplayImportCharacter(state.TargetCharacterName),
            detail);

    private static GalleryFilterCombinationImportPreviewRowDto CreateGalleryFilterCombinationAddPreviewRow(
        GalleryFilterCombinationImportRowDto row,
        string categoryName,
        string titleName,
        string characterName,
        bool isDuplicate) =>
        new(
            row.RowNumber,
            isDuplicate ? "重複のためスキップ" : "追加",
            string.Empty,
            string.Empty,
            string.Empty,
            DisplayImportValue(categoryName),
            titleName,
            DisplayImportCharacter(characterName),
            string.Empty);

    private static GalleryFilterCombinationImportPlan BuildGalleryFilterCombinationImportPlan(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<GalleryFilterCombinationImportRowDto> imports)
    {
        var updates = new List<GalleryFilterCombinationImportState>();
        var addRows = new List<GalleryFilterCombinationImportRowDto>();
        foreach (var row in imports)
        {
            if (row.Add || row.IsLegacyRow)
            {
                addRows.Add(row);
                continue;
            }
            if (row.CombinationId is not { } combinationId)
            {
                throw new InvalidDataException($"{row.RowNumber}行目は combination_id を指定するか、Add を True にしてください。");
            }

            var current = ReadGalleryFilterCombinationImportState(connection, transaction, combinationId, row.RowNumber);
            updates.Add(new GalleryFilterCombinationImportState(
                row,
                current,
                FirstNonEmpty(row.NewCategoryName, current.CategoryName),
                FirstNonEmpty(row.NewTitleName, current.TitleName),
                NormalizeImportCharacterName(FirstNonEmpty(row.NewCharacterName, current.CharacterName))));
        }

        foreach (var titleGroup in updates
                     .Where(state => state.Row.MergeCombinationId is null)
                     .GroupBy(state => state.Current.TitleFilterId))
        {
            var destinations = titleGroup
                .Select(state => BuildTitleDestinationKey(state.TargetCategoryName, state.TargetTitleName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (destinations.Length > 1)
            {
                throw new InvalidDataException($"Title filter_id {titleGroup.Key} の更新先 Category / Title が複数指定されています。TSV内の値を統一してください。");
            }
        }

        foreach (var characterGroup in updates
                     .Where(state => state.Row.MergeCombinationId is null && state.Current.CharacterFilterId is not null)
                     .GroupBy(state => state.Current.CharacterFilterId!.Value))
        {
            var destinations = characterGroup
                .Select(state => state.TargetCharacterName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (destinations.Length > 1)
            {
                throw new InvalidDataException($"Character filter_id {characterGroup.Key} の更新先 Character が複数指定されています。TSV内の値を統一してください。");
            }
        }

        foreach (var destinationGroup in updates
                     .Where(state => state.Row.MergeCombinationId is null)
                     .GroupBy(state => BuildTitleDestinationKey(state.TargetCategoryName, state.TargetTitleName), StringComparer.OrdinalIgnoreCase))
        {
            var representative = destinationGroup.First();
            var existingTitleId = FindTitleFilterIdByNameAndCategory(
                connection,
                transaction,
                representative.TargetCategoryName,
                representative.TargetTitleName);
            var targetTitleId = existingTitleId ?? destinationGroup.Min(state => state.Current.TitleFilterId);
            foreach (var state in destinationGroup)
            {
                state.TargetTitleId = targetTitleId;
            }
        }

        return new GalleryFilterCombinationImportPlan(updates, addRows);
    }

    private static void ApplyAutomaticTitleMerges(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalleryFilterCombinationImportPlan plan)
    {
        var automaticUpdates = plan.Updates
            .Where(state => state.Row.MergeCombinationId is null)
            .ToArray();

        foreach (var targetGroup in automaticUpdates.GroupBy(state => state.TargetTitleId))
        {
            var representative = targetGroup.First();
            var categoryId = GetOrCreateFilterCategoryId(connection, transaction, representative.TargetCategoryName);

            foreach (var state in targetGroup.Where(state => !string.IsNullOrWhiteSpace(state.TargetCharacterName)))
            {
                var targetCharacterId = GetOrCreateGalleryFilterId(
                    connection,
                    transaction,
                    "character",
                    state.TargetCharacterName,
                    categoryId,
                    targetGroup.Key);
                if (state.Current.CharacterFilterId is { } sourceCharacterId && sourceCharacterId != targetCharacterId)
                {
                    MergeCharacterFilterForImport(connection, transaction, sourceCharacterId, targetCharacterId);
                }
                else if (state.Current.CharacterFilterId is null)
                {
                    AssignCharacterToCombinationItems(connection, transaction, state.Current.CombinationId, targetCharacterId);
                }
            }

            foreach (var state in targetGroup.Where(state => string.IsNullOrWhiteSpace(state.TargetCharacterName) && state.Current.CharacterFilterId is not null))
            {
                AssignUnassignedToCombinationItems(
                    connection,
                    transaction,
                    state.Current.CombinationId,
                    state.Current.CharacterFilterId!.Value,
                    categoryId,
                    targetGroup.Key);
            }

            foreach (var sourceTitleId in targetGroup
                         .Select(state => state.Current.TitleFilterId)
                         .Where(sourceTitleId => sourceTitleId != targetGroup.Key)
                         .Distinct())
            {
                MergeTitleFilterForImport(connection, transaction, sourceTitleId, targetGroup.Key, categoryId);
            }

            // Existing final names can belong to a source Title. Delete those sources before renaming the target.
            UpdateTitleFilterForImport(
                connection,
                transaction,
                targetGroup.Key,
                representative.TargetTitleName,
                categoryId);
        }
    }

    private static void ApplyImportedCombinationUseFlags(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalleryFilterCombinationImportPlan plan)
    {
        var requestedFlags = plan.Updates
            .Where(state => state.Row.MergeCombinationId is null && state.Row.UseFlag is not null)
            .GroupBy(
                state => $"{state.TargetTitleId}\u001f{state.TargetCharacterName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                TargetTitleId = group.First().TargetTitleId,
                TargetCharacterName = group.First().TargetCharacterName,
                UseFlag = group.Max(state => state.Row.UseFlag!.Value)
            })
            .ToArray();

        foreach (var requested in requestedFlags)
        {
            var categoryId = GetFilterCategoryId(connection, transaction, requested.TargetTitleId);
            long? characterId = string.IsNullOrWhiteSpace(requested.TargetCharacterName)
                ? null
                : FindCharacterFilterIdByNameAndParent(connection, transaction, requested.TargetTitleId, requested.TargetCharacterName)
                    ?? throw new InvalidOperationException("統合後のCharacterフィルタが見つかりません。");
            var combinationId = GetOrCreateGalleryFilterCombinationId(
                connection,
                transaction,
                categoryId,
                requested.TargetTitleId,
                characterId,
                requested.UseFlag);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_filter_combinations SET use_flg = $useFlag, updated_at = CURRENT_TIMESTAMP WHERE combination_id = $id;";
            update.Parameters.AddWithValue("$useFlag", requested.UseFlag);
            update.Parameters.AddWithValue("$id", combinationId);
            update.ExecuteNonQuery();
        }
    }

    private static void UpdateTitleFilterForImport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long titleFilterId,
        string titleName,
        long? categoryId)
    {
        var previousName = GetGalleryFilterCanonicalName(connection, transaction, titleFilterId);
        if (!string.Equals(previousName, titleName, StringComparison.Ordinal))
        {
            InsertGalleryFilterAlias(connection, transaction, titleFilterId, "title", previousName);
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_filters SET canonical_name = $name, filter_category_id = $categoryId, category_id = $categoryId, updated_at = CURRENT_TIMESTAMP WHERE filter_id = $id AND filter_type = 'title';";
            update.Parameters.AddWithValue("$name", titleName);
            update.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
            update.Parameters.AddWithValue("$id", titleFilterId);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("統合先のTitleフィルタが見つかりません。");
            }
        }

        using var updateChildren = connection.CreateCommand();
        updateChildren.Transaction = transaction;
        updateChildren.CommandText = "UPDATE gallery_filters SET filter_category_id = $categoryId, category_id = $categoryId, updated_at = CURRENT_TIMESTAMP WHERE parent_filter_id = $titleId;";
        updateChildren.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        updateChildren.Parameters.AddWithValue("$titleId", titleFilterId);
        updateChildren.ExecuteNonQuery();
    }

    private static void MergeTitleFilterForImport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceTitleId,
        long targetTitleId,
        long? categoryId)
    {
        if (sourceTitleId == targetTitleId)
        {
            return;
        }

        var sourceCharacters = ListCharacterFilterIds(connection, transaction, sourceTitleId);
        foreach (var sourceCharacterId in sourceCharacters)
        {
            var characterName = GetGalleryFilterCanonicalName(connection, transaction, sourceCharacterId);
            var targetCharacterId = FindCharacterFilterIdByNameAndParent(connection, transaction, targetTitleId, characterName);
            if (targetCharacterId is { } existingCharacterId && existingCharacterId != sourceCharacterId)
            {
                MergeCharacterFilterForImport(connection, transaction, sourceCharacterId, existingCharacterId);
            }
            else
            {
                using var moveCharacter = connection.CreateCommand();
                moveCharacter.Transaction = transaction;
                moveCharacter.CommandText = "UPDATE gallery_filters SET parent_filter_id = $targetTitleId, filter_category_id = $categoryId, category_id = $categoryId, updated_at = CURRENT_TIMESTAMP WHERE filter_id = $id;";
                moveCharacter.Parameters.AddWithValue("$targetTitleId", targetTitleId);
                moveCharacter.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
                moveCharacter.Parameters.AddWithValue("$id", sourceCharacterId);
                moveCharacter.ExecuteNonQuery();
            }
        }

        CopyGalleryFilterItemMappings(connection, transaction, sourceTitleId, targetTitleId);
        CopyGalleryFilterAliases(connection, transaction, sourceTitleId, targetTitleId, "title");
        CopyGalleryFilterVisibility(connection, transaction, sourceTitleId, targetTitleId);
        using (var deleteSourceMappings = connection.CreateCommand())
        {
            deleteSourceMappings.Transaction = transaction;
            deleteSourceMappings.CommandText = "DELETE FROM gallery_item_filters WHERE filter_id = $sourceId;";
            deleteSourceMappings.Parameters.AddWithValue("$sourceId", sourceTitleId);
            deleteSourceMappings.ExecuteNonQuery();
        }
        using var deleteSource = connection.CreateCommand();
        deleteSource.Transaction = transaction;
        deleteSource.CommandText = "DELETE FROM gallery_filters WHERE filter_id = $sourceId AND filter_type = 'title';";
        deleteSource.Parameters.AddWithValue("$sourceId", sourceTitleId);
        deleteSource.ExecuteNonQuery();
    }

    private static void MergeCharacterFilterForImport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceCharacterId,
        long targetCharacterId)
    {
        if (sourceCharacterId == targetCharacterId)
        {
            return;
        }

        CopyGalleryFilterItemMappings(connection, transaction, sourceCharacterId, targetCharacterId);
        CopyGalleryFilterAliases(connection, transaction, sourceCharacterId, targetCharacterId, "character");
        CopyGalleryFilterVisibility(connection, transaction, sourceCharacterId, targetCharacterId);
        using (var deleteSourceMappings = connection.CreateCommand())
        {
            deleteSourceMappings.Transaction = transaction;
            deleteSourceMappings.CommandText = "DELETE FROM gallery_item_filters WHERE filter_id = $sourceId;";
            deleteSourceMappings.Parameters.AddWithValue("$sourceId", sourceCharacterId);
            deleteSourceMappings.ExecuteNonQuery();
        }
        using var deleteSource = connection.CreateCommand();
        deleteSource.Transaction = transaction;
        deleteSource.CommandText = "DELETE FROM gallery_filters WHERE filter_id = $sourceId AND filter_type = 'character';";
        deleteSource.Parameters.AddWithValue("$sourceId", sourceCharacterId);
        deleteSource.ExecuteNonQuery();
    }

    private static void AssignCharacterToCombinationItems(SqliteConnection connection, SqliteTransaction transaction, long combinationId, long characterFilterId)
    {
        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = "INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id) SELECT gid, $characterId FROM gallery_item_filter_combinations WHERE combination_id = $combinationId;";
            copy.Parameters.AddWithValue("$characterId", characterFilterId);
            copy.Parameters.AddWithValue("$combinationId", combinationId);
            copy.ExecuteNonQuery();
        }
        using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM gallery_item_filter_combinations WHERE combination_id = $combinationId;";
        remove.Parameters.AddWithValue("$combinationId", combinationId);
        remove.ExecuteNonQuery();
    }

    private static void AssignUnassignedToCombinationItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceCombinationId,
        long sourceCharacterFilterId,
        long? categoryId,
        long targetTitleFilterId)
    {
        var targetCombinationId = GetOrCreateGalleryFilterCombinationId(
            connection,
            transaction,
            categoryId,
            targetTitleFilterId,
            null,
            1);

        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = transaction;
            copy.CommandText = "INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id) SELECT gid, $targetId FROM gallery_item_filter_combinations WHERE combination_id = $sourceId;";
            copy.Parameters.AddWithValue("$targetId", targetCombinationId);
            copy.Parameters.AddWithValue("$sourceId", sourceCombinationId);
            copy.ExecuteNonQuery();
        }
        using (var clearCharacter = connection.CreateCommand())
        {
            clearCharacter.Transaction = transaction;
            clearCharacter.CommandText = "DELETE FROM gallery_item_filters WHERE filter_id = $characterId AND gid IN (SELECT gid FROM gallery_item_filter_combinations WHERE combination_id = $sourceId);";
            clearCharacter.Parameters.AddWithValue("$characterId", sourceCharacterFilterId);
            clearCharacter.Parameters.AddWithValue("$sourceId", sourceCombinationId);
            clearCharacter.ExecuteNonQuery();
        }
        using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = "DELETE FROM gallery_item_filter_combinations WHERE combination_id = $sourceId;";
        remove.Parameters.AddWithValue("$sourceId", sourceCombinationId);
        remove.ExecuteNonQuery();
    }

    private static void CopyGalleryFilterItemMappings(SqliteConnection connection, SqliteTransaction transaction, long sourceFilterId, long targetFilterId)
    {
        using var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = "INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id) SELECT gid, $targetId FROM gallery_item_filters WHERE filter_id = $sourceId;";
        copy.Parameters.AddWithValue("$targetId", targetFilterId);
        copy.Parameters.AddWithValue("$sourceId", sourceFilterId);
        copy.ExecuteNonQuery();
    }

    private static void CopyGalleryFilterAliases(SqliteConnection connection, SqliteTransaction transaction, long sourceFilterId, long targetFilterId, string filterType)
    {
        using var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = "INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias) SELECT $targetId, filter_type, alias FROM gallery_filter_aliases WHERE filter_id = $sourceId; INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias) SELECT $targetId, $filterType, canonical_name FROM gallery_filters WHERE filter_id = $sourceId;";
        copy.Parameters.AddWithValue("$targetId", targetFilterId);
        copy.Parameters.AddWithValue("$sourceId", sourceFilterId);
        copy.Parameters.AddWithValue("$filterType", filterType);
        copy.ExecuteNonQuery();
    }

    private static void CopyGalleryFilterVisibility(SqliteConnection connection, SqliteTransaction transaction, long sourceFilterId, long targetFilterId)
    {
        using var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = "INSERT OR IGNORE INTO gallery_filter_section_visibility (filter_id, gallery_category, is_visible) SELECT $targetId, gallery_category, is_visible FROM gallery_filter_section_visibility WHERE filter_id = $sourceId;";
        copy.Parameters.AddWithValue("$targetId", targetFilterId);
        copy.Parameters.AddWithValue("$sourceId", sourceFilterId);
        copy.ExecuteNonQuery();
    }

    private static IReadOnlyList<long> ListCharacterFilterIds(SqliteConnection connection, SqliteTransaction transaction, long titleFilterId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT filter_id FROM gallery_filters WHERE filter_type = 'character' AND parent_filter_id = $titleId ORDER BY filter_id;";
        command.Parameters.AddWithValue("$titleId", titleFilterId);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    private static long? FindTitleFilterIdByNameAndCategory(SqliteConnection connection, SqliteTransaction transaction, string categoryName, string titleName)
    {
        categoryName = NormalizeFilterCategoryName(categoryName);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT filter.filter_id FROM gallery_filters AS filter LEFT JOIN filter_categories AS category ON category.category_id = filter.filter_category_id WHERE filter.filter_type = 'title' AND COALESCE(category.name, '') = $categoryName COLLATE NOCASE AND filter.canonical_name = $titleName COLLATE NOCASE ORDER BY filter.filter_id LIMIT 1;";
        command.Parameters.AddWithValue("$categoryName", categoryName);
        command.Parameters.AddWithValue("$titleName", titleName.Trim());
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private static long? FindCharacterFilterIdByNameAndParent(SqliteConnection connection, SqliteTransaction transaction, long titleFilterId, string characterName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT filter.filter_id FROM gallery_filters AS filter LEFT JOIN gallery_filter_aliases AS alias ON alias.filter_id = filter.filter_id WHERE filter.filter_type = 'character' AND filter.parent_filter_id = $titleId AND (filter.canonical_name = $name COLLATE NOCASE OR alias.alias = $name COLLATE NOCASE) ORDER BY filter.filter_id LIMIT 1;";
        command.Parameters.AddWithValue("$titleId", titleFilterId);
        command.Parameters.AddWithValue("$name", characterName.Trim());
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private static long? GetFilterCategoryId(SqliteConnection connection, SqliteTransaction transaction, long titleFilterId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT filter_category_id FROM gallery_filters WHERE filter_id = $id AND filter_type = 'title';";
        command.Parameters.AddWithValue("$id", titleFilterId);
        var result = command.ExecuteScalar();
        return result is null || result is DBNull ? null : Convert.ToInt64(result);
    }

    private static string GetGalleryFilterCanonicalName(SqliteConnection connection, SqliteTransaction transaction, long filterId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT canonical_name FROM gallery_filters WHERE filter_id = $id;";
        command.Parameters.AddWithValue("$id", filterId);
        return command.ExecuteScalar() as string ?? throw new InvalidOperationException("フィルタが見つかりません。");
    }

    private static void InsertGalleryFilterAlias(SqliteConnection connection, SqliteTransaction transaction, long filterId, string filterType, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias) VALUES ($filterId, $filterType, $alias);";
        insert.Parameters.AddWithValue("$filterId", filterId);
        insert.Parameters.AddWithValue("$filterType", filterType);
        insert.Parameters.AddWithValue("$alias", alias.Trim());
        insert.ExecuteNonQuery();
    }

    private static GalleryFilterCombinationCurrentState ReadGalleryFilterCombinationImportState(SqliteConnection connection, SqliteTransaction transaction, long combinationId, int rowNumber)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT combination.combination_id, combination.filter_category_id, COALESCE(category.name, ''), combination.title_filter_id, title_filter.canonical_name, combination.character_filter_id, COALESCE(character_filter.canonical_name, '') FROM gallery_filter_combinations AS combination LEFT JOIN filter_categories AS category ON category.category_id = combination.filter_category_id JOIN gallery_filters AS title_filter ON title_filter.filter_id = combination.title_filter_id LEFT JOIN gallery_filters AS character_filter ON character_filter.filter_id = combination.character_filter_id WHERE combination.combination_id = $id;";
        command.Parameters.AddWithValue("$id", combinationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException($"{rowNumber}行目の combination_id {combinationId} は存在しません。");
        }
        return new GalleryFilterCombinationCurrentState(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.GetString(6));
    }

    private static string NormalizeImportCharacterName(string value) =>
        string.Equals(value.Trim(), UnassignedFilterValue, StringComparison.OrdinalIgnoreCase) ? string.Empty : value.Trim();

    private static string DisplayImportCharacter(string value) => string.IsNullOrWhiteSpace(value) ? UnassignedFilterValue : value;

    private static string DisplayImportValue(string value) => string.IsNullOrWhiteSpace(value) ? "未分類" : value;

    private static bool HasImportChange(GalleryFilterCombinationImportState state) =>
        !string.Equals(state.Current.CategoryName, state.TargetCategoryName, StringComparison.Ordinal) ||
        !string.Equals(state.Current.TitleName, state.TargetTitleName, StringComparison.Ordinal) ||
        !string.Equals(NormalizeImportCharacterName(state.Current.CharacterName), state.TargetCharacterName, StringComparison.Ordinal);

    private static string BuildTitleDestinationKey(string categoryName, string titleName) =>
        $"{NormalizeFilterCategoryName(categoryName)}\u001f{titleName.Trim()}";

    private sealed class GalleryFilterCombinationImportPlan
    {
        public GalleryFilterCombinationImportPlan(IReadOnlyList<GalleryFilterCombinationImportState> updates, IReadOnlyList<GalleryFilterCombinationImportRowDto> addRows)
        {
            Updates = updates;
            AddRows = addRows;
        }

        public IReadOnlyList<GalleryFilterCombinationImportState> Updates { get; }
        public IReadOnlyList<GalleryFilterCombinationImportRowDto> AddRows { get; }
    }

    private sealed class GalleryFilterCombinationImportState
    {
        public GalleryFilterCombinationImportState(
            GalleryFilterCombinationImportRowDto row,
            GalleryFilterCombinationCurrentState current,
            string targetCategoryName,
            string targetTitleName,
            string targetCharacterName)
        {
            Row = row;
            Current = current;
            TargetCategoryName = NormalizeFilterCategoryName(targetCategoryName);
            TargetTitleName = targetTitleName.Trim();
            TargetCharacterName = NormalizeImportCharacterName(targetCharacterName);
            TargetTitleId = current.TitleFilterId;
        }

        public GalleryFilterCombinationImportRowDto Row { get; }
        public GalleryFilterCombinationCurrentState Current { get; }
        public string TargetCategoryName { get; }
        public string TargetTitleName { get; }
        public string TargetCharacterName { get; }
        public long TargetTitleId { get; set; }
    }

    private sealed record GalleryFilterCombinationCurrentState(
        long CombinationId,
        long? FilterCategoryId,
        string CategoryName,
        long TitleFilterId,
        string TitleName,
        long? CharacterFilterId,
        string CharacterName);


    private static bool HasGalleryFilterCombinationImportContent(GalleryFilterCombinationImportRowDto row) =>
        row.CombinationId is not null ||
        row.Add ||
        row.MergeCombinationId is not null ||
        row.UseFlag is not null ||
        !string.IsNullOrWhiteSpace(row.NewCategoryName) ||
        !string.IsNullOrWhiteSpace(row.NewTitleName) ||
        !string.IsNullOrWhiteSpace(row.NewCharacterName) ||
        row.IsLegacyRow;

    private static bool AddGalleryFilterCombination(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalleryFilterCombinationImportRowDto row)
    {
        if (row.MergeCombinationId is not null)
        {
            throw new InvalidDataException($"{row.RowNumber}行目は Add と Merge を同時に指定できません。");
        }

        var categoryName = FirstNonEmpty(row.NewCategoryName, row.IsLegacyRow ? row.CategoryName : string.Empty);
        var titleName = FirstNonEmpty(row.NewTitleName, row.IsLegacyRow ? row.TitleName : string.Empty);
        var characterName = NormalizeImportCharacterName(FirstNonEmpty(row.NewCharacterName, row.IsLegacyRow ? row.CharacterName : string.Empty));
        if (string.IsNullOrWhiteSpace(titleName))
        {
            throw new InvalidDataException($"{row.RowNumber}行目は Add の場合 New_Title が必要です。");
        }

        if (FindGalleryFilterCombinationIdByNames(connection, transaction, categoryName, titleName, characterName) is not null)
        {
            return false;
        }

        var categoryId = GetOrCreateFilterCategoryId(connection, transaction, categoryName);
        var titleId = GetOrCreateGalleryFilterId(connection, transaction, "title", titleName, categoryId, null);
        long? characterId = string.IsNullOrWhiteSpace(characterName)
            ? null
            : GetOrCreateGalleryFilterId(connection, transaction, "character", characterName, categoryId, titleId);
        GetOrCreateGalleryFilterCombinationId(connection, transaction, categoryId, titleId, characterId, row.UseFlag ?? 1);
        return true;
    }

    private static long? FindGalleryFilterCombinationIdByNames(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string categoryName,
        string titleName,
        string characterName)
    {
        categoryName = NormalizeFilterCategoryName(categoryName);
        characterName = NormalizeImportCharacterName(characterName);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT combination.combination_id
            FROM gallery_filter_combinations AS combination
            JOIN gallery_filters AS title_filter ON title_filter.filter_id = combination.title_filter_id
            LEFT JOIN gallery_filters AS character_filter ON character_filter.filter_id = combination.character_filter_id
            LEFT JOIN filter_categories AS category ON category.category_id = combination.filter_category_id
            WHERE COALESCE(category.name, '') = $categoryName COLLATE NOCASE
              AND title_filter.canonical_name = $titleName COLLATE NOCASE
              AND COALESCE(character_filter.canonical_name, '') = $characterName COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$categoryName", categoryName);
        command.Parameters.AddWithValue("$titleName", titleName.Trim());
        command.Parameters.AddWithValue("$characterName", characterName.Trim());
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private static void UpdateGalleryFilterCombination(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalleryFilterCombinationImportRowDto row)
    {
        var current = GetGalleryFilterCombination(connection, transaction, row.CombinationId!.Value, row.RowNumber);
        var categoryId = current.FilterCategoryId;
        if (!string.IsNullOrWhiteSpace(row.NewCategoryName))
        {
            categoryId = GetOrCreateFilterCategoryId(connection, transaction, row.NewCategoryName);
            using var updateCategory = connection.CreateCommand();
            updateCategory.Transaction = transaction;
            updateCategory.CommandText = "UPDATE gallery_filters SET filter_category_id = $categoryId, category_id = $categoryId, updated_at = CURRENT_TIMESTAMP WHERE filter_id = $titleId OR parent_filter_id = $titleId;";
            updateCategory.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
            updateCategory.Parameters.AddWithValue("$titleId", current.TitleFilterId);
            updateCategory.ExecuteNonQuery();
        }

        if (!string.IsNullOrWhiteSpace(row.NewTitleName))
        {
            RenameGalleryFilter(connection, transaction, current.TitleFilterId, "title", null, row.NewTitleName, row.RowNumber);
        }

        var characterId = current.CharacterFilterId;
        if (!string.IsNullOrWhiteSpace(row.NewCharacterName))
        {
            if (characterId is { } existingCharacterId)
            {
                RenameGalleryFilter(connection, transaction, existingCharacterId, "character", current.TitleFilterId, row.NewCharacterName, row.RowNumber);
            }
            else
            {
                characterId = GetOrCreateGalleryFilterId(connection, transaction, "character", row.NewCharacterName, categoryId, current.TitleFilterId);
            }
        }

        var duplicateCombinationId = FindGalleryFilterCombinationId(connection, transaction, categoryId, current.TitleFilterId, characterId);
        if (duplicateCombinationId is { } duplicateId && duplicateId != current.CombinationId)
        {
            throw new InvalidDataException($"{row.RowNumber}行目の更新後の組合せは combination_id {duplicateId} と重複します。Merge を使用してください。");
        }

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE gallery_filter_combinations
            SET filter_category_id = $categoryId,
                title_filter_id = $titleId,
                character_filter_id = $characterId,
                use_flg = COALESCE($useFlag, use_flg),
                updated_at = CURRENT_TIMESTAMP
            WHERE combination_id = $combinationId;
            """;
        update.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        update.Parameters.AddWithValue("$titleId", current.TitleFilterId);
        update.Parameters.AddWithValue("$characterId", (object?)characterId ?? DBNull.Value);
        update.Parameters.AddWithValue("$useFlag", (object?)row.UseFlag ?? DBNull.Value);
        update.Parameters.AddWithValue("$combinationId", current.CombinationId);
        update.ExecuteNonQuery();
    }

    private static void MergeGalleryFilterCombination(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sourceCombinationId,
        long targetCombinationId,
        int rowNumber)
    {
        if (sourceCombinationId == targetCombinationId)
        {
            throw new InvalidDataException($"{rowNumber}行目は自分自身へは統合できません。");
        }
        _ = GetGalleryFilterCombination(connection, transaction, sourceCombinationId, rowNumber);
        _ = GetGalleryFilterCombination(connection, transaction, targetCombinationId, rowNumber);

        using (var moveItems = connection.CreateCommand())
        {
            moveItems.Transaction = transaction;
            moveItems.CommandText = "INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id) SELECT gid, $targetId FROM gallery_item_filter_combinations WHERE combination_id = $sourceId;";
            moveItems.Parameters.AddWithValue("$targetId", targetCombinationId);
            moveItems.Parameters.AddWithValue("$sourceId", sourceCombinationId);
            moveItems.ExecuteNonQuery();
        }
        using (var removeSourceMappings = connection.CreateCommand())
        {
            removeSourceMappings.Transaction = transaction;
            removeSourceMappings.CommandText = "DELETE FROM gallery_item_filter_combinations WHERE combination_id = $sourceId;";
            removeSourceMappings.Parameters.AddWithValue("$sourceId", sourceCombinationId);
            removeSourceMappings.ExecuteNonQuery();
        }
        using var disableSource = connection.CreateCommand();
        disableSource.Transaction = transaction;
        disableSource.CommandText = "UPDATE gallery_filter_combinations SET use_flg = 0, updated_at = CURRENT_TIMESTAMP WHERE combination_id = $sourceId;";
        disableSource.Parameters.AddWithValue("$sourceId", sourceCombinationId);
        disableSource.ExecuteNonQuery();
    }

    private static (long CombinationId, long? FilterCategoryId, long TitleFilterId, long? CharacterFilterId) GetGalleryFilterCombination(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long combinationId,
        int rowNumber)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT combination_id, filter_category_id, title_filter_id, character_filter_id FROM gallery_filter_combinations WHERE combination_id = $id;";
        command.Parameters.AddWithValue("$id", combinationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException($"{rowNumber}行目の combination_id {combinationId} は存在しません。");
        }
        return (
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private static long? FindGalleryFilterCombinationId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? categoryId,
        long titleFilterId,
        long? characterFilterId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT combination_id
            FROM gallery_filter_combinations
            WHERE COALESCE(filter_category_id, -1) = COALESCE($categoryId, -1)
              AND title_filter_id = $titleId
              AND ((character_filter_id IS NULL AND $characterId IS NULL) OR character_filter_id = $characterId)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$titleId", titleFilterId);
        command.Parameters.AddWithValue("$characterId", (object?)characterFilterId ?? DBNull.Value);
        var result = command.ExecuteScalar();
        return result is null ? null : Convert.ToInt64(result);
    }

    private static long GetOrCreateGalleryFilterCombinationId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? categoryId,
        long titleFilterId,
        long? characterFilterId,
        int useFlag)
    {
        var existingId = FindGalleryFilterCombinationId(connection, transaction, categoryId, titleFilterId, characterFilterId);
        if (existingId is { } combinationId)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_filter_combinations SET use_flg = $useFlag, updated_at = CURRENT_TIMESTAMP WHERE combination_id = $id;";
            update.Parameters.AddWithValue("$useFlag", useFlag);
            update.Parameters.AddWithValue("$id", combinationId);
            update.ExecuteNonQuery();
            return combinationId;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO gallery_filter_combinations (filter_category_id, title_filter_id, character_filter_id, use_flg)
            VALUES ($categoryId, $titleId, $characterId, $useFlag);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$titleId", titleFilterId);
        insert.Parameters.AddWithValue("$characterId", (object?)characterFilterId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$useFlag", useFlag);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    private static string NormalizeFilterCategoryName(string categoryName) =>
        string.Equals(categoryName.Trim(), "未分類", StringComparison.Ordinal) ? string.Empty : categoryName.Trim();

    private static long? GetOrCreateFilterCategoryId(SqliteConnection connection, SqliteTransaction transaction, string categoryName)
    {
        categoryName = NormalizeFilterCategoryName(categoryName);
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return null;
        }
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT category_id FROM filter_categories WHERE name = $name COLLATE NOCASE;";
            select.Parameters.AddWithValue("$name", categoryName);
            var existing = select.ExecuteScalar();
            if (existing is not null)
            {
                return Convert.ToInt64(existing);
            }
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO filter_categories (name, position) VALUES ($name, (SELECT COALESCE(MAX(position), -1) + 1 FROM filter_categories)); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", categoryName);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    private static void RenameGalleryFilter(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long filterId,
        string filterType,
        long? parentTitleId,
        string newName,
        int rowNumber)
    {
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = filterType == "character"
            ? """
                SELECT filter.filter_id
                FROM gallery_filters AS filter
                LEFT JOIN gallery_filter_aliases AS alias ON alias.filter_id = filter.filter_id
                WHERE filter.filter_type = 'character'
                  AND filter.parent_filter_id = $parentId
                  AND filter.filter_id <> $id
                  AND (filter.canonical_name = $name COLLATE NOCASE OR alias.alias = $name COLLATE NOCASE)
                LIMIT 1;
                """
            : """
                SELECT filter.filter_id
                FROM gallery_filters AS filter
                LEFT JOIN gallery_filter_aliases AS alias ON alias.filter_id = filter.filter_id
                WHERE filter.filter_type = 'title'
                  AND filter.filter_id <> $id
                  AND (filter.canonical_name = $name COLLATE NOCASE OR alias.alias = $name COLLATE NOCASE)
                LIMIT 1;
                """;
        existing.Parameters.AddWithValue("$id", filterId);
        existing.Parameters.AddWithValue("$name", newName);
        if (filterType == "character")
        {
            existing.Parameters.AddWithValue("$parentId", (object?)parentTitleId ?? DBNull.Value);
        }
        var conflict = existing.ExecuteScalar();
        if (conflict is not null)
        {
            throw new InvalidDataException($"{rowNumber}行目の {filterType} 名は既存のフィルタと重複します。Merge を使用してください。");
        }

        string? previousName;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT canonical_name FROM gallery_filters WHERE filter_id = $id;";
            read.Parameters.AddWithValue("$id", filterId);
            previousName = read.ExecuteScalar() as string;
        }
        if (previousName is null)
        {
            throw new InvalidDataException($"{rowNumber}行目のフィルタが見つかりません。");
        }
        if (string.Equals(previousName, newName, StringComparison.Ordinal))
        {
            return;
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE gallery_filters SET canonical_name = $name, updated_at = CURRENT_TIMESTAMP WHERE filter_id = $id;";
            update.Parameters.AddWithValue("$name", newName);
            update.Parameters.AddWithValue("$id", filterId);
            update.ExecuteNonQuery();
        }
        foreach (var aliasName in new[] { previousName, newName })
        {
            using var alias = connection.CreateCommand();
            alias.Transaction = transaction;
            alias.CommandText = "INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias) VALUES ($id, $type, $alias);";
            alias.Parameters.AddWithValue("$id", filterId);
            alias.Parameters.AddWithValue("$type", filterType);
            alias.Parameters.AddWithValue("$alias", aliasName);
            alias.ExecuteNonQuery();
        }
    }

    private static string FirstNonEmpty(string primary, string fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary.Trim() : fallback.Trim();

    private static long? FindReusableGalleryFilterDefinitionId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string filterType,
        string canonicalName,
        long? categoryId,
        long? parentTitleId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = filterType == "character"
            ? """
                SELECT filter_id
                FROM gallery_filters
                WHERE filter_type = 'character'
                  AND canonical_name = $name COLLATE NOCASE
                  AND COALESCE(filter_category_id, -1) = COALESCE($categoryId, -1)
                  AND parent_filter_id = $parentId
                ORDER BY filter_id
                LIMIT 1;
                """
            : """
                SELECT filter_id
                FROM gallery_filters
                WHERE filter_type = 'title'
                  AND canonical_name = $name COLLATE NOCASE
                  AND COALESCE(filter_category_id, -1) = COALESCE($categoryId, -1)
                ORDER BY filter_id
                LIMIT 1;
                """;
        command.Parameters.AddWithValue("$name", canonicalName);
        command.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        if (filterType == "character")
        {
            command.Parameters.AddWithValue("$parentId", (object?)parentTitleId ?? DBNull.Value);
        }

        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt64(value);
    }

    public long SaveGalleryFilterDefinition(
        long? filterId,
        string filterType,
        string canonicalName,
        string categoryName,
        long? parentTitleId,
        IReadOnlyList<string> aliases,
        IReadOnlyList<string> visibleCategories)
    {
        filterType = NormalizeGalleryFilterType(filterType);
        canonicalName = canonicalName.Trim();
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            throw new ArgumentException("表示名を入力してください。", nameof(canonicalName));
        }

        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            throw new FileNotFoundException("Gallery SQLiteDBファイルが見つかりません。", ExternalGalleryDatabasePath);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var categoryId = GetExistingFilterCategoryId(connection, transaction, categoryName);
        var normalizedVisibleCategories = visibleCategories
            .Select(category => category.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (filterType == "character" && parentTitleId is { } requestedParentId)
        {
            using var validateParent = connection.CreateCommand();
            validateParent.Transaction = transaction;
            validateParent.CommandText = "SELECT COUNT(*) FROM gallery_filters WHERE filter_id = $id AND filter_type = 'title';";
            validateParent.Parameters.AddWithValue("$id", requestedParentId);
            if (Convert.ToInt64(validateParent.ExecuteScalar()) == 0)
            {
                throw new ArgumentException("Characterの上位Titleが不正です。", nameof(parentTitleId));
            }
        }

        long savedId;
        string? previousCanonicalName = null;
        if (filterId is { } existingId)
        {
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT canonical_name FROM gallery_filters WHERE filter_id = $id AND filter_type = $type;";
                existing.Parameters.AddWithValue("$id", existingId);
                existing.Parameters.AddWithValue("$type", filterType);
                previousCanonicalName = existing.ExecuteScalar() as string;
            }
            if (previousCanonicalName is null)
            {
                throw new InvalidOperationException("編集対象のフィルタが見つかりません。");
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE gallery_filters
                SET canonical_name = $name,
                    filter_category_id = $categoryId,
                    category_id = $categoryId,
                    parent_filter_id = $parentId,
                    updated_at = CURRENT_TIMESTAMP
                WHERE filter_id = $id;
                """;
            update.Parameters.AddWithValue("$name", canonicalName);
            update.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
            update.Parameters.AddWithValue("$parentId", filterType == "character" ? (object?)parentTitleId ?? DBNull.Value : DBNull.Value);
            update.Parameters.AddWithValue("$id", existingId);
            update.ExecuteNonQuery();
            savedId = existingId;
        }
        else if (FindReusableGalleryFilterDefinitionId(connection, transaction, filterType, canonicalName, categoryId, parentTitleId) is { } reusableId)
        {
            savedId = reusableId;
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO gallery_filters (filter_type, canonical_name, filter_category_id, category_id, parent_filter_id)
                VALUES ($type, $name, $categoryId, $categoryId, $parentId);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$type", filterType);
            insert.Parameters.AddWithValue("$name", canonicalName);
            insert.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$parentId", filterType == "character" ? (object?)parentTitleId ?? DBNull.Value : DBNull.Value);
            savedId = Convert.ToInt64(insert.ExecuteScalar());
        }

        var aliasesToSave = aliases
            .Append(canonicalName)
            .Append(previousCanonicalName ?? string.Empty)
            .Select(alias => alias.Trim())
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var alias in aliasesToSave)
        {
            using var insertAlias = connection.CreateCommand();
            insertAlias.Transaction = transaction;
            insertAlias.CommandText = "INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias) VALUES ($filterId, $type, $alias);";
            insertAlias.Parameters.AddWithValue("$filterId", savedId);
            insertAlias.Parameters.AddWithValue("$type", filterType);
            insertAlias.Parameters.AddWithValue("$alias", alias);
            insertAlias.ExecuteNonQuery();
        }

        using (var clearVisibility = connection.CreateCommand())
        {
            clearVisibility.Transaction = transaction;
            clearVisibility.CommandText = "DELETE FROM gallery_filter_section_visibility WHERE filter_id = $filterId;";
            clearVisibility.Parameters.AddWithValue("$filterId", savedId);
            clearVisibility.ExecuteNonQuery();
        }
        foreach (var category in normalizedVisibleCategories)
        {
            using var insertVisibility = connection.CreateCommand();
            insertVisibility.Transaction = transaction;
            insertVisibility.CommandText = "INSERT INTO gallery_filter_section_visibility (filter_id, gallery_category, is_visible) VALUES ($filterId, $category, 1);";
            insertVisibility.Parameters.AddWithValue("$filterId", savedId);
            insertVisibility.Parameters.AddWithValue("$category", category);
            insertVisibility.ExecuteNonQuery();
        }

        SynchronizeGalleryFilterCombinationRecords(connection, transaction);
        if (filterType == "title")
        {
            GetOrCreateGalleryFilterCombinationId(connection, transaction, categoryId, savedId, null, 1);
        }
        else if (parentTitleId is { } parentId)
        {
            var parentCategoryId = GetFilterCategoryId(connection, transaction, parentId);
            GetOrCreateGalleryFilterCombinationId(connection, transaction, parentCategoryId, parentId, savedId, 1);
        }
        SynchronizeGalleryItemFilterCombinationRecords(connection, transaction);
        transaction.Commit();
        return savedId;
    }

    public void DeleteGalleryFilterDefinition(long filterId, string filterType)
    {
        filterType = NormalizeGalleryFilterType(filterType);
        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();

        using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = "SELECT COUNT(*) FROM gallery_filters WHERE filter_id = $id AND filter_type = $type;";
            validate.Parameters.AddWithValue("$id", filterId);
            validate.Parameters.AddWithValue("$type", filterType);
            if (Convert.ToInt64(validate.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException("削除対象のフィルタが見つかりません。");
            }
        }

        using (var disable = connection.CreateCommand())
        {
            disable.Transaction = transaction;
            disable.CommandText = filterType == "title"
                ? "UPDATE gallery_filter_combinations SET use_flg = 0, updated_at = CURRENT_TIMESTAMP WHERE title_filter_id = $id;"
                : "UPDATE gallery_filter_combinations SET use_flg = 0, updated_at = CURRENT_TIMESTAMP WHERE character_filter_id = $id;";
            disable.Parameters.AddWithValue("$id", filterId);
            disable.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void MergeGalleryFilters(string filterType, long targetFilterId, IReadOnlyList<long> sourceFilterIds)
    {
        filterType = NormalizeGalleryFilterType(filterType);
        var sources = sourceFilterIds
            .Where(id => id > 0 && id != targetFilterId)
            .Distinct()
            .ToArray();
        if (sources.Length == 0)
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using (var validateTarget = connection.CreateCommand())
        {
            validateTarget.Transaction = transaction;
            validateTarget.CommandText = "SELECT COUNT(*) FROM gallery_filters WHERE filter_id = $id AND filter_type = $type;";
            validateTarget.Parameters.AddWithValue("$id", targetFilterId);
            validateTarget.Parameters.AddWithValue("$type", filterType);
            if (Convert.ToInt64(validateTarget.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException("統合先のフィルタが見つかりません。");
            }
        }

        long? targetParentTitleId = null;
        if (filterType == "character")
        {
            using var parent = connection.CreateCommand();
            parent.Transaction = transaction;
            parent.CommandText = "SELECT parent_filter_id FROM gallery_filters WHERE filter_id = $id;";
            parent.Parameters.AddWithValue("$id", targetFilterId);
            var value = parent.ExecuteScalar();
            targetParentTitleId = value is null || value is DBNull ? null : (long?)Convert.ToInt64(value);
            if (targetParentTitleId is null)
            {
                throw new InvalidOperationException("Characterフィルタには上位Titleが必要です。");
            }
        }

        foreach (var sourceId in sources)
        {
            using (var validateSource = connection.CreateCommand())
            {
                validateSource.Transaction = transaction;
                validateSource.CommandText = "SELECT COUNT(*) FROM gallery_filters WHERE filter_id = $id AND filter_type = $type;";
                validateSource.Parameters.AddWithValue("$id", sourceId);
                validateSource.Parameters.AddWithValue("$type", filterType);
                if (Convert.ToInt64(validateSource.ExecuteScalar()) == 0)
                {
                    continue;
                }
            }

            if (filterType == "character")
            {
                using var sourceParent = connection.CreateCommand();
                sourceParent.Transaction = transaction;
                sourceParent.CommandText = "SELECT parent_filter_id FROM gallery_filters WHERE filter_id = $id;";
                sourceParent.Parameters.AddWithValue("$id", sourceId);
                var value = sourceParent.ExecuteScalar();
                long? parentId = value is null || value is DBNull ? null : Convert.ToInt64(value);
                if (parentId != targetParentTitleId)
                {
                    throw new InvalidOperationException("異なるTitleに属するCharacterフィルタは統合できません。");
                }
            }

            using (var moveItems = connection.CreateCommand())
            {
                moveItems.Transaction = transaction;
                moveItems.CommandText = "INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id) SELECT gid, $targetId FROM gallery_item_filters WHERE filter_id = $sourceId;";
                moveItems.Parameters.AddWithValue("$targetId", targetFilterId);
                moveItems.Parameters.AddWithValue("$sourceId", sourceId);
                moveItems.ExecuteNonQuery();
            }
            using (var moveAliases = connection.CreateCommand())
            {
                moveAliases.Transaction = transaction;
                moveAliases.CommandText = """
                    INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias)
                    SELECT $targetId, filter_type, alias FROM gallery_filter_aliases WHERE filter_id = $sourceId;
                    INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias)
                    SELECT $targetId, filter_type, canonical_name FROM gallery_filters WHERE filter_id = $sourceId;
                    """;
                moveAliases.Parameters.AddWithValue("$targetId", targetFilterId);
                moveAliases.Parameters.AddWithValue("$sourceId", sourceId);
                moveAliases.ExecuteNonQuery();
            }
            using (var moveVisibility = connection.CreateCommand())
            {
                moveVisibility.Transaction = transaction;
                moveVisibility.CommandText = """
                    INSERT OR IGNORE INTO gallery_filter_section_visibility (filter_id, gallery_category, is_visible)
                    SELECT $targetId, gallery_category, is_visible
                    FROM gallery_filter_section_visibility
                    WHERE filter_id = $sourceId;
                    """;
                moveVisibility.Parameters.AddWithValue("$targetId", targetFilterId);
                moveVisibility.Parameters.AddWithValue("$sourceId", sourceId);
                moveVisibility.ExecuteNonQuery();
            }
            if (filterType == "title")
            {
                using var moveChildren = connection.CreateCommand();
                moveChildren.Transaction = transaction;
                moveChildren.CommandText = "UPDATE gallery_filters SET parent_filter_id = $targetId WHERE parent_filter_id = $sourceId;";
                moveChildren.Parameters.AddWithValue("$targetId", targetFilterId);
                moveChildren.Parameters.AddWithValue("$sourceId", sourceId);
                moveChildren.ExecuteNonQuery();
            }
            using (var deleteMappings = connection.CreateCommand())
            {
                deleteMappings.Transaction = transaction;
                deleteMappings.CommandText = "DELETE FROM gallery_item_filters WHERE filter_id = $sourceId;";
                deleteMappings.Parameters.AddWithValue("$sourceId", sourceId);
                deleteMappings.ExecuteNonQuery();
            }
            using var deleteFilter = connection.CreateCommand();
            deleteFilter.Transaction = transaction;
            deleteFilter.CommandText = "DELETE FROM gallery_filters WHERE filter_id = $sourceId;";
            deleteFilter.Parameters.AddWithValue("$sourceId", sourceId);
            deleteFilter.ExecuteNonQuery();
        }

        SynchronizeGalleryFilterCombinationRecords(connection, transaction);
        SynchronizeGalleryItemFilterCombinationRecords(connection, transaction);
        transaction.Commit();
    }

    public void SetGalleryFilterVisibility(IReadOnlyList<long> filterIds, string galleryCategory, bool isVisible)
    {
        var ids = filterIds.Where(id => id > 0).Distinct().ToArray();
        galleryCategory = galleryCategory.Trim();
        if (ids.Length == 0 || string.IsNullOrWhiteSpace(galleryCategory))
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO gallery_filter_section_visibility (filter_id, gallery_category, is_visible)
            VALUES ($filterId, $galleryCategory, $isVisible)
            ON CONFLICT(filter_id, gallery_category) DO UPDATE SET is_visible = excluded.is_visible;
            """;
        var filterId = command.CreateParameter();
        filterId.ParameterName = "$filterId";
        command.Parameters.Add(filterId);
        command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
        command.Parameters.AddWithValue("$isVisible", isVisible ? 1 : 0);
        foreach (var id in ids)
        {
            filterId.Value = id;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public (int Rating, int Baseline) AdjustGalleryWorkRating(string path, int delta, int? baseline)
    {
        if (delta is not 1 and not -1)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        EnsureExternalGalleryGidSchema();

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        using (var timeout = connection.CreateCommand())
        {
            timeout.CommandText = "PRAGMA busy_timeout = 5000;";
            timeout.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        var currentRating = 0;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT COALESCE(rating, 0) FROM items WHERE current_path = $path;";
            current.Parameters.AddWithValue("$path", path);
            var value = current.ExecuteScalar();
            if (value is null)
            {
                throw new InvalidOperationException("評価対象の作品がSQLiteデータベースに見つかりません。");
            }

            currentRating = Convert.ToInt32((long)value);
        }

        var effectiveBaseline = Math.Max(0, baseline ?? currentRating);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE items
                SET rating = CASE
                        WHEN $delta > 0 THEN MAX($baseline, COALESCE(rating, 0)) + 1
                        ELSE MAX($baseline, COALESCE(rating, 0) - 1)
                    END,
                    updated_at = CURRENT_TIMESTAMP
                WHERE current_path = $path;
                """;
            update.Parameters.AddWithValue("$delta", delta);
            update.Parameters.AddWithValue("$baseline", effectiveBaseline);
            update.Parameters.AddWithValue("$path", path);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("評価対象の作品がSQLiteデータベースに見つかりません。");
            }
        }

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT COALESCE(rating, 0) FROM items WHERE current_path = $path;";
        select.Parameters.AddWithValue("$path", path);
        var rating = Convert.ToInt32((long)(select.ExecuteScalar() ?? 0));
        transaction.Commit();
        return (rating, effectiveBaseline);
    }

    private static string CreateGalleryTitleSearchText(string categoryName, string title)
    {
        var source = $"{categoryName} {title}";
        return $"{source} {FileNameRomanizer.Romanize(source)}".ToLowerInvariant();
    }

    private static string CreateGalleryCharacterSearchText(string title, string character)
    {
        var source = $"{title} {character}";
        return $"{source} {FileNameRomanizer.Romanize(source)}".ToLowerInvariant();
    }

    private static string CreateGalleryTagSearchText(string tag) =>
        $"{tag} {FileNameRomanizer.Romanize(tag)}".ToLowerInvariant();

    public GalleryReverseFilterSelectionDto GetGalleryReverseFilterSelection(
        string galleryCategory,
        string path)
    {
        galleryCategory = galleryCategory.Trim();
        path = path.Trim();
        if (string.IsNullOrWhiteSpace(galleryCategory) || string.IsNullOrWhiteSpace(path) ||
            !File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryReverseFilterSelectionDto([], []);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT
                   title_filter.canonical_name,
                   character_filter.filter_id,
                   COALESCE(character_filter.canonical_name, '')
            FROM items AS item
            JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
            JOIN gallery_filter_combinations AS combination
              ON combination.combination_id = combination_map.combination_id
             AND combination.use_flg <> 0
            JOIN gallery_filters AS title_filter
              ON title_filter.filter_id = combination.title_filter_id
             AND title_filter.filter_type = 'title'
            LEFT JOIN gallery_filters AS character_filter
              ON character_filter.filter_id = combination.character_filter_id
             AND character_filter.filter_type = 'character'
            WHERE item.current_path = $path
              AND item.category = $category
            ORDER BY title_filter.canonical_name COLLATE NOCASE,
                     character_filter.canonical_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$category", galleryCategory);

        var titles = new List<string>();
        var titleSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var characters = new List<GalleryReverseCharacterFilterDto>();
        var characterIds = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var title = reader.GetString(0).Trim();
            if (!string.IsNullOrWhiteSpace(title) && titleSet.Add(title))
            {
                titles.Add(title);
            }

            if (reader.IsDBNull(1))
            {
                continue;
            }
            var characterId = reader.GetInt64(1);
            if (!characterIds.Add(characterId))
            {
                continue;
            }
            characters.Add(new GalleryReverseCharacterFilterDto(
                $"filter:{characterId}",
                reader.GetString(2),
                title));
        }

        return new GalleryReverseFilterSelectionDto(titles, characters);
    }

    public GalleryTitleAssignmentOptionsDto ListGalleryTitleAssignmentOptions(
        string galleryCategory,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> paths)
    {
        galleryCategory = galleryCategory.Trim();
        var targetPaths = paths
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(galleryCategory) || !File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryTitleAssignmentOptionsDto([], [], [], []);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        var creatorNames = creators
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetPaths.Length > 0)
        {
            using var creatorCommand = connection.CreateCommand();
            var pathPlaceholders = string.Join(", ", targetPaths.Select((_, index) => $"$creatorPath{index}"));
            creatorCommand.CommandText = $"""
                SELECT current_path, COALESCE(creator, '')
                FROM items
                WHERE current_path IN ({pathPlaceholders});
                """;
            for (var index = 0; index < targetPaths.Length; index++)
            {
                creatorCommand.Parameters.AddWithValue($"$creatorPath{index}", targetPaths[index]);
            }

            using var creatorReader = creatorCommand.ExecuteReader();
            while (creatorReader.Read())
            {
                var creatorName = creatorReader.GetString(1).Trim();
                if (!string.IsNullOrWhiteSpace(creatorName))
                {
                    creatorNames.Add(creatorName);
                }
            }
        }

        var resolvedCreatorNames = creatorNames
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (resolvedCreatorNames.Length == 0)
        {
            throw new InvalidOperationException("選択した作品にCreator属性がありません。");
        }

        var creatorTitles = new List<GalleryTitleAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var creatorPlaceholders = string.Join(", ", resolvedCreatorNames.Select((_, index) => $"$creator{index}"));
            command.CommandText = $"""
                SELECT title_filter.filter_id,
                       title_filter.canonical_name,
                       COALESCE(filter_category.name, '未分類'),
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = combination.title_filter_id
                 AND title_filter.filter_type = 'title'
                LEFT JOIN filter_categories AS filter_category ON filter_category.category_id = title_filter.filter_category_id
                WHERE item.category = $galleryCategory
                  AND COALESCE(item.archived_flg, 0) = 0
                  AND COALESCE(item.creator, '') IN ({creatorPlaceholders})
                GROUP BY title_filter.filter_id
                ORDER BY COUNT(DISTINCT item.gid) DESC, title_filter.canonical_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < resolvedCreatorNames.Length; index++)
            {
                command.Parameters.AddWithValue($"$creator{index}", resolvedCreatorNames[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var title = reader.GetString(1);
                var categoryName = reader.GetString(2);
                creatorTitles.Add(new GalleryTitleAssignmentOptionDto(
                    reader.GetInt64(0),
                    title,
                    categoryName,
                    reader.GetInt32(3),
                    CreateGalleryTitleSearchText(categoryName, title)));
            }
        }

        var creatorTitleIds = creatorTitles.Select(option => option.Id).ToArray();
        var availableTitles = new List<GalleryTitleAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var excludedClause = creatorTitleIds.Length == 0
                ? string.Empty
                : "AND title_filter.filter_id NOT IN (" + string.Join(", ", creatorTitleIds.Select((_, index) => $"$excludedTitle{index}")) + ")";
            command.CommandText = $"""
                SELECT title_filter.filter_id,
                       title_filter.canonical_name,
                       COALESCE(filter_category.name, '未分類')
                FROM gallery_filters AS title_filter
                JOIN gallery_filter_combinations AS combination
                  ON combination.title_filter_id = title_filter.filter_id
                 AND combination.character_filter_id IS NULL
                 AND combination.use_flg <> 0
                JOIN gallery_filter_section_visibility AS visibility
                  ON visibility.filter_id = title_filter.filter_id
                 AND visibility.gallery_category = $galleryCategory
                 AND visibility.is_visible <> 0
                LEFT JOIN filter_categories AS filter_category ON filter_category.category_id = title_filter.filter_category_id
                WHERE title_filter.filter_type = 'title'
                  {excludedClause}
                GROUP BY title_filter.filter_id
                ORDER BY COALESCE(filter_category.position, 2147483647),
                         COALESCE(filter_category.name, '未分類') COLLATE NOCASE,
                         title_filter.canonical_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < creatorTitleIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$excludedTitle{index}", creatorTitleIds[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var title = reader.GetString(1);
                var categoryName = reader.GetString(2);
                availableTitles.Add(new GalleryTitleAssignmentOptionDto(
                    reader.GetInt64(0),
                    title,
                    categoryName,
                    0,
                    CreateGalleryTitleSearchText(categoryName, title)));
            }
        }

        var commonAssignedTitles = new List<GalleryTitleAssignmentOptionDto>();
        if (targetPaths.Length > 0)
        {
            using var command = connection.CreateCommand();
            var pathPlaceholders = string.Join(", ", targetPaths.Select((_, index) => $"$path{index}"));
            command.CommandText = $"""
                SELECT title_filter.filter_id,
                       title_filter.canonical_name,
                       COALESCE(filter_category.name, '未分類'),
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = combination.title_filter_id
                 AND title_filter.filter_type = 'title'
                LEFT JOIN filter_categories AS filter_category ON filter_category.category_id = title_filter.filter_category_id
                WHERE item.current_path IN ({pathPlaceholders})
                  AND COALESCE(item.archived_flg, 0) = 0
                GROUP BY title_filter.filter_id
                HAVING COUNT(DISTINCT item.gid) = $targetCount
                ORDER BY title_filter.canonical_name COLLATE NOCASE;
                """;
            for (var index = 0; index < targetPaths.Length; index++)
            {
                command.Parameters.AddWithValue($"$path{index}", targetPaths[index]);
            }
            command.Parameters.AddWithValue("$targetCount", targetPaths.Length);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var title = reader.GetString(1);
                var categoryName = reader.GetString(2);
                commonAssignedTitles.Add(new GalleryTitleAssignmentOptionDto(
                    reader.GetInt64(0),
                    title,
                    categoryName,
                    reader.GetInt32(3),
                    CreateGalleryTitleSearchText(categoryName, title)));
            }
        }

        return new GalleryTitleAssignmentOptionsDto(creatorTitles, availableTitles, commonAssignedTitles, resolvedCreatorNames);
    }

    public GalleryCharacterAssignmentOptionsDto ListGalleryCharacterAssignmentOptions(
        string galleryCategory,
        IReadOnlyList<string> creators,
        string sourcePath,
        IReadOnlyList<string> paths)
    {
        galleryCategory = galleryCategory.Trim();
        sourcePath = sourcePath.Trim();
        var targetPaths = paths
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(galleryCategory) || !File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryCharacterAssignmentOptionsDto([], [], [], [], []);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        var creatorNames = creators
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var creatorCommand = connection.CreateCommand())
        {
            creatorCommand.CommandText = "SELECT COALESCE(creator, '') FROM items WHERE current_path = $path AND category = $category;";
            creatorCommand.Parameters.AddWithValue("$path", sourcePath);
            creatorCommand.Parameters.AddWithValue("$category", galleryCategory);
            var creatorName = (creatorCommand.ExecuteScalar() as string)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(creatorName))
            {
                creatorNames.Add(creatorName);
            }
        }

        var resolvedCreatorNames = creatorNames
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (resolvedCreatorNames.Length == 0)
        {
            throw new InvalidOperationException("選択した作品にCreator属性がありません。");
        }

        var sourceTitles = new List<(long Id, string Name)>();
        using (var titleCommand = connection.CreateCommand())
        {
            titleCommand.CommandText = """
                SELECT DISTINCT title_filter.filter_id, title_filter.canonical_name
                FROM items AS item
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = combination.title_filter_id
                 AND title_filter.filter_type = 'title'
                WHERE item.current_path = $path
                  AND item.category = $category
                ORDER BY title_filter.canonical_name COLLATE NOCASE;
                """;
            titleCommand.Parameters.AddWithValue("$path", sourcePath);
            titleCommand.Parameters.AddWithValue("$category", galleryCategory);
            using var reader = titleCommand.ExecuteReader();
            while (reader.Read())
            {
                sourceTitles.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        if (sourceTitles.Count == 0)
        {
            throw new InvalidOperationException("選択した作品にTitle属性がありません。");
        }

        var sourceTitleIds = sourceTitles.Select(title => title.Id).ToArray();
        var creatorTitleCharacters = new List<GalleryCharacterAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var creatorPlaceholders = string.Join(", ", resolvedCreatorNames.Select((_, index) => $"$creator{index}"));
            var titlePlaceholders = string.Join(", ", sourceTitleIds.Select((_, index) => $"$sourceTitle{index}"));
            command.CommandText = $"""
                SELECT character_filter.filter_id,
                       character_filter.canonical_name,
                       title_filter.canonical_name,
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS character_filter
                  ON character_filter.filter_id = combination.character_filter_id
                 AND character_filter.filter_type = 'character'
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = combination.title_filter_id
                 AND title_filter.filter_type = 'title'
                 AND character_filter.parent_filter_id = title_filter.filter_id
                WHERE item.category = $galleryCategory
                  AND COALESCE(item.archived_flg, 0) = 0
                  AND COALESCE(item.creator, '') IN ({creatorPlaceholders})
                  AND title_filter.filter_id IN ({titlePlaceholders})
                GROUP BY character_filter.filter_id, title_filter.filter_id
                ORDER BY COUNT(DISTINCT item.gid) DESC,
                         character_filter.canonical_name COLLATE NOCASE,
                         title_filter.canonical_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < resolvedCreatorNames.Length; index++)
            {
                command.Parameters.AddWithValue($"$creator{index}", resolvedCreatorNames[index]);
            }
            for (var index = 0; index < sourceTitleIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$sourceTitle{index}", sourceTitleIds[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var character = reader.GetString(1);
                var title = reader.GetString(2);
                creatorTitleCharacters.Add(new GalleryCharacterAssignmentOptionDto(
                    reader.GetInt64(0),
                    character,
                    title,
                    reader.GetInt32(3),
                    CreateGalleryCharacterSearchText(title, character)));
            }
        }

        var creatorTitleCharacterIds = creatorTitleCharacters.Select(option => option.Id).ToArray();
        var availableCharacters = new List<GalleryCharacterAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var titlePlaceholders = string.Join(", ", sourceTitleIds.Select((_, index) => $"$availableTitle{index}"));
            var excludedClause = creatorTitleCharacterIds.Length == 0
                ? string.Empty
                : "AND character_filter.filter_id NOT IN (" + string.Join(", ", creatorTitleCharacterIds.Select((_, index) => $"$excludedCharacter{index}")) + ")";
            command.CommandText = $"""
                SELECT character_filter.filter_id,
                       character_filter.canonical_name,
                       title_filter.canonical_name
                FROM gallery_filters AS character_filter
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = character_filter.parent_filter_id
                 AND title_filter.filter_type = 'title'
                JOIN gallery_filter_combinations AS combination
                  ON combination.title_filter_id = title_filter.filter_id
                 AND combination.character_filter_id = character_filter.filter_id
                 AND combination.use_flg <> 0
                JOIN gallery_filter_section_visibility AS visibility
                  ON visibility.filter_id = character_filter.filter_id
                 AND visibility.gallery_category = $galleryCategory
                 AND visibility.is_visible <> 0
                WHERE character_filter.filter_type = 'character'
                  AND title_filter.filter_id IN ({titlePlaceholders})
                  {excludedClause}
                GROUP BY character_filter.filter_id, title_filter.filter_id
                ORDER BY character_filter.canonical_name COLLATE NOCASE,
                         title_filter.canonical_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < sourceTitleIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$availableTitle{index}", sourceTitleIds[index]);
            }
            for (var index = 0; index < creatorTitleCharacterIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$excludedCharacter{index}", creatorTitleCharacterIds[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var character = reader.GetString(1);
                var title = reader.GetString(2);
                availableCharacters.Add(new GalleryCharacterAssignmentOptionDto(
                    reader.GetInt64(0),
                    character,
                    title,
                    0,
                    CreateGalleryCharacterSearchText(title, character)));
            }
        }

        var commonAssignedCharacters = new List<GalleryCharacterAssignmentOptionDto>();
        if (targetPaths.Length > 0)
        {
            using var command = connection.CreateCommand();
            var pathPlaceholders = string.Join(", ", targetPaths.Select((_, index) => $"$path{index}"));
            command.CommandText = $"""
                SELECT character_filter.filter_id,
                       character_filter.canonical_name,
                       title_filter.canonical_name,
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS character_filter
                  ON character_filter.filter_id = combination.character_filter_id
                 AND character_filter.filter_type = 'character'
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = combination.title_filter_id
                 AND title_filter.filter_type = 'title'
                WHERE item.current_path IN ({pathPlaceholders})
                  AND COALESCE(item.archived_flg, 0) = 0
                GROUP BY character_filter.filter_id, title_filter.filter_id
                HAVING COUNT(DISTINCT item.gid) = $targetCount
                ORDER BY character_filter.canonical_name COLLATE NOCASE,
                         title_filter.canonical_name COLLATE NOCASE;
                """;
            for (var index = 0; index < targetPaths.Length; index++)
            {
                command.Parameters.AddWithValue($"$path{index}", targetPaths[index]);
            }
            command.Parameters.AddWithValue("$targetCount", targetPaths.Length);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var character = reader.GetString(1);
                var title = reader.GetString(2);
                commonAssignedCharacters.Add(new GalleryCharacterAssignmentOptionDto(
                    reader.GetInt64(0),
                    character,
                    title,
                    reader.GetInt32(3),
                    CreateGalleryCharacterSearchText(title, character)));
            }
        }

        return new GalleryCharacterAssignmentOptionsDto(
            creatorTitleCharacters,
            availableCharacters,
            commonAssignedCharacters,
            resolvedCreatorNames,
            sourceTitles.Select(title => title.Name).ToArray());
    }

    public GalleryCharacterAssignmentOptionsDto ListGalleryCharacterAssignmentOptionsForTitles(
        string galleryCategory,
        IReadOnlyList<string> creators,
        IReadOnlyList<long> titleFilterIds,
        IReadOnlyList<string> paths)
    {
        galleryCategory = galleryCategory.Trim();
        var sourceTitleIds = titleFilterIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var targetPaths = paths
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(galleryCategory) ||
            sourceTitleIds.Length == 0 ||
            !File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryCharacterAssignmentOptionsDto([], [], [], [], []);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        var creatorNames = creators
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetPaths.Length > 0)
        {
            using var creatorCommand = connection.CreateCommand();
            var pathPlaceholders = string.Join(", ", targetPaths.Select((_, index) => $"$creatorPath{index}"));
            creatorCommand.CommandText = $"""
                SELECT COALESCE(creator, '')
                FROM items
                WHERE current_path IN ({pathPlaceholders})
                  AND category = $galleryCategory;
                """;
            creatorCommand.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < targetPaths.Length; index++)
            {
                creatorCommand.Parameters.AddWithValue($"$creatorPath{index}", targetPaths[index]);
            }

            using var creatorReader = creatorCommand.ExecuteReader();
            while (creatorReader.Read())
            {
                var creatorName = creatorReader.GetString(0).Trim();
                if (!string.IsNullOrWhiteSpace(creatorName))
                {
                    creatorNames.Add(creatorName);
                }
            }
        }

        var resolvedCreatorNames = creatorNames
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (resolvedCreatorNames.Length == 0)
        {
            throw new InvalidOperationException("選択した作品にCreator属性がありません。");
        }

        var sourceTitles = new List<(long Id, string Name)>();
        using (var titleCommand = connection.CreateCommand())
        {
            var titlePlaceholders = string.Join(", ", sourceTitleIds.Select((_, index) => $"$title{index}"));
            titleCommand.CommandText = $"""
                SELECT filter_id, canonical_name
                FROM gallery_filters
                WHERE filter_type = 'title'
                  AND filter_id IN ({titlePlaceholders})
                ORDER BY canonical_name COLLATE NOCASE;
                """;
            for (var index = 0; index < sourceTitleIds.Length; index++)
            {
                titleCommand.Parameters.AddWithValue($"$title{index}", sourceTitleIds[index]);
            }

            using var reader = titleCommand.ExecuteReader();
            while (reader.Read())
            {
                sourceTitles.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }

        if (sourceTitles.Count == 0)
        {
            throw new InvalidOperationException("選択したTitle属性が見つかりません。");
        }

        sourceTitleIds = sourceTitles.Select(title => title.Id).ToArray();
        var creatorTitleCharacters = new List<GalleryCharacterAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var creatorPlaceholders = string.Join(", ", resolvedCreatorNames.Select((_, index) => $"$creator{index}"));
            var titlePlaceholders = string.Join(", ", sourceTitleIds.Select((_, index) => $"$sourceTitle{index}"));
            command.CommandText = $"""
                SELECT character_filter.filter_id,
                       character_filter.canonical_name,
                       title_filter.canonical_name,
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = item.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS character_filter
                  ON character_filter.filter_id = combination.character_filter_id
                 AND character_filter.filter_type = 'character'
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = combination.title_filter_id
                 AND title_filter.filter_type = 'title'
                 AND character_filter.parent_filter_id = title_filter.filter_id
                WHERE item.category = $galleryCategory
                  AND COALESCE(item.archived_flg, 0) = 0
                  AND COALESCE(item.creator, '') IN ({creatorPlaceholders})
                  AND title_filter.filter_id IN ({titlePlaceholders})
                GROUP BY character_filter.filter_id, title_filter.filter_id
                ORDER BY COUNT(DISTINCT item.gid) DESC,
                         character_filter.canonical_name COLLATE NOCASE,
                         title_filter.canonical_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < resolvedCreatorNames.Length; index++)
            {
                command.Parameters.AddWithValue($"$creator{index}", resolvedCreatorNames[index]);
            }
            for (var index = 0; index < sourceTitleIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$sourceTitle{index}", sourceTitleIds[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var character = reader.GetString(1);
                var title = reader.GetString(2);
                creatorTitleCharacters.Add(new GalleryCharacterAssignmentOptionDto(
                    reader.GetInt64(0),
                    character,
                    title,
                    reader.GetInt32(3),
                    CreateGalleryCharacterSearchText(title, character)));
            }
        }

        var creatorTitleCharacterIds = creatorTitleCharacters.Select(option => option.Id).ToArray();
        var availableCharacters = new List<GalleryCharacterAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var titlePlaceholders = string.Join(", ", sourceTitleIds.Select((_, index) => $"$availableTitle{index}"));
            var excludedClause = creatorTitleCharacterIds.Length == 0
                ? string.Empty
                : "AND character_filter.filter_id NOT IN (" + string.Join(", ", creatorTitleCharacterIds.Select((_, index) => $"$excludedCharacter{index}")) + ")";
            command.CommandText = $"""
                SELECT character_filter.filter_id,
                       character_filter.canonical_name,
                       title_filter.canonical_name
                FROM gallery_filters AS character_filter
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = character_filter.parent_filter_id
                 AND title_filter.filter_type = 'title'
                JOIN gallery_filter_combinations AS combination
                  ON combination.title_filter_id = title_filter.filter_id
                 AND combination.character_filter_id = character_filter.filter_id
                 AND combination.use_flg <> 0
                JOIN gallery_filter_section_visibility AS visibility
                  ON visibility.filter_id = character_filter.filter_id
                 AND visibility.gallery_category = $galleryCategory
                 AND visibility.is_visible <> 0
                WHERE character_filter.filter_type = 'character'
                  AND title_filter.filter_id IN ({titlePlaceholders})
                  {excludedClause}
                GROUP BY character_filter.filter_id, title_filter.filter_id
                ORDER BY character_filter.canonical_name COLLATE NOCASE,
                         title_filter.canonical_name COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$galleryCategory", galleryCategory);
            for (var index = 0; index < sourceTitleIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$availableTitle{index}", sourceTitleIds[index]);
            }
            for (var index = 0; index < creatorTitleCharacterIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$excludedCharacter{index}", creatorTitleCharacterIds[index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var character = reader.GetString(1);
                var title = reader.GetString(2);
                availableCharacters.Add(new GalleryCharacterAssignmentOptionDto(
                    reader.GetInt64(0),
                    character,
                    title,
                    0,
                    CreateGalleryCharacterSearchText(title, character)));
            }
        }

        return new GalleryCharacterAssignmentOptionsDto(
            creatorTitleCharacters,
            availableCharacters,
            [],
            resolvedCreatorNames,
            sourceTitles.Select(title => title.Name).ToArray());
    }

    public GalleryTagAssignmentOptionsDto ListGalleryTagAssignmentOptions(
        string galleryCategory,
        string sourcePath,
        IReadOnlyList<string> paths)
    {
        galleryCategory = galleryCategory.Trim();
        sourcePath = sourcePath.Trim();
        var targetPaths = paths
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (string.IsNullOrWhiteSpace(galleryCategory) || string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(ExternalGalleryDatabasePath))
        {
            return new GalleryTagAssignmentOptionsDto([], [], [], string.Empty, string.Empty);
        }

        EnsureExternalGalleryGidSchema();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ExternalGalleryDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ConnectionString);
        connection.Open();

        string creator;
        string title;
        using (var source = connection.CreateCommand())
        {
            source.CommandText = "SELECT COALESCE(TRIM(creator), ''), COALESCE(TRIM(title), '') FROM items WHERE current_path = $path AND category = $category;";
            source.Parameters.AddWithValue("$path", sourcePath);
            source.Parameters.AddWithValue("$category", galleryCategory);
            using var reader = source.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("選択した作品がSQLiteDBに見つかりません。");
            }
            creator = reader.GetString(0);
            title = reader.GetString(1);
        }

        var creatorTitleTags = new List<GalleryTagAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT tag.tag_id,
                       tag.tag,
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN item_tags AS tag_map ON tag_map.gid = item.gid
                JOIN tags AS tag ON tag.tag_id = tag_map.tag_id
                WHERE item.category = $category
                  AND COALESCE(item.archived_flg, 0) = 0
                  AND COALESCE(TRIM(item.creator), '') = $creator
                  AND COALESCE(TRIM(item.title), '') = $title
                  AND tag.use_flg <> 0
                GROUP BY tag.tag_id, tag.tag, tag.position
                ORDER BY COUNT(DISTINCT item.gid) DESC,
                         tag.position,
                         tag.tag COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$category", galleryCategory);
            command.Parameters.AddWithValue("$creator", creator);
            command.Parameters.AddWithValue("$title", title);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tag = reader.GetString(1);
                creatorTitleTags.Add(new GalleryTagAssignmentOptionDto(
                    reader.GetInt64(0),
                    tag,
                    reader.GetInt32(2),
                    CreateGalleryTagSearchText(tag)));
            }
        }

        var creatorTitleTagIds = creatorTitleTags.Select(option => option.Id).ToArray();
        var availableTags = new List<GalleryTagAssignmentOptionDto>();
        using (var command = connection.CreateCommand())
        {
            var excludedClause = creatorTitleTagIds.Length == 0
                ? string.Empty
                : "AND tag.tag_id NOT IN (" + string.Join(", ", creatorTitleTagIds.Select((_, index) => $"$excludedTag{index}")) + ")";
            command.CommandText = $"""
                SELECT tag.tag_id,
                       tag.tag
                FROM category_tags AS category_tag
                JOIN tags AS tag ON tag.tag_id = category_tag.tag_id
                WHERE category_tag.category = $category
                  AND tag.use_flg <> 0
                  {excludedClause}
                ORDER BY tag.position,
                         tag.tag COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$category", galleryCategory);
            for (var index = 0; index < creatorTitleTagIds.Length; index++)
            {
                command.Parameters.AddWithValue($"$excludedTag{index}", creatorTitleTagIds[index]);
            }
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tag = reader.GetString(1);
                availableTags.Add(new GalleryTagAssignmentOptionDto(
                    reader.GetInt64(0),
                    tag,
                    0,
                    CreateGalleryTagSearchText(tag)));
            }
        }

        var commonAssignedTags = new List<GalleryTagAssignmentOptionDto>();
        if (targetPaths.Length > 0)
        {
            using var command = connection.CreateCommand();
            var pathPlaceholders = string.Join(", ", targetPaths.Select((_, index) => $"$path{index}"));
            command.CommandText = $"""
                SELECT tag.tag_id,
                       tag.tag,
                       COUNT(DISTINCT item.gid)
                FROM items AS item
                JOIN item_tags AS tag_map ON tag_map.gid = item.gid
                JOIN tags AS tag ON tag.tag_id = tag_map.tag_id
                WHERE item.current_path IN ({pathPlaceholders})
                  AND COALESCE(item.archived_flg, 0) = 0
                  AND tag.use_flg <> 0
                GROUP BY tag.tag_id, tag.tag, tag.position
                HAVING COUNT(DISTINCT item.gid) = $targetCount
                ORDER BY tag.position,
                         tag.tag COLLATE NOCASE;
                """;
            for (var index = 0; index < targetPaths.Length; index++)
            {
                command.Parameters.AddWithValue($"$path{index}", targetPaths[index]);
            }
            command.Parameters.AddWithValue("$targetCount", targetPaths.Length);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var tag = reader.GetString(1);
                commonAssignedTags.Add(new GalleryTagAssignmentOptionDto(
                    reader.GetInt64(0),
                    tag,
                    reader.GetInt32(2),
                    CreateGalleryTagSearchText(tag)));
            }
        }

        return new GalleryTagAssignmentOptionsDto(
            creatorTitleTags,
            availableTags,
            commonAssignedTags,
            creator,
            title);
    }

    public GalleryTagAssignmentResultDto AssignGalleryWorkTags(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> tagIds,
        IReadOnlyList<long>? removeTagIds = null)
    {
        var normalizedTagIds = tagIds.Where(id => id > 0).Distinct().ToArray();
        if (normalizedTagIds.Length == 0)
        {
            throw new ArgumentException("登録するTagを選択してください。", nameof(tagIds));
        }

        return UpdateGalleryWorkTagAssignments(paths, normalizedTagIds, removeTagIds ?? []);
    }

    public GalleryTagAssignmentResultDto RemoveGalleryWorkTags(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> tagIds)
    {
        if (tagIds.Count == 0)
        {
            throw new ArgumentException("解除するTagを選択してください。", nameof(tagIds));
        }

        return UpdateGalleryWorkTagAssignments(paths, [], tagIds);
    }

    private GalleryTagAssignmentResultDto UpdateGalleryWorkTagAssignments(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> tagIds,
        IReadOnlyList<long> removeTagIds)
    {
        var targetPaths = paths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetPaths.Length == 0)
        {
            throw new ArgumentException("Tagを登録する作品を選択してください。", nameof(paths));
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();

        var items = new List<(string Gid, string Category)>();
        foreach (var path in targetPaths)
        {
            using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = "SELECT gid, category FROM items WHERE current_path = $path;";
            find.Parameters.AddWithValue("$path", path);
            using var reader = find.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException("Tagの登録対象がSQLiteDBに見つかりません。");
            }
            items.Add((reader.GetString(0), reader.GetString(1)));
        }

        var addTagIds = tagIds.Where(id => id > 0).Distinct().ToArray();
        foreach (var addTagId in addTagIds)
        {
            using var definition = connection.CreateCommand();
            definition.Transaction = transaction;
            definition.CommandText = "SELECT COUNT(*) FROM tags WHERE tag_id = $tagId AND use_flg <> 0;";
            definition.Parameters.AddWithValue("$tagId", addTagId);
            if (Convert.ToInt64(definition.ExecuteScalar()) == 0)
            {
                throw new InvalidOperationException("登録するTagが見つかりません。");
            }

            foreach (var category in items.Select(item => item.Category).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var mapping = connection.CreateCommand();
                mapping.Transaction = transaction;
                mapping.CommandText = "SELECT COUNT(*) FROM category_tags WHERE category = $category AND tag_id = $tagId;";
                mapping.Parameters.AddWithValue("$category", category);
                mapping.Parameters.AddWithValue("$tagId", addTagId);
                if (Convert.ToInt64(mapping.ExecuteScalar()) == 0)
                {
                    throw new InvalidOperationException($"選択したTagは区分「{category}」に登録できません。");
                }
            }
        }

        var removedCount = 0;
        var removeIds = removeTagIds.Where(id => id > 0).Distinct().ToArray();
        foreach (var item in items)
        {
            foreach (var removeId in removeIds)
            {
                using var remove = connection.CreateCommand();
                remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM item_tags WHERE gid = $gid AND tag_id = $tagId;";
                remove.Parameters.AddWithValue("$gid", item.Gid);
                remove.Parameters.AddWithValue("$tagId", removeId);
                removedCount += remove.ExecuteNonQuery();
            }
        }

        var addedCount = 0;
        var skippedCount = 0;
        foreach (var item in items)
        {
            foreach (var selectedTagId in addTagIds)
            {
                using var add = connection.CreateCommand();
                add.Transaction = transaction;
                add.CommandText = "INSERT OR IGNORE INTO item_tags (gid, tag_id) VALUES ($gid, $tagId);";
                add.Parameters.AddWithValue("$gid", item.Gid);
                add.Parameters.AddWithValue("$tagId", selectedTagId);
                if (add.ExecuteNonQuery() > 0)
                {
                    addedCount++;
                }
                else
                {
                    skippedCount++;
                }
            }
        }

        transaction.Commit();
        return new GalleryTagAssignmentResultDto(addedCount, skippedCount, removedCount);
    }

    public GalleryTitleAssignmentResultDto AssignGalleryWorkTitle(
        IReadOnlyList<string> paths,
        long titleFilterId,
        IReadOnlyList<long>? removeTitleFilterIds = null)
    {
        return UpdateGalleryWorkTitles(paths, titleFilterId, removeTitleFilterIds ?? []);
    }

    public GalleryTitleAssignmentResultDto RemoveGalleryWorkTitles(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> titleFilterIds)
    {
        if (titleFilterIds.Count == 0)
        {
            throw new ArgumentException("解除するTitle属性を選択してください。", nameof(titleFilterIds));
        }

        return UpdateGalleryWorkTitles(paths, null, titleFilterIds);
    }

    private GalleryTitleAssignmentResultDto UpdateGalleryWorkTitles(
        IReadOnlyList<string> paths,
        long? titleFilterId,
        IReadOnlyList<long> removeTitleFilterIds)
    {
        var targetPaths = paths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetPaths.Length == 0)
        {
            throw new ArgumentException("Title属性を登録する作品を選択してください。", nameof(paths));
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();

        long? filterCategoryId = null;
        long? combinationId = null;
        if (titleFilterId is not null)
        {
            using (var title = connection.CreateCommand())
            {
                title.Transaction = transaction;
                title.CommandText = """
                    SELECT filter_category_id
                    FROM gallery_filters
                    WHERE filter_id = $titleId
                      AND filter_type = 'title';
                    """;
                title.Parameters.AddWithValue("$titleId", titleFilterId.Value);
                var result = title.ExecuteScalar();
                if (result is null)
                {
                    throw new InvalidOperationException("登録するTitleが見つかりません。");
                }
                filterCategoryId = result is DBNull ? null : Convert.ToInt64(result);
            }

            combinationId = GetOrCreateGalleryFilterCombinationId(
                connection,
                transaction,
                filterCategoryId,
                titleFilterId.Value,
                null,
                1);
        }
        var gids = new List<string>();
        foreach (var path in targetPaths)
        {
            using var findItem = connection.CreateCommand();
            findItem.Transaction = transaction;
            findItem.CommandText = "SELECT gid FROM items WHERE current_path = $path;";
            findItem.Parameters.AddWithValue("$path", path);
            var result = findItem.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException("Title属性の登録対象がSQLiteDBに見つかりません。");
            }
            gids.Add(result);
        }

        var normalizedRemoveTitleIds = removeTitleFilterIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        var removedCount = 0;
        foreach (var gid in gids)
        {
            foreach (var removeTitleId in normalizedRemoveTitleIds)
            {
                using (var removeCombinations = connection.CreateCommand())
                {
                    removeCombinations.Transaction = transaction;
                    removeCombinations.CommandText = """
                        DELETE FROM gallery_item_filter_combinations
                        WHERE gid = $gid
                          AND combination_id IN (
                              SELECT combination_id
                              FROM gallery_filter_combinations
                              WHERE title_filter_id = $titleId
                          );
                        """;
                    removeCombinations.Parameters.AddWithValue("$gid", gid);
                    removeCombinations.Parameters.AddWithValue("$titleId", removeTitleId);
                    if (removeCombinations.ExecuteNonQuery() > 0)
                    {
                        removedCount++;
                    }
                }

                using var removeLegacy = connection.CreateCommand();
                removeLegacy.Transaction = transaction;
                removeLegacy.CommandText = """
                    DELETE FROM gallery_item_filters
                    WHERE gid = $gid
                      AND (filter_id = $titleId OR filter_id IN (
                          SELECT filter_id
                          FROM gallery_filters
                          WHERE filter_type = 'character'
                            AND parent_filter_id = $titleId
                      ));
                    """;
                removeLegacy.Parameters.AddWithValue("$gid", gid);
                removeLegacy.Parameters.AddWithValue("$titleId", removeTitleId);
                removeLegacy.ExecuteNonQuery();
            }
        }

        var addedCount = 0;
        var skippedCount = 0;
        if (titleFilterId is not null && combinationId is not null)
        {
            foreach (var gid in gids)
            {
                using var alreadyAssigned = connection.CreateCommand();
                alreadyAssigned.Transaction = transaction;
                alreadyAssigned.CommandText = """
                    SELECT COUNT(*)
                    FROM gallery_item_filter_combinations AS combination_map
                    JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id
                    WHERE combination_map.gid = $gid
                      AND combination.title_filter_id = $titleId;
                    """;
                alreadyAssigned.Parameters.AddWithValue("$gid", gid);
                alreadyAssigned.Parameters.AddWithValue("$titleId", titleFilterId.Value);
                if (Convert.ToInt64(alreadyAssigned.ExecuteScalar()) > 0)
                {
                    skippedCount++;
                    continue;
                }

                using (var addLegacy = connection.CreateCommand())
                {
                    addLegacy.Transaction = transaction;
                    addLegacy.CommandText = "INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id) VALUES ($gid, $titleId);";
                    addLegacy.Parameters.AddWithValue("$gid", gid);
                    addLegacy.Parameters.AddWithValue("$titleId", titleFilterId.Value);
                    addLegacy.ExecuteNonQuery();
                }
                using var addCombination = connection.CreateCommand();
                addCombination.Transaction = transaction;
                addCombination.CommandText = "INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id) VALUES ($gid, $combinationId);";
                addCombination.Parameters.AddWithValue("$gid", gid);
                addCombination.Parameters.AddWithValue("$combinationId", combinationId.Value);
                addCombination.ExecuteNonQuery();
                addedCount++;
            }
        }

        transaction.Commit();
        return new GalleryTitleAssignmentResultDto(addedCount, skippedCount, removedCount);
    }

    public GalleryCharacterAssignmentResultDto AssignGalleryWorkCharacters(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> characterFilterIds,
        IReadOnlyList<long>? removeCharacterFilterIds = null)
    {
        var normalizedCharacterFilterIds = characterFilterIds.Where(id => id > 0).Distinct().ToArray();
        if (normalizedCharacterFilterIds.Length == 0)
        {
            throw new ArgumentException("登録するCharacter属性を選択してください。", nameof(characterFilterIds));
        }

        return UpdateGalleryWorkCharacters(paths, normalizedCharacterFilterIds, removeCharacterFilterIds ?? []);
    }

    public GalleryCharacterAssignmentResultDto RemoveGalleryWorkCharacters(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> characterFilterIds)
    {
        if (characterFilterIds.Count == 0)
        {
            throw new ArgumentException("解除するCharacter属性を選択してください。", nameof(characterFilterIds));
        }

        return UpdateGalleryWorkCharacters(paths, [], characterFilterIds);
    }

    private GalleryCharacterAssignmentResultDto UpdateGalleryWorkCharacters(
        IReadOnlyList<string> paths,
        IReadOnlyList<long> characterFilterIds,
        IReadOnlyList<long> removeCharacterFilterIds)
    {
        var targetPaths = paths
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetPaths.Length == 0)
        {
            throw new ArgumentException("Character属性を登録する作品を選択してください。", nameof(paths));
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();

        var characterDefinitions = new Dictionary<long, (long TitleId, long? CategoryId)>();
        var requestedCharacterIds = removeCharacterFilterIds
            .Where(id => id > 0)
            .Concat(characterFilterIds)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        foreach (var requestedCharacterId in requestedCharacterIds)
        {
            using var definition = connection.CreateCommand();
            definition.Transaction = transaction;
            definition.CommandText = """
                SELECT title_filter.filter_id, title_filter.filter_category_id
                FROM gallery_filters AS character_filter
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = character_filter.parent_filter_id
                 AND title_filter.filter_type = 'title'
                WHERE character_filter.filter_id = $characterId
                  AND character_filter.filter_type = 'character';
                """;
            definition.Parameters.AddWithValue("$characterId", requestedCharacterId);
            using var reader = definition.ExecuteReader();
            if (reader.Read())
            {
                characterDefinitions[requestedCharacterId] = (
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1));
            }
        }

        if (characterFilterIds.Any(id => !characterDefinitions.ContainsKey(id)))
        {
            throw new InvalidOperationException("登録するCharacterが見つかりません。");
        }

        var gids = new List<string>();
        foreach (var path in targetPaths)
        {
            using var findItem = connection.CreateCommand();
            findItem.Transaction = transaction;
            findItem.CommandText = "SELECT gid FROM items WHERE current_path = $path;";
            findItem.Parameters.AddWithValue("$path", path);
            var result = findItem.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException("Character属性の登録対象がSQLiteDBに見つかりません。");
            }
            gids.Add(result);
        }

        var removedCount = 0;
        foreach (var gid in gids)
        {
            foreach (var removeCharacterId in removeCharacterFilterIds.Where(id => id > 0).Distinct())
            {
                if (!characterDefinitions.TryGetValue(removeCharacterId, out var removeDefinition))
                {
                    continue;
                }

                var removed = false;
                using (var removeCombination = connection.CreateCommand())
                {
                    removeCombination.Transaction = transaction;
                    removeCombination.CommandText = """
                        DELETE FROM gallery_item_filter_combinations
                        WHERE gid = $gid
                          AND combination_id IN (
                              SELECT combination_id
                              FROM gallery_filter_combinations
                              WHERE character_filter_id = $characterId
                          );
                        """;
                    removeCombination.Parameters.AddWithValue("$gid", gid);
                    removeCombination.Parameters.AddWithValue("$characterId", removeCharacterId);
                    removed = removeCombination.ExecuteNonQuery() > 0;
                }

                using (var removeLegacy = connection.CreateCommand())
                {
                    removeLegacy.Transaction = transaction;
                    removeLegacy.CommandText = "DELETE FROM gallery_item_filters WHERE gid = $gid AND filter_id = $characterId;";
                    removeLegacy.Parameters.AddWithValue("$gid", gid);
                    removeLegacy.Parameters.AddWithValue("$characterId", removeCharacterId);
                    removeLegacy.ExecuteNonQuery();
                }

                if (!removed)
                {
                    continue;
                }

                var baseCombinationId = GetOrCreateGalleryFilterCombinationId(
                    connection,
                    transaction,
                    removeDefinition.CategoryId,
                    removeDefinition.TitleId,
                    null,
                    1);
                using (var preserveLegacyTitle = connection.CreateCommand())
                {
                    preserveLegacyTitle.Transaction = transaction;
                    preserveLegacyTitle.CommandText = "INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id) VALUES ($gid, $titleId);";
                    preserveLegacyTitle.Parameters.AddWithValue("$gid", gid);
                    preserveLegacyTitle.Parameters.AddWithValue("$titleId", removeDefinition.TitleId);
                    preserveLegacyTitle.ExecuteNonQuery();
                }
                using (var preserveTitleCombination = connection.CreateCommand())
                {
                    preserveTitleCombination.Transaction = transaction;
                    preserveTitleCombination.CommandText = "INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id) VALUES ($gid, $combinationId);";
                    preserveTitleCombination.Parameters.AddWithValue("$gid", gid);
                    preserveTitleCombination.Parameters.AddWithValue("$combinationId", baseCombinationId);
                    preserveTitleCombination.ExecuteNonQuery();
                }
                removedCount++;
            }
        }

        var addedCount = 0;
        var skippedCount = 0;
        foreach (var characterFilterId in characterFilterIds)
        {
            var characterDefinition = characterDefinitions[characterFilterId];
            var baseCombinationId = GetOrCreateGalleryFilterCombinationId(
                connection,
                transaction,
                characterDefinition.CategoryId,
                characterDefinition.TitleId,
                null,
                1);
            var characterCombinationId = GetOrCreateGalleryFilterCombinationId(
                connection,
                transaction,
                characterDefinition.CategoryId,
                characterDefinition.TitleId,
                characterFilterId,
                1);

            foreach (var gid in gids)
            {
                using var alreadyAssigned = connection.CreateCommand();
                alreadyAssigned.Transaction = transaction;
                alreadyAssigned.CommandText = """
                    SELECT COUNT(*)
                    FROM gallery_item_filter_combinations AS combination_map
                    JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id
                    WHERE combination_map.gid = $gid
                      AND combination.character_filter_id = $characterId;
                    """;
                alreadyAssigned.Parameters.AddWithValue("$gid", gid);
                alreadyAssigned.Parameters.AddWithValue("$characterId", characterFilterId);
                if (Convert.ToInt64(alreadyAssigned.ExecuteScalar()) > 0)
                {
                    skippedCount++;
                    continue;
                }

                foreach (var filterId in new[] { characterDefinition.TitleId, characterFilterId })
                {
                    using var addLegacy = connection.CreateCommand();
                    addLegacy.Transaction = transaction;
                    addLegacy.CommandText = "INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id) VALUES ($gid, $filterId);";
                    addLegacy.Parameters.AddWithValue("$gid", gid);
                    addLegacy.Parameters.AddWithValue("$filterId", filterId);
                    addLegacy.ExecuteNonQuery();
                }
                foreach (var combinationId in new[] { baseCombinationId, characterCombinationId })
                {
                    using var addCombination = connection.CreateCommand();
                    addCombination.Transaction = transaction;
                    addCombination.CommandText = "INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id) VALUES ($gid, $combinationId);";
                    addCombination.Parameters.AddWithValue("$gid", gid);
                    addCombination.Parameters.AddWithValue("$combinationId", combinationId);
                    addCombination.ExecuteNonQuery();
                }
                addedCount++;
            }
        }

        transaction.Commit();
        return new GalleryCharacterAssignmentResultDto(addedCount, skippedCount, removedCount);
    }

    public int DeleteGalleryWorks(IReadOnlyList<string> paths)
    {
        var targetPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeGalleryTargetPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targetPaths.Length == 0)
        {
            throw new ArgumentException("削除する作品のパスを指定してください。", nameof(paths));
        }

        var deletedCount = 0;
        if (File.Exists(ExternalGalleryDatabasePath))
        {
            EnsureExternalGalleryGidSchema();
            EnsureExternalApplicationDataSchema();
            using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
            using (var foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }
            using var transaction = connection.BeginTransaction();
            var itemPathPredicate = BuildPathDeletionPredicate("current_path", targetPaths.Length);
            var registryPathPredicate = BuildPathDeletionPredicate("source_path", targetPaths.Length);

            using (var collectGids = connection.CreateCommand())
            {
                collectGids.Transaction = transaction;
                collectGids.CommandText = $"""
                    CREATE TEMP TABLE explorer_deleted_gids (
                        gid TEXT PRIMARY KEY
                    ) WITHOUT ROWID;
                    INSERT OR IGNORE INTO explorer_deleted_gids (gid)
                    SELECT gid
                    FROM items
                    WHERE {itemPathPredicate};
                    """;
                AddPathDeletionParameters(collectGids, targetPaths);
                collectGids.ExecuteNonQuery();
            }

            using (var countItems = connection.CreateCommand())
            {
                countItems.Transaction = transaction;
                countItems.CommandText = "SELECT COUNT(*) FROM explorer_deleted_gids;";
                deletedCount = Convert.ToInt32((long)(countItems.ExecuteScalar() ?? 0L));
            }

            using (var deleteRelated = connection.CreateCommand())
            {
                deleteRelated.Transaction = transaction;
                deleteRelated.CommandText = $"""
                    -- Keep every issued GID reserved so a deleted work's number
                    -- cannot be assigned to a different file. Only its stale
                    -- filesystem association is cleared.
                    INSERT OR IGNORE INTO gid_registry (gid, source_path)
                    SELECT gid, '' FROM explorer_deleted_gids;
                    UPDATE gid_registry
                    SET source_path = ''
                    WHERE gid IN (SELECT gid FROM explorer_deleted_gids)
                       OR {registryPathPredicate};

                    DELETE FROM item_events
                    WHERE gid IN (SELECT gid FROM explorer_deleted_gids);
                    DELETE FROM item_tags
                    WHERE gid IN (SELECT gid FROM explorer_deleted_gids);
                    DELETE FROM gallery_item_filters
                    WHERE gid IN (SELECT gid FROM explorer_deleted_gids);
                    DELETE FROM gallery_item_filter_combinations
                    WHERE gid IN (SELECT gid FROM explorer_deleted_gids);
                    DELETE FROM items
                    WHERE gid IN (SELECT gid FROM explorer_deleted_gids);
                    """;
                AddPathDeletionParameters(deleteRelated, targetPaths);
                deleteRelated.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        ClearCachesForDeletedGalleryPaths(targetPaths);
        return deletedCount;
    }

    private void ClearCachesForDeletedGalleryPaths(IReadOnlyList<string> targetPaths)
    {
        var pathPredicate = BuildPathDeletionPredicate("path", targetPaths.Count);
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var clearCache = connection.CreateCommand();
            clearCache.Transaction = transaction;
            clearCache.CommandText = $"""
                DELETE FROM file_metadata
                WHERE {pathPredicate};
                DELETE FROM creator_summary_cache;
                DELETE FROM derived_data_cache;
                """;
            AddPathDeletionParameters(clearCache, targetPaths);
            clearCache.ExecuteNonQuery();
            transaction.Commit();
        }

        lock (_creatorSummaryCacheLock)
        {
            _creatorSummaryMemorySignature = null;
            _creatorSummaryMemorySnapshot = null;
        }
        lock (_derivedDataCacheLock)
        {
            _derivedDataMemoryCache.Clear();
        }
        _galleryWorksCache.Clear();
        _galleryFiltersCache.Clear();
    }

    private void ClearGalleryDerivedCaches()
    {
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM creator_summary_cache;
                DELETE FROM derived_data_cache;
                """;
            command.ExecuteNonQuery();
        }

        lock (_creatorSummaryCacheLock)
        {
            _creatorSummaryMemorySignature = null;
            _creatorSummaryMemorySnapshot = null;
        }
        lock (_derivedDataCacheLock)
        {
            _derivedDataMemoryCache.Clear();
        }
        _galleryWorksCache.Clear();
        _galleryFiltersCache.Clear();
    }

    private static string BuildPathDeletionPredicate(string columnName, int pathCount) =>
        string.Join(
            " OR ",
            Enumerable.Range(0, pathCount)
                .Select(index =>
                    $"({columnName} = $deletePath{index} COLLATE NOCASE OR " +
                    $"instr(lower({columnName}), lower($deleteChild{index})) = 1)"));

    private static void AddPathDeletionParameters(
        SqliteCommand command,
        IReadOnlyList<string> targetPaths)
    {
        for (var index = 0; index < targetPaths.Count; index++)
        {
            command.Parameters.AddWithValue($"$deletePath{index}", targetPaths[index]);
            command.Parameters.AddWithValue(
                $"$deleteChild{index}",
                targetPaths[index] + Path.DirectorySeparatorChar);
        }
    }

    private static void AddGalleryWorkParameters(
        SqliteCommand command,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string? excludedFilter = null)
    {
        command.Parameters.AddWithValue("$category", category);
        if (excludedFilter != "rating")
        {
            for (var index = 0; index < ratings.Count; index++)
            {
                command.Parameters.AddWithValue($"$rating{index}", ratings[index]);
            }
        }

        if (excludedFilter != "tag")
        {
            for (var index = 0; index < tags.Count; index++)
            {
                command.Parameters.AddWithValue($"$tag{index}", tags[index]);
            }
        }

        if (excludedFilter != "creator")
        {
            for (var index = 0; index < creators.Count; index++)
            {
                command.Parameters.AddWithValue($"$creator{index}", creators[index]);
            }
        }
        if (excludedFilter != "title")
        {
            for (var index = 0; index < titles.Count; index++)
            {
                command.Parameters.AddWithValue($"$title{index}", titles[index]);
            }
        }
        if (excludedFilter != "character")
        {
            for (var index = 0; index < characters.Count; index++)
            {
                if (!IsUnassignedCharacterFilter(characters[index]))
                {
                    var value = characters[index];
                    if (!value.StartsWith("filter:", StringComparison.OrdinalIgnoreCase) ||
                        !long.TryParse(value["filter:".Length..], out var filterId))
                    {
                        throw new ArgumentException("Characterフィルタの識別子が不正です。", nameof(characters));
                    }
                    command.Parameters.AddWithValue($"$character{index}", filterId);
                }
            }
        }

        for (var index = 0; index < targetPaths.Count; index++)
        {
            command.Parameters.AddWithValue($"$target{index}", targetPaths[index]);
        }

        for (var index = 0; index < supportedExtensions.Count; index++)
        {
            command.Parameters.AddWithValue($"$extension{index}", "%" + supportedExtensions[index].ToLowerInvariant());
        }
    }

    private static string BuildGalleryWorkWhere(
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string? excludedFilter = null)
    {
        var clauses = new List<string>
        {
            "i.category = $category",
            "COALESCE(i.archived_flg, 0) = 0"
        };
        if (targetPaths.Count > 0)
        {
            clauses.Add("(" + string.Join(" OR ", targetPaths.Select((_, index) =>
                $"i.current_path = $target{index} OR instr(i.current_path, $target{index} || '\\') = 1")) + ")");
        }
        if (supportedExtensions.Count > 0)
        {
            clauses.Add("(" + string.Join(" OR ", supportedExtensions.Select((_, index) =>
                $"LOWER(i.current_path) LIKE $extension{index}")) + ")");
        }
        if (ratings.Count > 0 && excludedFilter != "rating")
        {
            clauses.Add("(" + string.Join(" OR ", ratings.Select((rating, index) =>
                rating >= 6
                    ? $"COALESCE(i.rating, 0) >= $rating{index}"
                    : $"COALESCE(i.rating, 0) = $rating{index}")) + ")");
        }
        if (tags.Count > 0 && excludedFilter != "tag")
        {
            clauses.Add("EXISTS (SELECT 1 FROM item_tags AS tag_map JOIN tags AS tag ON tag.tag_id = tag_map.tag_id WHERE tag_map.gid = i.gid AND tag.tag IN (" + string.Join(", ", tags.Select((_, index) => $"$tag{index}")) + "))");
        }
        if (creators.Count > 0 && excludedFilter != "creator")
        {
            clauses.Add("COALESCE(i.creator, '') IN (" + string.Join(", ", creators.Select((_, index) => $"$creator{index}")) + ")");
        }
        if (titles.Count > 0 && excludedFilter != "title")
        {
            clauses.Add("(" + string.Join(" OR ", titles.Select((title, index) =>
                IsUnassignedTitleFilter(title)
                    ? "NOT EXISTS (SELECT 1 FROM gallery_item_filter_combinations AS combination_map JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id AND combination.use_flg <> 0 WHERE combination_map.gid = i.gid AND combination.title_filter_id IS NOT NULL)"
                    : $"EXISTS (SELECT 1 FROM gallery_item_filter_combinations AS combination_map JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id AND combination.use_flg <> 0 JOIN gallery_filters AS filter ON filter.filter_id = combination.title_filter_id WHERE combination_map.gid = i.gid AND filter.canonical_name = $title{index})")) + ")");
        }
        if (characters.Count > 0 && excludedFilter != "character")
        {
            clauses.Add("(" + string.Join(" OR ", characters.Select((character, index) =>
                IsUnassignedCharacterFilter(character)
                    ? "NOT EXISTS (SELECT 1 FROM gallery_item_filter_combinations AS combination_map JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id AND combination.use_flg <> 0 WHERE combination_map.gid = i.gid AND combination.character_filter_id IS NOT NULL)"
                    : $"EXISTS (SELECT 1 FROM gallery_item_filter_combinations AS combination_map JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id AND combination.use_flg <> 0 WHERE combination_map.gid = i.gid AND combination.character_filter_id = $character{index})")) + ")");
        }

        return string.Join(" AND ", clauses);
    }

    private static string BuildGalleryWorkAttributeSelectExpression(string filterType, string legacyColumn)
    {
        var filterIdColumn = filterType switch
        {
            "title" when legacyColumn == "title" => "title_filter_id",
            "character" when legacyColumn == "character" => "character_filter_id",
            _ => throw new ArgumentOutOfRangeException(nameof(filterType), filterType, "対応していない属性種別です。")
        };
        return $"""
            COALESCE(NULLIF((
                SELECT group_concat(attribute_name, ' / ')
                FROM (
                    SELECT DISTINCT filter.canonical_name AS attribute_name
                    FROM gallery_item_filter_combinations AS combination_map
                    JOIN gallery_filter_combinations AS combination
                      ON combination.combination_id = combination_map.combination_id
                     AND combination.use_flg <> 0
                    JOIN gallery_filters AS filter
                      ON filter.filter_id = combination.{filterIdColumn}
                     AND filter.filter_type = '{filterType}'
                    WHERE combination_map.gid = i.gid
                    ORDER BY filter.canonical_name COLLATE NOCASE
                )
            ), ''), COALESCE(i.{legacyColumn}, ''))
            """;
    }

    private static bool IsUnassignedCharacterFilter(string value) =>
        string.Equals(value, UnassignedFilterValue, StringComparison.Ordinal) ||
        string.Equals(value, LegacyNoCharacterFilterValue, StringComparison.Ordinal);

    private static bool IsUnassignedTitleFilter(string value) =>
        string.Equals(value, UnassignedFilterValue, StringComparison.Ordinal);

    private static string BuildGalleryWorkOrderBy(string sort)
    {
        var clauses = new List<string>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawCriterion in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawCriterion.Split(':', 2, StringSplitOptions.TrimEntries);
            var key = parts[0].ToLowerInvariant() switch
            {
                "updated" => "accessed",
                "name" => "path",
                var value => value
            };
            if (!selectedKeys.Add(key))
            {
                continue;
            }

            var requestedDirection = parts.Length > 1 ? parts[1] : string.Empty;
            switch (key)
            {
                case "rating":
                    clauses.Add("COALESCE(i.rating, 0) DESC");
                    break;
                case "images":
                    clauses.Add($"COALESCE(i.image_count, 0) {(string.Equals(requestedDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC")}");
                    break;
                case "accessed":
                    clauses.Add($"COALESCE(i.last_access_time, '') {(string.Equals(requestedDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC")}");
                    break;
                case "path":
                    clauses.Add($"i.current_path COLLATE NOCASE {(string.Equals(requestedDirection, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC")}");
                    break;
                default:
                    selectedKeys.Remove(key);
                    break;
            }
        }

        clauses.Add("i.gid DESC");
        return string.Join(", ", clauses);
    }

    private static string BuildGalleryFilterOrderBy(
        string filterSort,
        string ratingExpression,
        string filesExpression,
        string nameExpression)
    {
        var clauses = new List<string>();
        var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawCriterion in filterSort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawCriterion.Split(':', 2, StringSplitOptions.TrimEntries);
            var key = parts[0].ToLowerInvariant();
            if (!selectedKeys.Add(key))
            {
                continue;
            }

            var requestedDirection = parts.Length > 1 ? parts[1] : string.Empty;
            switch (key)
            {
                case "rating":
                    clauses.Add($"{ratingExpression} DESC");
                    break;
                case "files":
                    clauses.Add($"{filesExpression} {(string.Equals(requestedDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC")}");
                    break;
                case "name":
                    clauses.Add($"{nameExpression} {(string.Equals(requestedDirection, "desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC")}");
                    break;
                default:
                    selectedKeys.Remove(key);
                    break;
            }
        }

        if (!selectedKeys.Contains("name"))
        {
            clauses.Add($"{nameExpression} ASC");
        }

        return string.Join(", ", clauses);
    }

    private static IReadOnlyList<GalleryFilterOptionDto> ListGalleryFilterOptions(
        SqliteConnection connection,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string excludedFilter,
        string valueExpression,
        string alias,
        string filterSort)
    {
        using var command = connection.CreateCommand();
        var where = BuildGalleryWorkWhere(ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, excludedFilter);
        var orderBy = BuildGalleryFilterOrderBy(
            filterSort,
            "SUM(COALESCE(i.rating, 0))",
            "COUNT(*)",
            $"{valueExpression} COLLATE NOCASE");
        command.CommandText = $"""
            SELECT {valueExpression} AS {alias}, COUNT(*) AS count
            FROM items AS i
            WHERE {where}
              AND TRIM(CAST({valueExpression} AS TEXT)) <> ''
            GROUP BY {valueExpression}
            ORDER BY {orderBy};
            """;
        AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, excludedFilter);

        using var reader = command.ExecuteReader();
        var options = new List<GalleryFilterOptionDto>();
        while (reader.Read())
        {
            options.Add(new GalleryFilterOptionDto(reader.GetValue(0).ToString() ?? string.Empty, reader.GetInt32(1)));
        }

        return options;
    }

    private static IReadOnlyList<GalleryFilterOptionDto> ListGalleryManagedFilterOptions(
        SqliteConnection connection,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string filterType,
        string filterSort)
    {
        using var command = connection.CreateCommand();
        var excludedFilter = filterType;
        var where = BuildGalleryWorkWhere(ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, excludedFilter);
        var orderBy = BuildGalleryFilterOrderBy(
            filterSort,
            "SUM(rating)",
            "COUNT(*)",
            "canonical_name COLLATE NOCASE");
        var filterIdColumn = filterType == "title" ? "combination.title_filter_id" : "combination.character_filter_id";
        var characterPresenceCondition = filterType == "title" ? "1 = 1" : "combination.character_filter_id IS NOT NULL";
        var characterTitleScope = filterType == "character" && titles.Count > 0
            ? "AND filter.parent_filter_id IN (SELECT selected_title.filter_id FROM gallery_filters AS selected_title WHERE selected_title.filter_type = 'title' AND selected_title.canonical_name IN (" + string.Join(", ", titles.Select((_, index) => $"$title{index}")) + "))"
            : string.Empty;
        command.CommandText = $"""
            WITH matched AS (
                SELECT DISTINCT
                       filter.filter_id,
                       filter.filter_type,
                       filter.canonical_name,
                       i.gid,
                       COALESCE(i.rating, 0) AS rating
                FROM items AS i
                JOIN gallery_item_filter_combinations AS combination_map ON combination_map.gid = i.gid
                JOIN gallery_filter_combinations AS combination
                  ON combination.combination_id = combination_map.combination_id
                 AND combination.use_flg <> 0
                JOIN gallery_filters AS filter ON filter.filter_id = {filterIdColumn}
                JOIN gallery_filter_section_visibility AS visibility
                  ON visibility.filter_id = filter.filter_id
                 AND visibility.gallery_category = $category
                 AND visibility.is_visible <> 0
                WHERE {where}
                  AND filter.filter_type = $filterType
                  AND ({characterPresenceCondition})
                  {characterTitleScope}
            )
            SELECT CASE WHEN filter_type = 'character' THEN 'filter:' || filter_id ELSE canonical_name END,
                   canonical_name,
                   COUNT(*) AS count
            FROM matched
            GROUP BY filter_id, filter_type, canonical_name
            ORDER BY {orderBy};
            """;
        command.Parameters.AddWithValue("$filterType", filterType);
        AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, excludedFilter);

        using var reader = command.ExecuteReader();
        var options = new List<GalleryFilterOptionDto>();
        while (reader.Read())
        {
            options.Add(new GalleryFilterOptionDto(reader.GetString(0), reader.GetInt32(2), reader.GetString(1)));
        }

        return options;
    }

    private static IReadOnlyList<GalleryFilterOptionDto> ListGalleryTagOptions(
        SqliteConnection connection,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string filterSort)
    {
        using var command = connection.CreateCommand();
        var where = BuildGalleryWorkWhere(ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "tag");
        var orderBy = BuildGalleryFilterOrderBy(
            filterSort,
            "SUM(COALESCE(i.rating, 0))",
            "COUNT(*)",
            "t.tag COLLATE NOCASE");
        command.CommandText = $"""
            SELECT t.tag, COUNT(*) AS count
            FROM items AS i
            JOIN item_tags AS it ON it.gid = i.gid
            JOIN tags AS t ON t.tag_id = it.tag_id
            WHERE {where}
              AND TRIM(t.tag) <> ''
              AND t.use_flg <> 0
            GROUP BY t.tag
            ORDER BY {orderBy};
            """;
        AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "tag");

        using var reader = command.ExecuteReader();
        var options = new List<GalleryFilterOptionDto>();
        while (reader.Read())
        {
            options.Add(new GalleryFilterOptionDto(reader.GetString(0), reader.GetInt32(1)));
        }

        return options;
    }

    private static IReadOnlyList<GalleryFilterOptionDto> ListGalleryTitleOptions(
        SqliteConnection connection,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string filterSort)
    {
        var options = ListGalleryManagedFilterOptions(
            connection,
            category,
            ratings,
            tags,
            creators,
            titles,
            characters,
            targetPaths,
            supportedExtensions,
            "title",
            filterSort).ToList();

        using var command = connection.CreateCommand();
        var where = BuildGalleryWorkWhere(ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "title");
        command.CommandText = $"SELECT COUNT(*) FROM items AS i WHERE {where} AND NOT EXISTS (SELECT 1 FROM gallery_item_filter_combinations AS combination_map JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id AND combination.use_flg <> 0 WHERE combination_map.gid = i.gid AND combination.title_filter_id IS NOT NULL);";
        AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "title");
        var unassignedCount = Convert.ToInt32((long)(command.ExecuteScalar() ?? 0));
        if (unassignedCount > 0)
        {
            options.Add(new GalleryFilterOptionDto(UnassignedFilterValue, unassignedCount, UnassignedFilterValue));
        }

        return options;
    }

    private static IReadOnlyList<GalleryFilterOptionDto> ListGalleryCharacterOptions(
        SqliteConnection connection,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions,
        string filterSort)
    {
        var options = ListGalleryManagedFilterOptions(
            connection,
            category,
            ratings,
            tags,
            creators,
            titles,
            characters,
            targetPaths,
            supportedExtensions,
            "character",
            filterSort).ToList();

        using var command = connection.CreateCommand();
        var where = BuildGalleryWorkWhere(ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "character");
        command.CommandText = $"SELECT COUNT(*) FROM items AS i WHERE {where} AND NOT EXISTS (SELECT 1 FROM gallery_item_filter_combinations AS combination_map JOIN gallery_filter_combinations AS combination ON combination.combination_id = combination_map.combination_id AND combination.use_flg <> 0 WHERE combination_map.gid = i.gid AND combination.character_filter_id IS NOT NULL);";
        AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "character");
        var noCharacterCount = Convert.ToInt32((long)(command.ExecuteScalar() ?? 0));
        if (noCharacterCount > 0)
        {
            options.Add(new GalleryFilterOptionDto(UnassignedFilterValue, noCharacterCount, UnassignedFilterValue));
        }

        return options;
    }

    private static IReadOnlyList<GalleryFilterOptionDto> ListGalleryRatingOptions(
        SqliteConnection connection,
        string category,
        IReadOnlyList<int> ratings,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> creators,
        IReadOnlyList<string> titles,
        IReadOnlyList<string> characters,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<string> supportedExtensions)
    {
        using var command = connection.CreateCommand();
        var where = BuildGalleryWorkWhere(ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "rating");
        command.CommandText = $"""
            SELECT CASE WHEN COALESCE(i.rating, 0) >= 6 THEN 6 ELSE COALESCE(i.rating, 0) END AS rating_group, COUNT(*) AS count
            FROM items AS i
            WHERE {where}
            GROUP BY rating_group;
            """;
        AddGalleryWorkParameters(command, category, ratings, tags, creators, titles, characters, targetPaths, supportedExtensions, "rating");

        using var reader = command.ExecuteReader();
        var counts = new Dictionary<int, int>();
        while (reader.Read())
        {
            counts[reader.GetInt32(0)] = reader.GetInt32(1);
        }

        return new[] { 6, 5, 4, 3, 2, 1, 0 }
            .Where(rating => counts.GetValueOrDefault(rating) > 0)
            .Select(rating => new GalleryFilterOptionDto(rating.ToString(), counts.GetValueOrDefault(rating)))
            .ToArray();
    }

    public IReadOnlyList<GallerySectionDto> ListGallerySections()
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT section_id, label, position FROM gallery_sections ORDER BY position, label COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        var sections = new List<GallerySectionDto>();
        while (reader.Read())
        {
            sections.Add(new GallerySectionDto(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return sections;
    }

    public string GetDefaultGallerySectionId() =>
        ListGallerySections().FirstOrDefault()?.Id ?? DefaultGallerySections[0].Id;

    public string GetGallerySectionLabel(string sectionId) =>
        ListGallerySections().FirstOrDefault(section =>
            string.Equals(section.Id, sectionId, StringComparison.OrdinalIgnoreCase))?.Label
        ?? sectionId;

    public GallerySectionDto CreateGallerySection(string label)
    {
        var normalizedLabel = NormalizeGallerySectionLabel(label);
        EnsureCreated();

        var section = new GallerySectionDto($"section_{Guid.NewGuid():N}"[..20], normalizedLabel, 0);
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var duplicate = connection.CreateCommand())
            {
                duplicate.Transaction = transaction;
                duplicate.CommandText = "SELECT 1 FROM gallery_sections WHERE label = $label COLLATE NOCASE LIMIT 1;";
                duplicate.Parameters.AddWithValue("$label", normalizedLabel);
                if (duplicate.ExecuteScalar() is not null)
                {
                    throw new ArgumentException("同じ名前の区分が既に存在します。", nameof(label));
                }
            }

            var position = 0;
            using (var nextPosition = connection.CreateCommand())
            {
                nextPosition.Transaction = transaction;
                nextPosition.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM gallery_sections;";
                position = Convert.ToInt32(nextPosition.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            section = section with { Position = position };

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO gallery_sections (section_id, label, position) VALUES ($id, $label, $position);";
                insert.Parameters.AddWithValue("$id", section.Id);
                insert.Parameters.AddWithValue("$label", section.Label);
                insert.Parameters.AddWithValue("$position", section.Position);
                insert.ExecuteNonQuery();
            }
            using (var settings = connection.CreateCommand())
            {
                settings.Transaction = transaction;
                settings.CommandText = """
                    INSERT OR IGNORE INTO gallery_scan_settings (
                        category, supported_extensions, card_aspect, enabled_filters, file_name_lines)
                    VALUES ($category, '', 'portrait', $enabledFilters, 3);
                    """;
                settings.Parameters.AddWithValue("$category", section.Id);
                settings.Parameters.AddWithValue("$enabledFilters", SerializeGalleryEnabledFilters(StandardGalleryFilters));
                settings.ExecuteNonQuery();
            }
            using (var adjustment = connection.CreateCommand())
            {
                adjustment.Transaction = transaction;
                adjustment.CommandText = """
                    INSERT OR IGNORE INTO thumbnail_crop_adjustments (
                        category, horizontal_offset_percent, vertical_offset_percent, scale_percent)
                    VALUES ($category, 0, -10, 100);
                    """;
                adjustment.Parameters.AddWithValue("$category", section.Id);
                adjustment.ExecuteNonQuery();
            }
            ClearGalleryDefinitionCaches(connection, transaction);
            transaction.Commit();
        }

        ClearGalleryDefinitionMemoryCaches();
        PersistApplicationSettings();
        return section;
    }

    public void RenameGallerySection(string sectionId, string label)
    {
        sectionId = sectionId.Trim();
        var normalizedLabel = NormalizeGallerySectionLabel(label);
        EnsureCreated();

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var duplicate = connection.CreateCommand())
            {
                duplicate.Transaction = transaction;
                duplicate.CommandText = """
                    SELECT 1 FROM gallery_sections
                    WHERE label = $label COLLATE NOCASE AND section_id <> $id COLLATE NOCASE
                    LIMIT 1;
                    """;
                duplicate.Parameters.AddWithValue("$label", normalizedLabel);
                duplicate.Parameters.AddWithValue("$id", sectionId);
                if (duplicate.ExecuteScalar() is not null)
                {
                    throw new ArgumentException("同じ名前の区分が既に存在します。", nameof(label));
                }
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE gallery_sections
                SET label = $label, updated_at = CURRENT_TIMESTAMP
                WHERE section_id = $id;
                """;
            update.Parameters.AddWithValue("$label", normalizedLabel);
            update.Parameters.AddWithValue("$id", sectionId);
            if (update.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException("変更する区分が見つかりません。", nameof(sectionId));
            }
            transaction.Commit();
        }
        PersistApplicationSettings();
    }

    public void DeleteGallerySection(string sectionId)
    {
        sectionId = sectionId.Trim();
        EnsureCreated();

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM gallery_sections;";
                if (Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture) <= 1)
                {
                    throw new InvalidOperationException("区分は1件以上必要なため削除できません。");
                }
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM gallery_scan_targets WHERE category = $id;
                DELETE FROM gallery_scan_settings WHERE category = $id;
                DELETE FROM thumbnail_crop_adjustments WHERE category = $id;
                DELETE FROM gallery_sections WHERE section_id = $id;
                """;
            delete.Parameters.AddWithValue("$id", sectionId);
            if (delete.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException("削除する区分が見つかりません。", nameof(sectionId));
            }
            ClearGalleryDefinitionCaches(connection, transaction);
            transaction.Commit();
        }

        ClearGalleryDefinitionMemoryCaches();
        PersistApplicationSettings();
    }

    private static string NormalizeGallerySectionLabel(string? label)
    {
        var normalized = label?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("区分名を入力してください。", nameof(label));
        }
        return normalized[..Math.Min(normalized.Length, 80)];
    }

    private static void ClearGalleryDefinitionCaches(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM creator_summary_cache; DELETE FROM derived_data_cache;";
        clear.ExecuteNonQuery();
    }

    private void ClearGalleryDefinitionMemoryCaches()
    {
        lock (_creatorSummaryCacheLock)
        {
            _creatorSummaryMemorySignature = null;
            _creatorSummaryMemorySnapshot = null;
        }
        lock (_derivedDataCacheLock)
        {
            _derivedDataMemoryCache.Clear();
        }
        _galleryWorksCache.Clear();
        _galleryFiltersCache.Clear();
    }

    public IReadOnlyList<GalleryScanTargetDto> ListGalleryScanTargets(string? category = null)
    {
        EnsureCreated();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(category)
            ? "SELECT category, path, position FROM gallery_scan_targets ORDER BY category, position, path COLLATE NOCASE;"
            : "SELECT category, path, position FROM gallery_scan_targets WHERE category = $category ORDER BY position, path COLLATE NOCASE;";
        if (!string.IsNullOrWhiteSpace(category))
        {
            command.Parameters.AddWithValue("$category", category.Trim());
        }

        using var reader = command.ExecuteReader();
        var targets = new List<GalleryScanTargetDto>();
        while (reader.Read())
        {
            targets.Add(new GalleryScanTargetDto(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return targets;
    }

    public string? FindGalleryCategoryForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalizedPath = NormalizeGalleryTargetPath(path);
        return ListGalleryScanTargets()
            .Select(target => new
            {
                target.Category,
                Path = NormalizeGalleryTargetPath(target.Path)
            })
            .Where(target => IsPathWithinRoot(normalizedPath, target.Path))
            .OrderByDescending(target => target.Path.Length)
            .Select(target => target.Category)
            .FirstOrDefault();
    }

    public IReadOnlyList<string> ListGalleryItemPathsUnderFolders(
        string category,
        IReadOnlyList<string> folders)
    {
        var normalizedFolders = folders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeGalleryTargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFolders.Length == 0)
        {
            return [];
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT current_path
            FROM items
            WHERE category = $category
              AND COALESCE(archived_flg, 0) = 0
              AND ({string.Join(" OR ", normalizedFolders.Select((_, index) =>
                  $"current_path = $folder{index} COLLATE NOCASE OR instr(lower(current_path), lower($child{index})) = 1"))})
            ORDER BY current_path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$category", category.Trim());
        for (var index = 0; index < normalizedFolders.Length; index++)
        {
            command.Parameters.AddWithValue($"$folder{index}", normalizedFolders[index]);
            command.Parameters.AddWithValue($"$child{index}", normalizedFolders[index] + Path.DirectorySeparatorChar);
        }

        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }
        return paths;
    }

    public void SynchronizeGidRegistryPathsUnderFolders(IReadOnlyList<string> folders)
    {
        var normalizedFolders = folders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeGalleryTargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedFolders.Length == 0)
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var folder in normalizedFolders)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE gid_registry
                SET source_path = (
                    SELECT item.current_path
                    FROM items AS item
                    WHERE item.gid = gid_registry.gid)
                WHERE EXISTS (
                    SELECT 1
                    FROM items AS item
                    WHERE item.gid = gid_registry.gid
                      AND (item.current_path = $folder COLLATE NOCASE
                           OR instr(lower(item.current_path), lower($child)) = 1));

                INSERT OR IGNORE INTO gid_registry (gid, source_path)
                SELECT gid, current_path
                FROM items
                WHERE current_path = $folder COLLATE NOCASE
                   OR instr(lower(current_path), lower($child)) = 1;
                """;
            update.Parameters.AddWithValue("$folder", folder);
            update.Parameters.AddWithValue("$child", folder + Path.DirectorySeparatorChar);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void UpdateGalleryPathsAfterDirectoryMove(string oldPath, string newPath)
    {
        var normalizedOldPath = NormalizeGalleryTargetPath(oldPath);
        var normalizedNewPath = NormalizeGalleryTargetPath(newPath);
        if (string.Equals(normalizedOldPath, normalizedNewPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        using (var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath))
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var table in new[] { "items", "gid_registry" })
            {
                var pathColumn = table == "items" ? "current_path" : "source_path";
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE {table}
                    SET {pathColumn} = $newPath || substr({pathColumn}, length($oldPath) + 1)
                    WHERE {pathColumn} = $oldPath COLLATE NOCASE
                       OR instr(lower({pathColumn}), lower($childPrefix)) = 1;
                    """;
                update.Parameters.AddWithValue("$oldPath", normalizedOldPath);
                update.Parameters.AddWithValue("$newPath", normalizedNewPath);
                update.Parameters.AddWithValue("$childPrefix", normalizedOldPath + Path.DirectorySeparatorChar);
                update.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            using var clearMetadata = connection.CreateCommand();
            clearMetadata.Transaction = transaction;
            clearMetadata.CommandText = """
                DELETE FROM file_metadata
                WHERE path = $oldPath COLLATE NOCASE
                   OR instr(lower(path), lower($childPrefix)) = 1;
                DELETE FROM creator_summary_cache;
                DELETE FROM derived_data_cache;
                """;
            clearMetadata.Parameters.AddWithValue("$oldPath", normalizedOldPath);
            clearMetadata.Parameters.AddWithValue("$childPrefix", normalizedOldPath + Path.DirectorySeparatorChar);
            clearMetadata.ExecuteNonQuery();
            transaction.Commit();
        }

        lock (_creatorSummaryCacheLock)
        {
            _creatorSummaryMemorySignature = null;
            _creatorSummaryMemorySnapshot = null;
        }
        lock (_derivedDataCacheLock)
        {
            _derivedDataMemoryCache.Clear();
        }
        _galleryWorksCache.Clear();
        _galleryFiltersCache.Clear();
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public void SaveGalleryScanTargets(string category, IEnumerable<string> paths)
    {
        category = category.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("区分を指定してください。", nameof(category));
        }

        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeGalleryTargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        EnsureCreated();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM gallery_scan_targets WHERE category = $category;";
        clear.Parameters.AddWithValue("$category", category);
        clear.ExecuteNonQuery();

        for (var index = 0; index < normalizedPaths.Length; index++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO gallery_scan_targets (category, path, position) VALUES ($category, $path, $position);";
            insert.Parameters.AddWithValue("$category", category);
            insert.Parameters.AddWithValue("$path", normalizedPaths[index]);
            insert.Parameters.AddWithValue("$position", index);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        PersistApplicationSettings();
    }

    public IReadOnlyList<GalleryScanSettingsDto> ListGalleryScanSettings()
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT category, supported_extensions, card_aspect, enabled_filters, file_name_lines, creator_label, title_label, character_label, tag_label, core_title_label, core_tags_label FROM gallery_scan_settings ORDER BY category;";
        using var reader = command.ExecuteReader();
        var settings = new List<GalleryScanSettingsDto>();
        while (reader.Read())
        {
            settings.Add(new GalleryScanSettingsDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseGalleryEnabledFilters(reader.GetString(3), reader.GetString(0)),
                NormalizeGalleryFileNameLines(reader.GetInt32(4)),
                NormalizeGalleryFilterLabel(reader.GetString(5), "Creator"),
                NormalizeGalleryFilterLabel(reader.GetString(6), "Title"),
                NormalizeGalleryFilterLabel(reader.GetString(7), "Character"),
                NormalizeGalleryFilterLabel(reader.GetString(8), "Tag"),
                NormalizeGalleryFilterLabel(reader.GetString(9), "Core title"),
                NormalizeGalleryFilterLabel(reader.GetString(10), "Core tags")));
        }

        return settings;
    }

    public GalleryScanSettingsDto GetGalleryScanSettings(string category)
    {
        category = category.Trim();
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT supported_extensions, card_aspect, enabled_filters, file_name_lines, creator_label, title_label, character_label, tag_label, core_title_label, core_tags_label FROM gallery_scan_settings WHERE category = $category;";
        command.Parameters.AddWithValue("$category", category);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new GalleryScanSettingsDto(
                category,
                reader.GetString(0),
                reader.GetString(1),
                ParseGalleryEnabledFilters(reader.GetString(2), category),
                NormalizeGalleryFileNameLines(reader.GetInt32(3)),
                NormalizeGalleryFilterLabel(reader.GetString(4), "Creator"),
                NormalizeGalleryFilterLabel(reader.GetString(5), "Title"),
                NormalizeGalleryFilterLabel(reader.GetString(6), "Character"),
                NormalizeGalleryFilterLabel(reader.GetString(7), "Tag"),
                NormalizeGalleryFilterLabel(reader.GetString(8), "Core title"),
                NormalizeGalleryFilterLabel(reader.GetString(9), "Core tags"))
            : new GalleryScanSettingsDto(category, string.Empty, GetDefaultGalleryCardAspect(category), GetDefaultGalleryEnabledFilters(category), 3);
    }

    public void SaveGalleryScanSettings(
        string category,
        string supportedExtensions,
        string cardAspect,
        IReadOnlyList<string>? enabledFilters = null,
        int fileNameLines = 3,
        string creatorLabel = "Creator",
        string titleLabel = "Title",
        string characterLabel = "Character",
        string tagLabel = "Tag",
        string coreTitleLabel = "Core title",
        string coreTagsLabel = "Core tags")
    {
        category = category.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("区分を指定してください。", nameof(category));
        }

        cardAspect = NormalizeGalleryCardAspect(cardAspect);

        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var normalizedFilters = NormalizeGalleryEnabledFilters(enabledFilters, category);
        command.CommandText = """
            INSERT INTO gallery_scan_settings (
                category, supported_extensions, card_aspect, enabled_filters, file_name_lines,
                creator_label, title_label, character_label, tag_label, core_title_label, core_tags_label)
            VALUES (
                $category, $supportedExtensions, $cardAspect, $enabledFilters, $fileNameLines,
                $creatorLabel, $titleLabel, $characterLabel, $tagLabel, $coreTitleLabel, $coreTagsLabel)
            ON CONFLICT(category) DO UPDATE SET
                supported_extensions = excluded.supported_extensions,
                card_aspect = excluded.card_aspect,
                enabled_filters = excluded.enabled_filters,
                file_name_lines = excluded.file_name_lines,
                creator_label = excluded.creator_label,
                title_label = excluded.title_label,
                character_label = excluded.character_label,
                tag_label = excluded.tag_label,
                core_title_label = excluded.core_title_label,
                core_tags_label = excluded.core_tags_label;
            """;
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$supportedExtensions", NormalizeExtensionList(supportedExtensions));
        command.Parameters.AddWithValue("$cardAspect", cardAspect);
        command.Parameters.AddWithValue("$enabledFilters", SerializeGalleryEnabledFilters(normalizedFilters));
        command.Parameters.AddWithValue("$fileNameLines", NormalizeGalleryFileNameLines(fileNameLines));
        command.Parameters.AddWithValue("$creatorLabel", NormalizeGalleryFilterLabel(creatorLabel, "Creator"));
        command.Parameters.AddWithValue("$titleLabel", NormalizeGalleryFilterLabel(titleLabel, "Title"));
        command.Parameters.AddWithValue("$characterLabel", NormalizeGalleryFilterLabel(characterLabel, "Character"));
        command.Parameters.AddWithValue("$tagLabel", NormalizeGalleryFilterLabel(tagLabel, "Tag"));
        command.Parameters.AddWithValue("$coreTitleLabel", NormalizeGalleryFilterLabel(coreTitleLabel, "Core title"));
        command.Parameters.AddWithValue("$coreTagsLabel", NormalizeGalleryFilterLabel(coreTagsLabel, "Core tags"));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    private static IReadOnlyList<string> GetDefaultGalleryEnabledFilters(string category) =>
        string.Equals(category, "av", StringComparison.OrdinalIgnoreCase)
            ? AvGalleryFilters
            : StandardGalleryFilters;

    private static IReadOnlyList<string> ParseGalleryEnabledFilters(string value, string category) =>
        NormalizeGalleryEnabledFilters(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), category);

    private static IReadOnlyList<string> NormalizeGalleryEnabledFilters(IReadOnlyList<string>? filters, string category)
    {
        var allowed = new[] { "rating", "creator", "title", "character", "tag", "core_title", "core_tags" };
        var source = filters is null || filters.Count == 0 ? GetDefaultGalleryEnabledFilters(category) : filters;
        return allowed.Where(filter => source.Any(value => string.Equals(value.Trim(), filter, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    private static string SerializeGalleryEnabledFilters(IReadOnlyList<string> filters) => string.Join(',', filters);

    private static int NormalizeGalleryFileNameLines(int lines) => Math.Clamp(lines, 1, 4);

    private static string NormalizeGalleryFilterLabel(string? value, string fallback)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized[..Math.Min(normalized.Length, 40)];
    }

    public IReadOnlyList<ThumbnailCropAdjustmentDto> ListThumbnailCropAdjustments()
    {
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT category, horizontal_offset_percent, vertical_offset_percent, scale_percent FROM thumbnail_crop_adjustments ORDER BY category;";
        using var reader = command.ExecuteReader();
        var adjustments = new List<ThumbnailCropAdjustmentDto>();
        while (reader.Read())
        {
            adjustments.Add(new ThumbnailCropAdjustmentDto(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
        }

        return adjustments;
    }

    public ThumbnailCropAdjustmentDto GetThumbnailCropAdjustment(string category)
    {
        category = category.Trim();
        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT horizontal_offset_percent, vertical_offset_percent, scale_percent FROM thumbnail_crop_adjustments WHERE category = $category;";
        command.Parameters.AddWithValue("$category", category);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ThumbnailCropAdjustmentDto(category, reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2))
            : new ThumbnailCropAdjustmentDto(category, 0, -10, 100);
    }

    public void SaveThumbnailCropAdjustment(string category, double horizontalOffsetPercent, double verticalOffsetPercent, double scalePercent)
    {
        category = category.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("区分を指定してください。", nameof(category));
        }

        EnsureCreated();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO thumbnail_crop_adjustments (category, horizontal_offset_percent, vertical_offset_percent, scale_percent)
            VALUES ($category, $horizontalOffsetPercent, $verticalOffsetPercent, $scalePercent)
            ON CONFLICT(category) DO UPDATE SET
                horizontal_offset_percent = excluded.horizontal_offset_percent,
                vertical_offset_percent = excluded.vertical_offset_percent,
                scale_percent = excluded.scale_percent;
            """;
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$horizontalOffsetPercent", Math.Clamp(horizontalOffsetPercent, -45, 45));
        command.Parameters.AddWithValue("$verticalOffsetPercent", Math.Clamp(verticalOffsetPercent, -45, 45));
        command.Parameters.AddWithValue("$scalePercent", Math.Clamp(scalePercent, 100, 250));
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    private static string NormalizeGalleryCardAspect(string cardAspect) =>
        string.Equals(cardAspect?.Trim(), "landscape", StringComparison.OrdinalIgnoreCase)
            ? "landscape"
            : "portrait";

    private static string NormalizeGalleryFilterType(string filterType) =>
        string.Equals(filterType?.Trim(), "character", StringComparison.OrdinalIgnoreCase)
            ? "character"
            : string.Equals(filterType?.Trim(), "title", StringComparison.OrdinalIgnoreCase)
                ? "title"
                : throw new ArgumentException("フィルタ種別が不正です。", nameof(filterType));

    private static string NormalizeSearchEngineProvider(string provider) =>
        provider?.Trim().ToLowerInvariant() switch
        {
            "brave" => "brave",
            "gemini" => "gemini",
            _ => "google"
        };

    private static string NormalizeColorTheme(string? theme) =>
        string.Equals(theme?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? "light"
            : "dark";

    private static string NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch
        {
            "en" => "en",
            "zh-cn" => "zh-CN",
            "zh-tw" => "zh-TW",
            _ => "ja"
        };

    private static string NormalizeThemeAccent(string? color, string fallback)
    {
        var normalized = color?.Trim().ToLowerInvariant() ?? string.Empty;
        return Regex.IsMatch(normalized, "^#[0-9a-f]{6}$", RegexOptions.CultureInvariant)
            ? normalized
            : fallback;
    }

    private static string NormalizeGoogleSearchUrlTemplate(string template)
    {
        template = template.Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            return "https://www.google.com/search?q={query}";
        }
        if (!template.Contains("{query}", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Google検索URLには {query} を含めてください。", nameof(template));
        }
        if (!Uri.TryCreate(template.Replace("{query}", "test", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Google検索URLは http または https のURLにしてください。", nameof(template));
        }
        return template;
    }

    private static long? GetExistingFilterCategoryId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string categoryName)
    {
        categoryName = categoryName.Trim();
        if (string.IsNullOrWhiteSpace(categoryName) || string.Equals(categoryName, "未分類", StringComparison.Ordinal))
        {
            return null;
        }
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT category_id FROM filter_categories WHERE name = $name COLLATE NOCASE;";
        select.Parameters.AddWithValue("$name", categoryName);
        var value = select.ExecuteScalar();
        if (value is null)
        {
            throw new ArgumentException("選択したCategoryが登録されていません。先にCategoryを登録してください。", nameof(categoryName));
        }
        return Convert.ToInt64(value);
    }

    private static long GetOrCreateGalleryFilterId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string filterType,
        string canonicalName,
        long? categoryId,
        long? parentTitleId)
    {
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = filterType == "character"
                ? """
                    SELECT filter.filter_id
                    FROM gallery_filters AS filter
                    LEFT JOIN gallery_filter_aliases AS alias ON alias.filter_id = filter.filter_id
                    WHERE filter.filter_type = 'character'
                      AND filter.parent_filter_id = $parentId
                      AND (filter.canonical_name = $name COLLATE NOCASE OR alias.alias = $name COLLATE NOCASE)
                    ORDER BY filter.filter_id
                    LIMIT 1;
                    """
                : """
                    SELECT filter.filter_id
                    FROM gallery_filters AS filter
                    LEFT JOIN gallery_filter_aliases AS alias ON alias.filter_id = filter.filter_id
                    WHERE filter.filter_type = 'title'
                      AND (filter.canonical_name = $name COLLATE NOCASE OR alias.alias = $name COLLATE NOCASE)
                    ORDER BY filter.filter_id
                    LIMIT 1;
                    """;
            select.Parameters.AddWithValue("$type", filterType);
            select.Parameters.AddWithValue("$name", canonicalName);
            if (filterType == "character")
            {
                select.Parameters.AddWithValue("$parentId", (object?)parentTitleId ?? DBNull.Value);
            }
            var existing = select.ExecuteScalar();
            if (existing is not null)
            {
                var existingFilterId = Convert.ToInt64(existing);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE gallery_filters SET filter_category_id = $categoryId, category_id = $categoryId, parent_filter_id = $parentId, updated_at = CURRENT_TIMESTAMP WHERE filter_id = $id;";
                update.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
                update.Parameters.AddWithValue("$parentId", filterType == "character" ? (object?)parentTitleId ?? DBNull.Value : DBNull.Value);
                update.Parameters.AddWithValue("$id", existingFilterId);
                update.ExecuteNonQuery();
                return existingFilterId;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO gallery_filters (filter_type, canonical_name, filter_category_id, category_id, parent_filter_id)
            VALUES ($type, $name, $categoryId, $categoryId, $parentId);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$type", filterType);
        insert.Parameters.AddWithValue("$name", canonicalName);
        insert.Parameters.AddWithValue("$categoryId", (object?)categoryId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$parentId", filterType == "character" ? (object?)parentTitleId ?? DBNull.Value : DBNull.Value);
        var id = Convert.ToInt64(insert.ExecuteScalar());
        using var alias = connection.CreateCommand();
        alias.Transaction = transaction;
        alias.CommandText = "INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias) VALUES ($id, $type, $alias);";
        alias.Parameters.AddWithValue("$id", id);
        alias.Parameters.AddWithValue("$type", filterType);
        alias.Parameters.AddWithValue("$alias", canonicalName);
        alias.ExecuteNonQuery();
        return id;
    }

    private static void SeedScopedCharacterFilters(SqliteConnection connection, SqliteTransaction transaction)
    {
        var pairs = new List<(long ParentTitleId, string CharacterName, long? FilterCategoryId)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT DISTINCT title_filter.filter_id,
                                TRIM(COALESCE(i.character, '')),
                                title_filter.filter_category_id
                FROM items AS i
                JOIN gallery_item_filters AS title_map ON title_map.gid = i.gid
                JOIN gallery_filters AS title_filter
                  ON title_filter.filter_id = title_map.filter_id
                 AND title_filter.filter_type = 'title'
                WHERE TRIM(COALESCE(i.character, '')) <> '';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pairs.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
            }
        }

        foreach (var pair in pairs)
        {
            GetOrCreateGalleryFilterId(connection, transaction, "character", pair.CharacterName, pair.FilterCategoryId, pair.ParentTitleId);
        }

        using var map = connection.CreateCommand();
        map.Transaction = transaction;
        map.CommandText = """
            INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id)
            SELECT i.gid, character_filter.filter_id
            FROM items AS i
            JOIN gallery_item_filters AS title_map ON title_map.gid = i.gid
            JOIN gallery_filters AS title_filter
              ON title_filter.filter_id = title_map.filter_id
             AND title_filter.filter_type = 'title'
            JOIN gallery_filters AS character_filter
              ON character_filter.filter_type = 'character'
             AND character_filter.parent_filter_id = title_filter.filter_id
            JOIN gallery_filter_aliases AS character_alias
              ON character_alias.filter_id = character_filter.filter_id
             AND character_alias.alias = TRIM(COALESCE(i.character, ''))
            WHERE TRIM(COALESCE(i.character, '')) <> '';
            """;
        map.ExecuteNonQuery();
    }

    private static IReadOnlyList<GalleryFilterCategoryDto> ListGalleryFilterCategories(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category.category_id,
                   category.name,
                   category.position,
                   SUM(CASE WHEN filter.filter_type = 'title' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN filter.filter_type = 'character' THEN 1 ELSE 0 END)
            FROM filter_categories AS category
            LEFT JOIN gallery_filters AS filter ON filter.filter_category_id = category.category_id
            GROUP BY category.category_id, category.name, category.position
            ORDER BY category.position, category.name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var categories = new List<GalleryFilterCategoryDto>();
        while (reader.Read())
        {
            categories.Add(new GalleryFilterCategoryDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }
        return categories;
    }

    private static string GetDefaultGalleryCardAspect(string category) =>
        DefaultGalleryScanSettings.FirstOrDefault(setting =>
            string.Equals(setting.Category, category, StringComparison.OrdinalIgnoreCase))?.CardAspect
        ?? "portrait";

    public string GetGalleryDatabasePath()
    {
        return _externalGalleryDatabasePath;
    }

    public GalleryStorageSettingsDto GetGalleryDatabaseSettings()
    {
        var configuredCachePath = _storageSettingsStore.GetConfiguredCacheDatabasePath(_defaultCacheDatabasePath);
        return new GalleryStorageSettingsDto(
            _externalGalleryDatabasePath,
            _databasePath,
            configuredCachePath,
            !string.Equals(_databasePath, configuredCachePath, StringComparison.OrdinalIgnoreCase));
    }

    public GalleryCacheDatabaseMoveResult ConfigureCacheDatabaseLocation(string targetDirectory)
    {
        var requestedDirectory = Path.GetFullPath(targetDirectory.Trim());
        Directory.CreateDirectory(requestedDirectory);
        var destinationPath = Path.Combine(requestedDirectory, Path.GetFileName(_defaultCacheDatabasePath));
        if (string.Equals(_databasePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            _storageSettingsStore.SaveCacheDatabasePath(destinationPath);
            return new GalleryCacheDatabaseMoveResult(destinationPath, RestartRequired: false);
        }

        var alreadyConfiguredPath = _storageSettingsStore.GetConfiguredCacheDatabasePath(_defaultCacheDatabasePath);
        if (string.Equals(alreadyConfiguredPath, destinationPath, StringComparison.OrdinalIgnoreCase) && File.Exists(destinationPath))
        {
            return new GalleryCacheDatabaseMoveResult(destinationPath, RestartRequired: true);
        }
        if (File.Exists(destinationPath))
        {
            throw new IOException("指定した保存先には gallerybrowser.cache.sqlite が既にあります。空のディレクトリを指定してください。");
        }

        var temporaryPath = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            using (var source = OpenConnection())
            using (var destination = OpenStandaloneConnection(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(destination);
                ValidateSqliteDatabase(destination, "作成したキャッシュDB");
            }
            File.Move(temporaryPath, destinationPath);
            _storageSettingsStore.SaveCacheDatabasePath(destinationPath);
            return new GalleryCacheDatabaseMoveResult(destinationPath, RestartRequired: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public void CreateGalleryDatabaseSnapshot(string destinationPath)
    {
        var sourcePath = Path.GetFullPath(_externalGalleryDatabasePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("バックアップ対象のSQLiteDBファイルが見つかりません。", sourcePath);
        }

        var fullDestinationPath = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        if (File.Exists(fullDestinationPath))
        {
            throw new IOException("バックアップ用の一時ファイルが既に存在します。");
        }

        try
        {
            using var source = OpenStandaloneConnection(sourcePath, SqliteOpenMode.ReadOnly);
            using var destination = OpenStandaloneConnection(fullDestinationPath, SqliteOpenMode.ReadWriteCreate);
            source.BackupDatabase(destination);
            ValidateSqliteDatabase(destination, "バックアップスナップショット");
        }
        catch
        {
            if (File.Exists(fullDestinationPath))
            {
                File.Delete(fullDestinationPath);
            }
            throw;
        }
    }

    public GalleryDatabaseMoveResult MoveGalleryDatabase(string targetDirectory)
    {
        var requestedDirectory = Path.GetFullPath(targetDirectory.Trim());
        Directory.CreateDirectory(requestedDirectory);

        var sourcePath = Path.GetFullPath(_externalGalleryDatabasePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("現在のSQLiteDBファイルが見つかりません。", sourcePath);
        }

        var destinationPath = Path.Combine(requestedDirectory, Path.GetFileName(sourcePath));
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return new GalleryDatabaseMoveResult(sourcePath);
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException("移動先には同名のSQLiteDBファイルが既にあります。");
        }

        using (var connection = OpenExternalReadWriteConnection(sourcePath))
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        IOException? lastMoveError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath);
                lastMoveError = null;
                break;
            }
            catch (IOException exception) when (attempt < 5)
            {
                lastMoveError = exception;
                SqliteConnection.ClearAllPools();
                Thread.Sleep(150);
            }
            catch (IOException exception)
            {
                lastMoveError = exception;
            }
        }

        if (lastMoveError is not null)
        {
            throw new IOException("SQLiteDBが他のプロセスにより使用中のため移動できません。GalleryBrowser以外で開いているギャラリー関連ツールを閉じてから、もう一度実行してください。", lastMoveError);
        }

        DeleteSqliteSidecar(sourcePath + "-wal");
        DeleteSqliteSidecar(sourcePath + "-shm");
        SaveConfiguredGalleryDatabasePath(destinationPath);
        _externalGalleryDatabasePath = destinationPath;
        _externalGalleryFilterSeedSignature = null;
        return new GalleryDatabaseMoveResult(destinationPath);
    }

    public GalleryDatabaseMergeResult MergeGalleryDatabase(string selectedDatabasePath, bool selectedDatabaseIsCanonical)
    {
        var currentPath = Path.GetFullPath(_externalGalleryDatabasePath);
        var selectedPath = Path.GetFullPath(selectedDatabasePath.Trim());
        if (!File.Exists(selectedPath))
        {
            throw new FileNotFoundException("結合するSQLiteDBファイルが見つかりません。", selectedPath);
        }
        if (string.Equals(currentPath, selectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("現在使用中の本体DBとは別のSQLiteDBを選択してください。");
        }

        ValidateMergeSourceDatabase(selectedPath);
        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalleryBrowser",
            "DatabaseMergeBackups");
        Directory.CreateDirectory(backupDirectory);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var currentBackupPath = CreateDatabaseMergeBackup(currentPath, backupDirectory, timestamp, "current");
        var selectedBackupPath = CreateDatabaseMergeBackup(selectedPath, backupDirectory, timestamp, "selected");

        var canonicalPath = selectedDatabaseIsCanonical ? selectedPath : currentPath;
        var importedPath = selectedDatabaseIsCanonical ? currentPath : selectedPath;
        var previousPath = _externalGalleryDatabasePath;
        try
        {
            if (selectedDatabaseIsCanonical)
            {
                _externalGalleryDatabasePath = selectedPath;
            }
            ResetExternalGalleryDatabaseCaches();
            EnsureExternalApplicationDataSchema();
            EnsureExternalGalleryGidSchema();

            var beforeItems = CountMergeTableRows(canonicalPath, "items");
            var beforeCreators = CountMergeTableRows(canonicalPath, "creator_tracking");
            var beforeBookmarks = CountMergeTableRows(canonicalPath, "view_bookmarks");
            var beforeNotes = CountMergeTableRows(canonicalPath, "sticky_notes");
            MergeGalleryDatabaseIntoCanonical(canonicalPath, importedPath);

            ResetExternalGalleryDatabaseCaches();
            EnsureExternalApplicationDataSchema();
            EnsureExternalGalleryGidSchema();
            if (selectedDatabaseIsCanonical)
            {
                SaveConfiguredGalleryDatabasePath(selectedPath);
            }

            return new GalleryDatabaseMergeResult(
                canonicalPath,
                importedPath,
                selectedDatabaseIsCanonical ? "selected" : "current",
                Math.Max(0, CountMergeTableRows(canonicalPath, "items") - beforeItems),
                Math.Max(0, CountMergeTableRows(canonicalPath, "creator_tracking") - beforeCreators),
                Math.Max(0, CountMergeTableRows(canonicalPath, "view_bookmarks") - beforeBookmarks),
                Math.Max(0, CountMergeTableRows(canonicalPath, "sticky_notes") - beforeNotes),
                currentBackupPath,
                selectedBackupPath);
        }
        catch
        {
            _externalGalleryDatabasePath = previousPath;
            ResetExternalGalleryDatabaseCaches();
            throw;
        }
    }

    private void ResetExternalGalleryDatabaseCaches()
    {
        _applicationDataSchemaReadyPath = null;
        InvalidateExternalGallerySchemaCache();
        ClearGalleryDefinitionMemoryCaches();
    }

    private static void ValidateMergeSourceDatabase(string databasePath)
    {
        using var connection = OpenStandaloneConnection(databasePath, SqliteOpenMode.ReadOnly);
        ValidateSqliteDatabase(connection, "結合対象のSQLiteDB");
        if (!DatabaseTableExists(connection, "main", "items"))
        {
            throw new InvalidDataException("結合対象はGallery本体DBではありません。itemsテーブルが見つかりません。");
        }
        var itemColumns = ReadDatabaseColumns(connection, "main", "items");
        if (!itemColumns.Contains("gid", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("結合対象のitemsテーブルにgid列がありません。先にGID移行を完了してください。");
        }
    }

    private static string CreateDatabaseMergeBackup(
        string sourcePath,
        string backupDirectory,
        string timestamp,
        string role)
    {
        var safeName = Path.GetFileNameWithoutExtension(sourcePath);
        var destinationPath = Path.Combine(backupDirectory, $"{safeName}_{role}_{timestamp}.sqlite");
        for (var suffix = 1; File.Exists(destinationPath); suffix++)
        {
            destinationPath = Path.Combine(backupDirectory, $"{safeName}_{role}_{timestamp}_{suffix}.sqlite");
        }
        using var source = OpenStandaloneConnection(sourcePath, SqliteOpenMode.ReadOnly);
        using var destination = OpenStandaloneConnection(destinationPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
        ValidateSqliteDatabase(destination, "DB結合前バックアップ");
        return destinationPath;
    }

    private static int CountMergeTableRows(string databasePath, string table)
    {
        using var connection = OpenStandaloneConnection(databasePath, SqliteOpenMode.ReadOnly);
        if (!DatabaseTableExists(connection, "main", table))
        {
            return 0;
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteMergeIdentifier(table)};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void MergeGalleryDatabaseIntoCanonical(string canonicalPath, string importedPath)
    {
        using var connection = OpenStandaloneConnection(canonicalPath, SqliteOpenMode.ReadWrite);
        using (var attach = connection.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $path AS incoming;";
            attach.Parameters.AddWithValue("$path", importedPath);
            attach.ExecuteNonQuery();
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            CopyMergeTable(connection, transaction, "items", ["gid"]);
            MergeGalleryTags(connection, transaction);
            CopyMergeTable(connection, transaction, "item_events", ["gid", "event_type", "detail_json", "created_at"], ["event_id"]);
            CopyMergeTable(connection, transaction, "creator_tracking", ["creator"]);
            CopyMergeTable(connection, transaction, "creator_tracking_exchange_rates", ["rate_date", "base_currency", "quote_currency"]);
            CopyMergeTable(connection, transaction, "creator_tracking_archive_snapshots", ["creator", "category", "snapshot_date"]);
            CopyMergeTable(connection, transaction, "gid_registry", ["gid"]);
            CopyMergeTable(connection, transaction, "view_bookmarks", ["name", "view_type", "state_json", "created_at"], ["bookmark_id"]);
            CopyMergeTable(connection, transaction, "sticky_notes", ["view_type", "context_key", "content", "created_at"], ["note_id"]);
            MergeGalleryFilters(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            using var detach = connection.CreateCommand();
            detach.CommandText = "DETACH DATABASE incoming;";
            detach.ExecuteNonQuery();
        }
    }

    private static void MergeGalleryTags(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!DatabaseTableExists(connection, "incoming", "tags"))
        {
            return;
        }
        var sourceColumns = ReadDatabaseColumns(connection, "incoming", "tags")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var useFlagExpression = sourceColumns.Contains("use_flg") ? "COALESCE(use_flg, 1)" : "1";
        var positionExpression = sourceColumns.Contains("position") ? "COALESCE(position, 0)" : "0";
        ExecuteMergeSql(connection, transaction, $"""
            INSERT OR IGNORE INTO main.tags (tag, use_flg, position)
            SELECT tag, {useFlagExpression}, {positionExpression}
            FROM incoming.tags;
            """);
        if (DatabaseTableExists(connection, "incoming", "item_tags"))
        {
            ExecuteMergeSql(connection, transaction, """
                INSERT OR IGNORE INTO main.item_tags (gid, tag_id)
                SELECT source_map.gid, destination_tag.tag_id
                FROM incoming.item_tags AS source_map
                JOIN incoming.tags AS source_tag ON source_tag.tag_id = source_map.tag_id
                JOIN main.tags AS destination_tag ON destination_tag.tag = source_tag.tag COLLATE NOCASE
                JOIN main.items AS destination_item ON destination_item.gid = source_map.gid;
                """);
        }
        if (DatabaseTableExists(connection, "incoming", "category_tags") &&
            DatabaseTableExists(connection, "main", "category_tags"))
        {
            ExecuteMergeSql(connection, transaction, """
                INSERT OR IGNORE INTO main.category_tags (category, tag_id)
                SELECT source_map.category, destination_tag.tag_id
                FROM incoming.category_tags AS source_map
                JOIN incoming.tags AS source_tag ON source_tag.tag_id = source_map.tag_id
                JOIN main.tags AS destination_tag ON destination_tag.tag = source_tag.tag COLLATE NOCASE;
                """);
        }
    }

    private static void MergeGalleryFilters(SqliteConnection connection, SqliteTransaction transaction)
    {
        var requiredTables = new[] { "filter_categories", "gallery_filters", "gallery_filter_aliases" };
        if (requiredTables.Any(table => !DatabaseTableExists(connection, "incoming", table)))
        {
            return;
        }

        ExecuteMergeSql(connection, transaction, """
            INSERT OR IGNORE INTO main.filter_categories (name, position)
            SELECT name, position FROM incoming.filter_categories;

            DROP TABLE IF EXISTS temp.incoming_filter_map;
            CREATE TEMP TABLE incoming_filter_map (
                source_filter_id INTEGER PRIMARY KEY,
                destination_filter_id INTEGER NOT NULL
            );

            INSERT OR IGNORE INTO main.gallery_filters (
                filter_type, canonical_name, filter_category_id, category_id,
                parent_filter_id, created_at, updated_at)
            SELECT source.filter_type, source.canonical_name,
                   destination_category.category_id, destination_category.category_id,
                   NULL, source.created_at, source.updated_at
            FROM incoming.gallery_filters AS source
            LEFT JOIN incoming.filter_categories AS source_category
              ON source_category.category_id = COALESCE(source.filter_category_id, source.category_id)
            LEFT JOIN main.filter_categories AS destination_category
              ON destination_category.name = source_category.name COLLATE NOCASE
            WHERE source.filter_type = 'title';

            INSERT OR IGNORE INTO temp.incoming_filter_map (source_filter_id, destination_filter_id)
            SELECT source.filter_id, destination.filter_id
            FROM incoming.gallery_filters AS source
            JOIN main.gallery_filters AS destination
              ON destination.filter_type = 'title'
             AND destination.canonical_name = source.canonical_name COLLATE NOCASE
            WHERE source.filter_type = 'title';

            INSERT OR IGNORE INTO main.gallery_filters (
                filter_type, canonical_name, filter_category_id, category_id,
                parent_filter_id, created_at, updated_at)
            SELECT 'character', source.canonical_name,
                   destination_category.category_id, destination_category.category_id,
                   parent_map.destination_filter_id, source.created_at, source.updated_at
            FROM incoming.gallery_filters AS source
            JOIN temp.incoming_filter_map AS parent_map ON parent_map.source_filter_id = source.parent_filter_id
            LEFT JOIN incoming.filter_categories AS source_category
              ON source_category.category_id = COALESCE(source.filter_category_id, source.category_id)
            LEFT JOIN main.filter_categories AS destination_category
              ON destination_category.name = source_category.name COLLATE NOCASE
            WHERE source.filter_type = 'character';

            INSERT OR IGNORE INTO temp.incoming_filter_map (source_filter_id, destination_filter_id)
            SELECT source.filter_id, destination.filter_id
            FROM incoming.gallery_filters AS source
            JOIN temp.incoming_filter_map AS parent_map ON parent_map.source_filter_id = source.parent_filter_id
            JOIN main.gallery_filters AS destination
              ON destination.filter_type = 'character'
             AND destination.parent_filter_id = parent_map.destination_filter_id
             AND destination.canonical_name = source.canonical_name COLLATE NOCASE
            WHERE source.filter_type = 'character';

            INSERT OR IGNORE INTO main.gallery_filter_aliases (filter_id, filter_type, alias)
            SELECT map.destination_filter_id, source.filter_type, source.alias
            FROM incoming.gallery_filter_aliases AS source
            JOIN temp.incoming_filter_map AS map ON map.source_filter_id = source.filter_id;
            """);

        if (DatabaseTableExists(connection, "incoming", "gallery_filter_section_visibility"))
        {
            ExecuteMergeSql(connection, transaction, """
                INSERT OR IGNORE INTO main.gallery_filter_section_visibility (filter_id, gallery_category, is_visible)
                SELECT map.destination_filter_id, source.gallery_category, source.is_visible
                FROM incoming.gallery_filter_section_visibility AS source
                JOIN temp.incoming_filter_map AS map ON map.source_filter_id = source.filter_id;
                """);
        }
        if (DatabaseTableExists(connection, "incoming", "gallery_item_filters"))
        {
            ExecuteMergeSql(connection, transaction, """
                INSERT OR IGNORE INTO main.gallery_item_filters (gid, filter_id)
                SELECT source.gid, map.destination_filter_id
                FROM incoming.gallery_item_filters AS source
                JOIN main.items AS item ON item.gid = source.gid
                JOIN temp.incoming_filter_map AS map ON map.source_filter_id = source.filter_id;
                """);
        }
        MergeGalleryFilterCombinations(connection, transaction);
        CopyMergeTable(connection, transaction, "gallery_filter_metadata", ["metadata_key"]);
    }

    private static void MergeGalleryFilterCombinations(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!DatabaseTableExists(connection, "incoming", "gallery_filter_combinations"))
        {
            return;
        }
        ExecuteMergeSql(connection, transaction, """
            INSERT OR IGNORE INTO main.gallery_filter_combinations (
                filter_category_id, title_filter_id, character_filter_id, use_flg, created_at, updated_at)
            SELECT destination_category.category_id,
                   title_map.destination_filter_id,
                   character_map.destination_filter_id,
                   source.use_flg, source.created_at, source.updated_at
            FROM incoming.gallery_filter_combinations AS source
            JOIN temp.incoming_filter_map AS title_map ON title_map.source_filter_id = source.title_filter_id
            LEFT JOIN temp.incoming_filter_map AS character_map ON character_map.source_filter_id = source.character_filter_id
            LEFT JOIN incoming.filter_categories AS source_category ON source_category.category_id = source.filter_category_id
            LEFT JOIN main.filter_categories AS destination_category
              ON destination_category.name = source_category.name COLLATE NOCASE;

            DROP TABLE IF EXISTS temp.incoming_combination_map;
            CREATE TEMP TABLE incoming_combination_map (
                source_combination_id INTEGER PRIMARY KEY,
                destination_combination_id INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO temp.incoming_combination_map (source_combination_id, destination_combination_id)
            SELECT source.combination_id, destination.combination_id
            FROM incoming.gallery_filter_combinations AS source
            JOIN temp.incoming_filter_map AS title_map ON title_map.source_filter_id = source.title_filter_id
            LEFT JOIN temp.incoming_filter_map AS character_map ON character_map.source_filter_id = source.character_filter_id
            LEFT JOIN incoming.filter_categories AS source_category ON source_category.category_id = source.filter_category_id
            LEFT JOIN main.filter_categories AS destination_category
              ON destination_category.name = source_category.name COLLATE NOCASE
            JOIN main.gallery_filter_combinations AS destination
              ON COALESCE(destination.filter_category_id, -1) = COALESCE(destination_category.category_id, -1)
             AND destination.title_filter_id = title_map.destination_filter_id
             AND COALESCE(destination.character_filter_id, -1) = COALESCE(character_map.destination_filter_id, -1);
            """);
        if (DatabaseTableExists(connection, "incoming", "gallery_item_filter_combinations"))
        {
            ExecuteMergeSql(connection, transaction, """
                INSERT OR IGNORE INTO main.gallery_item_filter_combinations (gid, combination_id)
                SELECT source.gid, map.destination_combination_id
                FROM incoming.gallery_item_filter_combinations AS source
                JOIN main.items AS item ON item.gid = source.gid
                JOIN temp.incoming_combination_map AS map ON map.source_combination_id = source.combination_id;
                """);
        }
    }

    private static void CopyMergeTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        IReadOnlyList<string> identityColumns,
        IReadOnlyList<string>? excludedColumns = null)
    {
        if (!DatabaseTableExists(connection, "main", table) ||
            !DatabaseTableExists(connection, "incoming", table))
        {
            return;
        }
        var excluded = (excludedColumns ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinationColumns = ReadDatabaseColumns(connection, "main", table);
        var sourceColumns = ReadDatabaseColumns(connection, "incoming", table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commonColumns = destinationColumns
            .Where(column => sourceColumns.Contains(column) && !excluded.Contains(column))
            .ToArray();
        if (commonColumns.Length == 0 || identityColumns.Any(column => !commonColumns.Contains(column, StringComparer.OrdinalIgnoreCase)))
        {
            return;
        }

        var columnList = string.Join(", ", commonColumns.Select(QuoteMergeIdentifier));
        var selectList = string.Join(", ", commonColumns.Select(column => $"source.{QuoteMergeIdentifier(column)}"));
        var identityPredicate = string.Join(
            " AND ",
            identityColumns.Select(column => $"destination.{QuoteMergeIdentifier(column)} IS source.{QuoteMergeIdentifier(column)}"));
        ExecuteMergeSql(connection, transaction, $"""
            INSERT OR IGNORE INTO main.{QuoteMergeIdentifier(table)} ({columnList})
            SELECT {selectList}
            FROM incoming.{QuoteMergeIdentifier(table)} AS source
            WHERE NOT EXISTS (
                SELECT 1 FROM main.{QuoteMergeIdentifier(table)} AS destination
                WHERE {identityPredicate}
            );
            """);
    }

    private static bool DatabaseTableExists(SqliteConnection connection, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {QuoteMergeIdentifier(schema)}.sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static IReadOnlyList<string> ReadDatabaseColumns(SqliteConnection connection, string schema, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {QuoteMergeIdentifier(schema)}.table_info({QuoteMergeIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static string QuoteMergeIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static void ExecuteMergeSql(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void MaintainGalleryDatabase()
    {
        var databasePath = _externalGalleryDatabasePath;
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("SQLiteDBファイルが見つかりません。", databasePath);
        }

        using var connection = OpenExternalReadWriteConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM; ANALYZE; PRAGMA optimize;";
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, int> SnapshotGalleryRatings()
    {
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_externalGalleryDatabasePath))
        {
            return ratings;
        }

        using var connection = OpenExternalReadWriteConnection(_externalGalleryDatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_path, COALESCE(rating, 0) FROM items;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ratings[reader.GetString(0)] = reader.GetInt32(1);
        }

        return ratings;
    }

    public void RestoreGalleryRatings(IReadOnlyDictionary<string, int> ratings)
    {
        using var connection = OpenExternalReadWriteConnection(_externalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        using (var resetNewRows = connection.CreateCommand())
        {
            resetNewRows.Transaction = transaction;
            resetNewRows.CommandText = "UPDATE items SET rating = 0;";
            resetNewRows.ExecuteNonQuery();
        }

        using (var restore = connection.CreateCommand())
        {
            restore.Transaction = transaction;
            restore.CommandText = "UPDATE items SET rating = $rating WHERE current_path = $path;";
            var rating = restore.CreateParameter();
            rating.ParameterName = "$rating";
            restore.Parameters.Add(rating);
            var path = restore.CreateParameter();
            path.ParameterName = "$path";
            restore.Parameters.Add(path);
            foreach (var (currentPath, currentRating) in ratings)
            {
                rating.Value = currentRating;
                path.Value = currentPath;
                restore.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    internal GalleryDatabaseScanWriteResult UpsertScannedGalleryItems(
        IReadOnlyList<GalleryScannedItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return new GalleryDatabaseScanWriteResult(0, []);
        }

        EnsureExternalApplicationDataSchema();
        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var errors = new List<string>();
        var writtenCount = 0;
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var savepoint = $"gallery_scan_{index}";
            ExecuteGalleryScanSql(connection, transaction, $"SAVEPOINT {savepoint};");
            try
            {
                UpsertScannedGalleryItem(connection, transaction, item);
                ExecuteGalleryScanSql(connection, transaction, $"RELEASE SAVEPOINT {savepoint};");
                writtenCount++;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ArgumentException)
            {
                ExecuteGalleryScanSql(connection, transaction, $"ROLLBACK TO SAVEPOINT {savepoint}; RELEASE SAVEPOINT {savepoint};");
                errors.Add($"{item.CurrentPath}: {ex.Message}");
            }
        }
        transaction.Commit();
        InvalidateExternalGallerySchemaCache();
        ClearGalleryDefinitionMemoryCaches();
        return new GalleryDatabaseScanWriteResult(writtenCount, errors);
    }

    private static void UpsertScannedGalleryItem(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GalleryScannedItem item)
    {
        string? existingGid = null;
        string existingCreator = string.Empty;
        string existingTitle = string.Empty;
        string existingCharacter = string.Empty;
        var existingRating = 0;
        string? existingMetadataSyncedAt = null;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = """
                SELECT gid, creator, title, character, rating, metadata_synced_at
                FROM items
                WHERE gid = $gid OR current_path = $path COLLATE NOCASE
                ORDER BY CASE WHEN gid = $gid THEN 0 ELSE 1 END
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$gid", item.Gid);
            find.Parameters.AddWithValue("$path", item.CurrentPath);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                existingGid = reader.GetString(0);
                existingCreator = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                existingTitle = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                existingCharacter = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                existingRating = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                existingMetadataSyncedAt = reader.IsDBNull(5) ? null : reader.GetString(5);
            }
        }

        if (existingGid is not null && !string.Equals(existingGid, item.Gid, StringComparison.OrdinalIgnoreCase))
        {
            using var conflict = connection.CreateCommand();
            conflict.Transaction = transaction;
            conflict.CommandText = "SELECT COUNT(*) FROM items WHERE gid = $gid;";
            conflict.Parameters.AddWithValue("$gid", item.Gid);
            if (Convert.ToInt64(conflict.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException($"ファイル名のgid {item.Gid} は別のDBレコードで使用されています。");
            }
            ReassignScannedGalleryItemGid(connection, transaction, existingGid, item.Gid);
            existingGid = item.Gid;
        }

        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (existingGid is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO items (
                    gid, current_path, source_zip_name, media_type, category, top_folder,
                    creator, title, character, rating, image_count, duration_seconds,
                    zip_entry_count, total_uncompressed_size, last_access_time, last_write_time,
                    metadata_dirty, metadata_synced_at, created_at, updated_at)
                VALUES (
                    $gid, $path, $sourceName, $mediaType, $category, $topFolder,
                    $creator, $title, $character, $rating, $imageCount, $duration,
                    $entryCount, $totalSize, $lastAccess, $lastWrite,
                    0, NULL, $now, $now);
                """;
            AddScannedGalleryItemParameters(
                insert,
                item,
                item.Creator,
                item.Title,
                item.Character,
                item.Rating,
                now);
            insert.ExecuteNonQuery();
            ReplaceScannedGalleryItemTags(connection, transaction, item.Gid, item.Tags);
            InsertGalleryScanEvent(connection, transaction, item.Gid, "import", item.CurrentPath, now);
        }
        else
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE items
                SET current_path = $path,
                    source_zip_name = $sourceName,
                    media_type = $mediaType,
                    category = $category,
                    top_folder = $topFolder,
                    creator = $creator,
                    title = $title,
                    character = $character,
                    rating = $rating,
                    image_count = $imageCount,
                    duration_seconds = $duration,
                    zip_entry_count = $entryCount,
                    total_uncompressed_size = $totalSize,
                    last_access_time = $lastAccess,
                    last_write_time = $lastWrite,
                    archived_flg = 0,
                    metadata_dirty = 0,
                    metadata_synced_at = $metadataSyncedAt,
                    updated_at = $now
                WHERE gid = $gid;
                """;
            AddScannedGalleryItemParameters(
                update,
                item,
                string.IsNullOrWhiteSpace(item.Creator) ? existingCreator : item.Creator,
                string.IsNullOrWhiteSpace(existingTitle) ? item.Title : existingTitle,
                existingCharacter,
                Math.Max(existingRating, item.Rating),
                now);
            update.Parameters.AddWithValue("$metadataSyncedAt", (object?)existingMetadataSyncedAt ?? DBNull.Value);
            update.ExecuteNonQuery();
            InsertGalleryScanEvent(connection, transaction, item.Gid, "import_sync", item.CurrentPath, now);
        }

        using var registry = connection.CreateCommand();
        registry.Transaction = transaction;
        registry.CommandText = """
            INSERT INTO gid_registry (gid, source_path)
            VALUES ($gid, $path)
            ON CONFLICT(gid) DO UPDATE SET source_path = excluded.source_path;
            """;
        registry.Parameters.AddWithValue("$gid", item.Gid);
        registry.Parameters.AddWithValue("$path", item.CurrentPath);
        registry.ExecuteNonQuery();
    }

    private static void AddScannedGalleryItemParameters(
        SqliteCommand command,
        GalleryScannedItem item,
        string creator,
        string title,
        string character,
        int rating,
        string now)
    {
        command.Parameters.AddWithValue("$gid", item.Gid);
        command.Parameters.AddWithValue("$path", item.CurrentPath);
        command.Parameters.AddWithValue("$sourceName", item.SourceFileName);
        command.Parameters.AddWithValue("$mediaType", item.MediaType);
        command.Parameters.AddWithValue("$category", item.Category);
        command.Parameters.AddWithValue("$topFolder", item.TopFolder);
        command.Parameters.AddWithValue("$creator", creator);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$character", character);
        command.Parameters.AddWithValue("$rating", rating);
        command.Parameters.AddWithValue("$imageCount", (object?)item.ImageCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)item.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$entryCount", (object?)item.ZipEntryCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$totalSize", (object?)item.TotalUncompressedSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastAccess", item.LastAccessTime);
        command.Parameters.AddWithValue("$lastWrite", item.LastWriteTime);
        command.Parameters.AddWithValue("$now", now);
    }

    private static void ReassignScannedGalleryItemGid(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string oldGid,
        string newGid)
    {
        foreach (var table in new[] { "item_tags", "gallery_item_filters", "gallery_item_filter_combinations" })
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE OR IGNORE {QuoteIdentifier(table)} SET gid = $newGid WHERE gid = $oldGid; DELETE FROM {QuoteIdentifier(table)} WHERE gid = $oldGid;";
            update.Parameters.AddWithValue("$newGid", newGid);
            update.Parameters.AddWithValue("$oldGid", oldGid);
            update.ExecuteNonQuery();
        }
        using (var events = connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText = "UPDATE item_events SET gid = $newGid WHERE gid = $oldGid;";
            events.Parameters.AddWithValue("$newGid", newGid);
            events.Parameters.AddWithValue("$oldGid", oldGid);
            events.ExecuteNonQuery();
        }
        using (var registry = connection.CreateCommand())
        {
            registry.Transaction = transaction;
            registry.CommandText = "DELETE FROM gid_registry WHERE gid IN ($oldGid, $newGid);";
            registry.Parameters.AddWithValue("$oldGid", oldGid);
            registry.Parameters.AddWithValue("$newGid", newGid);
            registry.ExecuteNonQuery();
        }
        using var item = connection.CreateCommand();
        item.Transaction = transaction;
        item.CommandText = "UPDATE items SET gid = $newGid WHERE gid = $oldGid;";
        item.Parameters.AddWithValue("$newGid", newGid);
        item.Parameters.AddWithValue("$oldGid", oldGid);
        item.ExecuteNonQuery();
    }

    private static void ReplaceScannedGalleryItemTags(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string gid,
        IReadOnlyList<string> tags)
    {
        foreach (var tag in tags
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(value => value.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using (var ensure = connection.CreateCommand())
            {
                ensure.Transaction = transaction;
                ensure.CommandText = "INSERT OR IGNORE INTO tags (tag) VALUES ($tag);";
                ensure.Parameters.AddWithValue("$tag", tag);
                ensure.ExecuteNonQuery();
            }
            using var map = connection.CreateCommand();
            map.Transaction = transaction;
            map.CommandText = "INSERT OR IGNORE INTO item_tags (gid, tag_id) SELECT $gid, tag_id FROM tags WHERE tag = $tag COLLATE NOCASE;";
            map.Parameters.AddWithValue("$gid", gid);
            map.Parameters.AddWithValue("$tag", tag);
            map.ExecuteNonQuery();
        }
    }

    private static void InsertGalleryScanEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string gid,
        string eventType,
        string path,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO item_events (gid, event_type, detail_json, created_at) VALUES ($gid, $type, $detail, $now);";
        command.Parameters.AddWithValue("$gid", gid);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new { path }));
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void ExecuteGalleryScanSql(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public GalleryCreatorReassignmentResultDto ReassignGalleryCreatorForFolders(
        IReadOnlyList<string> folders,
        string sourceCreator,
        string targetCreator)
    {
        var roots = folders
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeGalleryTargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var source = sourceCreator.Trim();
        var target = targetCreator.Trim();
        if (roots.Length == 0)
        {
            throw new ArgumentException("作者情報を付け替えるフォルダを選択してください。", nameof(folders));
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("現在のCreatorが指定されていません。", nameof(sourceCreator));
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ArgumentException("正しいCreatorが指定されていません。", nameof(targetCreator));
        }
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return new GalleryCreatorReassignmentResultDto(0, false, false, "現在のCreatorと正しいCreatorが同じため、変更はありません。");
        }

        EnsureExternalGalleryGidSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var predicates = new List<string>();
        for (var index = 0; index < roots.Length; index++)
        {
            predicates.Add($"(current_path = $root{index} OR current_path LIKE $childPath{index} ESCAPE '\\')");
        }

        using var updateItems = connection.CreateCommand();
        updateItems.Transaction = transaction;
        updateItems.CommandText = $"""
            UPDATE items
            SET creator = $targetCreator,
                updated_at = CURRENT_TIMESTAMP
            WHERE COALESCE(creator, '') = $sourceCreator COLLATE NOCASE
              AND ({string.Join(" OR ", predicates)});
            """;
        updateItems.Parameters.AddWithValue("$sourceCreator", source);
        updateItems.Parameters.AddWithValue("$targetCreator", target);
        for (var index = 0; index < roots.Length; index++)
        {
            updateItems.Parameters.AddWithValue($"$root{index}", roots[index]);
            updateItems.Parameters.AddWithValue($"$childPath{index}", EscapeLike(roots[index]) + "\\%");
        }
        var itemCount = updateItems.ExecuteNonQuery();

        var trackingRenamed = false;
        var trackingConflict = false;
        using (var sourceTracking = connection.CreateCommand())
        {
            sourceTracking.Transaction = transaction;
            sourceTracking.CommandText = "SELECT EXISTS(SELECT 1 FROM creator_tracking WHERE creator = $creator COLLATE NOCASE);";
            sourceTracking.Parameters.AddWithValue("$creator", source);
            var hasSourceTracking = Convert.ToInt64(sourceTracking.ExecuteScalar()) != 0;
            if (hasSourceTracking)
            {
                using var targetTracking = connection.CreateCommand();
                targetTracking.Transaction = transaction;
                targetTracking.CommandText = "SELECT EXISTS(SELECT 1 FROM creator_tracking WHERE creator = $creator COLLATE NOCASE);";
                targetTracking.Parameters.AddWithValue("$creator", target);
                var hasTargetTracking = Convert.ToInt64(targetTracking.ExecuteScalar()) != 0;
                if (hasTargetTracking)
                {
                    trackingConflict = true;
                }
                else
                {
                    using var renameTracking = connection.CreateCommand();
                    renameTracking.Transaction = transaction;
                    renameTracking.CommandText = """
                        UPDATE creator_tracking
                        SET creator = $targetCreator,
                            display_name = CASE
                                WHEN TRIM(display_name) = '' OR display_name = $sourceCreator THEN $targetCreator
                                ELSE display_name
                            END,
                            updated_at = CURRENT_TIMESTAMP
                        WHERE creator = $sourceCreator COLLATE NOCASE;
                        """;
                    renameTracking.Parameters.AddWithValue("$sourceCreator", source);
                    renameTracking.Parameters.AddWithValue("$targetCreator", target);
                    trackingRenamed = renameTracking.ExecuteNonQuery() > 0;
                }
            }
        }

        using (var clearCache = connection.CreateCommand())
        {
            clearCache.Transaction = transaction;
            clearCache.CommandText = "DELETE FROM creator_summary_cache; DELETE FROM derived_data_cache;";
            clearCache.ExecuteNonQuery();
        }

        transaction.Commit();
        var message = $"{itemCount:N0}件の作品Creatorを「{source}」から「{target}」へ付け替えました。";
        if (trackingRenamed)
        {
            message += " Creator Trackingも付け替えました。";
        }
        if (trackingConflict)
        {
            message += " 正しいCreatorのCreator Trackingが既にあるため、旧CreatorのCreator Trackingは残しました。";
        }
        return new GalleryCreatorReassignmentResultDto(itemCount, trackingRenamed, trackingConflict, message);
    }

    public void ApplyGalleryTargetCategories(IReadOnlyList<string> categories)
    {
        using var connection = OpenExternalReadWriteConnection(_externalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        foreach (var category in categories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var target in ListGalleryScanTargets(category))
            {
                var root = NormalizeGalleryTargetPath(target.Path);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE items
                    SET category = $category,
                        updated_at = CURRENT_TIMESTAMP
                    WHERE current_path = $root
                       OR current_path LIKE $childPath ESCAPE '\';
                    """;
                update.Parameters.AddWithValue("$category", category);
                update.Parameters.AddWithValue("$root", root);
                update.Parameters.AddWithValue("$childPath", EscapeLike(root) + "\\%");
                update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static string NormalizeGalleryTargetPath(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private string ReadConfiguredGalleryDatabasePath()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT database_path FROM gallery_database_settings WHERE id = 1;";
        var value = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(value)
            ? _defaultExternalGalleryDatabasePath
            : Path.GetFullPath(value);
    }

    private void SaveConfiguredGalleryDatabasePath(string databasePath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO gallery_database_settings (id, database_path)
            VALUES (1, $path)
            ON CONFLICT(id) DO UPDATE SET
                database_path = excluded.database_path,
                updated_at = CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$path", databasePath);
        command.ExecuteNonQuery();
        PersistApplicationSettings();
    }

    private void EnsureExternalGalleryGidSchema()
    {
        lock (_externalGallerySchemaLock)
        {
            if (string.Equals(_externalGallerySchemaReadyPath, ExternalGalleryDatabasePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EnsureExternalGalleryGidSchemaCore();
            _externalGallerySchemaReadyPath = ExternalGalleryDatabasePath;
        }
    }

    public void InvalidateExternalGallerySchemaCache()
    {
        lock (_externalGallerySchemaLock)
        {
            _externalGallerySchemaReadyPath = null;
            _externalGalleryFilterSeedSignature = null;
        }
    }

    private void EnsureExternalGalleryGidSchemaCore()
    {
        if (!File.Exists(ExternalGalleryDatabasePath))
        {
            return;
        }

        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        var hasLegacyGenre = false;
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(items);";
            using var reader = schema.ExecuteReader();
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            if (columns.Contains("gid"))
            {
                EnsureExternalGalleryArchivedFlag(connection, columns);
                EnsureExternalGalleryTagCategories(connection);
                EnsureExternalGalleryFilterSchema(connection);
                return;
            }

            if (!columns.Contains("archive_id"))
            {
                throw new InvalidOperationException("外部Gallery SQLiteDBのitemsテーブルにgidまたはarchive_idがありません。");
            }
            hasLegacyGenre = columns.Contains("genre");
        }

        if (hasLegacyGenre)
        {
            CreateGenreRemovalBackup(connection, ExternalGalleryDatabasePath);
        }

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = transaction;
            migrate.CommandText = """
                DROP VIEW IF EXISTS v_items;

                CREATE TABLE items_gid (
                    gid TEXT PRIMARY KEY,
                    current_path TEXT NOT NULL UNIQUE,
                    source_zip_name TEXT NOT NULL,
                    media_type TEXT NOT NULL DEFAULT 'archive',
                    category TEXT NOT NULL DEFAULT '',
                    top_folder TEXT NOT NULL DEFAULT '',
                    creator TEXT NOT NULL DEFAULT '',
                    title TEXT NOT NULL DEFAULT '',
                    character TEXT NOT NULL DEFAULT '',
                    rating INTEGER NOT NULL DEFAULT 0,
                    image_count INTEGER,
                    duration_seconds INTEGER,
                    zip_entry_count INTEGER,
                    total_uncompressed_size INTEGER,
                    last_access_time TEXT,
                    last_write_time TEXT,
                    archived_flg INTEGER NOT NULL DEFAULT 0 CHECK (archived_flg IN (0, 1)),
                    metadata_dirty INTEGER NOT NULL DEFAULT 0,
                    metadata_synced_at TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO items_gid (
                    gid, current_path, source_zip_name, media_type, category, top_folder,
                    creator, title, character, rating, image_count, duration_seconds,
                    zip_entry_count, total_uncompressed_size, last_access_time, last_write_time,
                    archived_flg, metadata_dirty, metadata_synced_at, created_at, updated_at
                )
                SELECT
                    archive_id, current_path, source_zip_name, media_type, category, top_folder,
                    creator, title, character, rating, image_count, duration_seconds,
                    zip_entry_count, total_uncompressed_size, last_access_time, last_write_time,
                    0, metadata_dirty, metadata_synced_at, created_at, updated_at
                FROM items;

                CREATE TABLE item_tags_gid (
                    gid TEXT NOT NULL REFERENCES items_gid(gid) ON DELETE CASCADE,
                    tag_id INTEGER NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
                    PRIMARY KEY (gid, tag_id)
                );
                INSERT INTO item_tags_gid (gid, tag_id)
                SELECT i.archive_id, it.tag_id
                FROM item_tags it
                JOIN items i ON i.item_id = it.item_id;

                CREATE TABLE item_events_gid (
                    event_id INTEGER PRIMARY KEY,
                    gid TEXT REFERENCES items_gid(gid) ON DELETE SET NULL,
                    event_type TEXT NOT NULL,
                    detail_json TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                INSERT INTO item_events_gid (event_id, gid, event_type, detail_json, created_at)
                SELECT e.event_id, i.archive_id, e.event_type, e.detail_json, e.created_at
                FROM item_events e
                LEFT JOIN items i ON i.item_id = e.item_id;

                DROP TABLE item_tags;
                DROP TABLE item_events;
                DROP TABLE items;
                ALTER TABLE items_gid RENAME TO items;
                ALTER TABLE item_tags_gid RENAME TO item_tags;
                ALTER TABLE item_events_gid RENAME TO item_events;

                CREATE INDEX idx_items_path ON items(current_path);
                CREATE INDEX idx_items_rating ON items(rating);
                CREATE INDEX idx_items_creator ON items(creator);
                CREATE INDEX idx_items_title ON items(title);
                CREATE INDEX idx_items_character ON items(character);
                CREATE INDEX idx_item_tags_tag_id ON item_tags(tag_id);
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
            CreateExternalItemsView(connection);
            EnsureExternalGalleryTagCategories(connection);
            EnsureExternalGalleryFilterSchema(connection);
        }
        finally
        {
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }
    }

    public void SetGalleryWorkArchived(string path, bool archived, string? remotePath = null)
    {
        var normalizedPath = NormalizeGalleryTargetPath(path);
        EnsureExternalGalleryGidSchema();
        EnsureExternalApplicationDataSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        string gid;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT gid FROM items WHERE current_path = $path COLLATE NOCASE;";
            find.Parameters.AddWithValue("$path", normalizedPath);
            gid = find.ExecuteScalar() as string
                ?? throw new InvalidOperationException("アーカイブ対象の作品がGallery用SQLiteDBに見つかりません。");
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE items SET archived_flg = $archived, updated_at = CURRENT_TIMESTAMP WHERE gid = $gid;";
            update.Parameters.AddWithValue("$archived", archived ? 1 : 0);
            update.Parameters.AddWithValue("$gid", gid);
            update.ExecuteNonQuery();
        }

        using (var addEvent = connection.CreateCommand())
        {
            addEvent.Transaction = transaction;
            addEvent.CommandText = "INSERT INTO item_events (gid, event_type, detail_json, created_at) VALUES ($gid, $eventType, $detail, $createdAt);";
            addEvent.Parameters.AddWithValue("$gid", gid);
            addEvent.Parameters.AddWithValue("$eventType", archived ? "pcloud_archive" : "pcloud_archive_rollback");
            addEvent.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new
            {
                localPath = normalizedPath,
                remotePath = remotePath ?? string.Empty,
                archivedAt = DateTimeOffset.UtcNow
            }, JsonOptions));
            addEvent.Parameters.AddWithValue(
                "$createdAt",
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            addEvent.ExecuteNonQuery();
        }
        transaction.Commit();
        ClearGalleryDerivedCaches();
    }

    public IReadOnlyList<string> SetGalleryWorksArchivedUnderPaths(
        IReadOnlyList<string> paths,
        bool archived,
        string? remotePath = null)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeGalleryTargetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return [];
        }

        EnsureExternalGalleryGidSchema();
        EnsureExternalApplicationDataSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        var rows = new List<(string Gid, string Path)>();
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = $"""
                SELECT gid, current_path
                FROM items
                WHERE ({BuildPathDeletionPredicate("current_path", normalizedPaths.Length)})
                  AND COALESCE(archived_flg, 0) <> $archived;
                """;
            AddPathDeletionParameters(find, normalizedPaths);
            find.Parameters.AddWithValue("$archived", archived ? 1 : 0);
            using var reader = find.ExecuteReader();
            while (reader.Read())
            {
                var itemPath = reader.GetString(1);
                if (!archived || File.Exists(itemPath) || Directory.Exists(itemPath))
                {
                    rows.Add((reader.GetString(0), itemPath));
                }
            }
        }

        var gids = rows
            .Select(row => row.Gid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ApplyGalleryArchiveState(
            connection,
            transaction,
            gids,
            archived,
            new
            {
                localPaths = normalizedPaths,
                remotePath = remotePath ?? string.Empty,
                archivedAt = DateTimeOffset.UtcNow
            });
        transaction.Commit();
        if (gids.Length > 0)
        {
            ClearGalleryDerivedCaches();
        }
        return gids;
    }

    public void SetGalleryWorksArchivedByGids(
        IReadOnlyList<string> gids,
        bool archived,
        string? remotePath = null,
        bool onlyWhenLocalSourceExists = false)
    {
        var normalizedGids = gids
            .Where(gid => !string.IsNullOrWhiteSpace(gid))
            .Select(gid => gid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedGids.Length == 0)
        {
            return;
        }

        EnsureExternalGalleryGidSchema();
        EnsureExternalApplicationDataSchema();
        using var connection = OpenExternalReadWriteConnection(ExternalGalleryDatabasePath);
        using var transaction = connection.BeginTransaction();
        if (onlyWhenLocalSourceExists)
        {
            var requestedGids = normalizedGids.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existingGids = new List<string>();
            using var findExisting = connection.CreateCommand();
            findExisting.Transaction = transaction;
            findExisting.CommandText = "SELECT gid, current_path FROM items WHERE COALESCE(archived_flg, 0) = 1;";
            using var reader = findExisting.ExecuteReader();
            while (reader.Read())
            {
                var gid = reader.GetString(0);
                var itemPath = reader.GetString(1);
                if (requestedGids.Contains(gid) && (File.Exists(itemPath) || Directory.Exists(itemPath)))
                {
                    existingGids.Add(gid);
                }
            }
            normalizedGids = existingGids.ToArray();
            if (normalizedGids.Length == 0)
            {
                transaction.Commit();
                return;
            }
        }
        ApplyGalleryArchiveState(
            connection,
            transaction,
            normalizedGids,
            archived,
            new
            {
                gids = normalizedGids,
                remotePath = remotePath ?? string.Empty,
                archivedAt = DateTimeOffset.UtcNow
            });
        transaction.Commit();
        ClearGalleryDerivedCaches();
    }

    private static void ApplyGalleryArchiveState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> gids,
        bool archived,
        object eventDetail)
    {
        if (gids.Count == 0)
        {
            return;
        }

        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS pcloud_archive_gids (
                    gid TEXT PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM pcloud_archive_gids;
                """;
            create.ExecuteNonQuery();
        }
        foreach (var gid in gids)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO pcloud_archive_gids (gid) VALUES ($gid);";
            insert.Parameters.AddWithValue("$gid", gid);
            insert.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE items
                SET archived_flg = $archived,
                    updated_at = CURRENT_TIMESTAMP
                WHERE gid IN (SELECT gid FROM pcloud_archive_gids);
                """;
            update.Parameters.AddWithValue("$archived", archived ? 1 : 0);
            update.ExecuteNonQuery();
        }

        using var addEvents = connection.CreateCommand();
        addEvents.Transaction = transaction;
        addEvents.CommandText = """
            INSERT INTO item_events (gid, event_type, detail_json, created_at)
            SELECT gid, $eventType, $detail, $createdAt
            FROM pcloud_archive_gids;
            """;
        addEvents.Parameters.AddWithValue("$eventType", archived ? "pcloud_archive" : "pcloud_archive_rollback");
        addEvents.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(eventDetail, JsonOptions));
        addEvents.Parameters.AddWithValue(
            "$createdAt",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        addEvents.ExecuteNonQuery();
    }

    private static void EnsureExternalGalleryArchivedFlag(
        SqliteConnection connection,
        IReadOnlySet<string>? knownColumns = null)
    {
        var columns = knownColumns ?? ReadDatabaseColumns(connection, "main", "items")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("archived_flg"))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "ALTER TABLE items ADD COLUMN archived_flg INTEGER NOT NULL DEFAULT 0 CHECK (archived_flg IN (0, 1));";
                command.ExecuteNonQuery();
            }
            using (var dropView = connection.CreateCommand())
            {
                dropView.CommandText = "DROP VIEW IF EXISTS v_items;";
                dropView.ExecuteNonQuery();
            }
            CreateExternalItemsView(connection);
        }

        using (var index = connection.CreateCommand())
        {
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_items_archived_category ON items(archived_flg, category);";
            index.ExecuteNonQuery();
        }
    }

    private static void EnsureExternalGalleryTagCategories(SqliteConnection connection)
    {
        var tagColumns = ReadColumns(connection, "tags")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!tagColumns.Contains("useflg"))
        {
            using var addUseFlag = connection.CreateCommand();
            addUseFlag.CommandText = "ALTER TABLE tags ADD COLUMN use_flg INTEGER NOT NULL DEFAULT 1 CHECK (use_flg IN (0, 1));";
            addUseFlag.ExecuteNonQuery();
        }
        if (!tagColumns.Contains("position"))
        {
            using var addPosition = connection.CreateCommand();
            addPosition.CommandText = """
                ALTER TABLE tags ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
                WITH ranked AS (
                    SELECT tag_id,
                           ROW_NUMBER() OVER (ORDER BY tag COLLATE NOCASE, tag_id) - 1 AS position
                    FROM tags
                )
                UPDATE tags
                SET position = (SELECT ranked.position FROM ranked WHERE ranked.tag_id = tags.tag_id);
                """;
            addPosition.ExecuteNonQuery();
        }

        var categoryTagsExists = false;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'category_tags';";
            categoryTagsExists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS category_tags (
                category TEXT NOT NULL,
                tag_id INTEGER NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
                PRIMARY KEY (category, tag_id)
            );
            CREATE INDEX IF NOT EXISTS idx_category_tags_tag_id ON category_tags(tag_id);
            """;
        command.ExecuteNonQuery();

        if (!categoryTagsExists)
        {
            using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT OR IGNORE INTO category_tags (category, tag_id)
                SELECT DISTINCT i.category, it.tag_id
                FROM items AS i
                JOIN item_tags AS it ON it.gid = i.gid
                WHERE TRIM(i.category) <> '';
                """;
            seed.ExecuteNonQuery();
        }
    }

    private void EnsureExternalGalleryFilterSchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS filter_categories (
                    category_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    position INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS gallery_filters (
                    filter_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    filter_type TEXT NOT NULL CHECK (filter_type IN ('title', 'character')),
                    canonical_name TEXT NOT NULL COLLATE NOCASE,
                    filter_category_id INTEGER REFERENCES filter_categories(category_id) ON DELETE SET NULL,
                    category_id INTEGER REFERENCES filter_categories(category_id) ON DELETE SET NULL,
                    parent_filter_id INTEGER REFERENCES gallery_filters(filter_id) ON DELETE SET NULL,
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS gallery_filter_aliases (
                    filter_id INTEGER NOT NULL REFERENCES gallery_filters(filter_id) ON DELETE CASCADE,
                    filter_type TEXT NOT NULL CHECK (filter_type IN ('title', 'character')),
                    alias TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (filter_id, alias)
                );

                CREATE TABLE IF NOT EXISTS gallery_filter_section_visibility (
                    filter_id INTEGER NOT NULL REFERENCES gallery_filters(filter_id) ON DELETE CASCADE,
                    gallery_category TEXT NOT NULL,
                    is_visible INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY (filter_id, gallery_category)
                );

                CREATE TABLE IF NOT EXISTS gallery_item_filters (
                    gid TEXT NOT NULL REFERENCES items(gid) ON DELETE CASCADE,
                    filter_id INTEGER NOT NULL REFERENCES gallery_filters(filter_id) ON DELETE CASCADE,
                    PRIMARY KEY (gid, filter_id)
                );

                CREATE TABLE IF NOT EXISTS gallery_item_filter_combinations (
                    gid TEXT NOT NULL REFERENCES items(gid) ON DELETE CASCADE,
                    combination_id INTEGER NOT NULL REFERENCES gallery_filter_combinations(combination_id) ON DELETE CASCADE,
                    PRIMARY KEY (gid, combination_id)
                );

                CREATE TABLE IF NOT EXISTS gallery_filter_metadata (
                    metadata_key TEXT PRIMARY KEY,
                    metadata_value TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_gallery_title_canonical_name
                    ON gallery_filters(canonical_name) WHERE filter_type = 'title';
                CREATE UNIQUE INDEX IF NOT EXISTS ux_gallery_character_parent_canonical_name
                    ON gallery_filters(parent_filter_id, canonical_name) WHERE filter_type = 'character';
                CREATE INDEX IF NOT EXISTS idx_gallery_filters_type_name ON gallery_filters(filter_type, canonical_name);
                CREATE INDEX IF NOT EXISTS idx_gallery_filter_aliases_alias ON gallery_filter_aliases(filter_type, alias);
                CREATE INDEX IF NOT EXISTS idx_gallery_filter_visibility_category ON gallery_filter_section_visibility(gallery_category, filter_id);
                CREATE INDEX IF NOT EXISTS idx_gallery_item_filters_filter ON gallery_item_filters(filter_id, gid);
                CREATE INDEX IF NOT EXISTS idx_gallery_item_filter_combinations_combination ON gallery_item_filter_combinations(combination_id, gid);
                CREATE INDEX IF NOT EXISTS idx_items_gallery_category ON items(category);
                CREATE INDEX IF NOT EXISTS idx_items_gallery_category_creator ON items(category, creator);
                """;
            command.ExecuteNonQuery();
        }
        EnsureExternalGalleryFilterCategoryColumns(connection);
        EnsureExternalGalleryFilterCategoryReferences(connection);
        EnsureExternalGalleryCharacterFilterScope(connection);
        EnsureExternalGalleryFilterCombinationSchema(connection);

        string signature;
        using (var signatureCommand = connection.CreateCommand())
        {
            signatureCommand.CommandText = "SELECT COUNT(*), COALESCE(MAX(updated_at), '') FROM items;";
            using var reader = signatureCommand.ExecuteReader();
            reader.Read();
            signature = $"{reader.GetInt64(0)}:{reader.GetString(1)}";
        }
        if (string.Equals(_externalGalleryFilterSeedSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        using (var storedSignature = connection.CreateCommand())
        {
            storedSignature.CommandText = "SELECT metadata_value FROM gallery_filter_metadata WHERE metadata_key = 'seed_signature';";
            if (string.Equals(storedSignature.ExecuteScalar() as string, signature, StringComparison.Ordinal))
            {
                _externalGalleryFilterSeedSignature = signature;
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        using (var seedTitles = connection.CreateCommand())
        {
            seedTitles.Transaction = transaction;
            seedTitles.CommandText = """
                INSERT OR IGNORE INTO gallery_filters (filter_type, canonical_name)
                SELECT 'title', DISTINCT_TITLE.value
                FROM (SELECT DISTINCT TRIM(COALESCE(title, '')) AS value FROM items) AS DISTINCT_TITLE
                WHERE DISTINCT_TITLE.value <> ''
                  AND NOT EXISTS (
                    SELECT 1 FROM gallery_filter_aliases AS alias
                    WHERE alias.filter_type = 'title' AND alias.alias = DISTINCT_TITLE.value
                  );
                """;
            seedTitles.ExecuteNonQuery();
        }
        using (var seedCanonicalAliases = connection.CreateCommand())
        {
            seedCanonicalAliases.Transaction = transaction;
            seedCanonicalAliases.CommandText = """
                INSERT OR IGNORE INTO gallery_filter_aliases (filter_id, filter_type, alias)
                SELECT filter_id, filter_type, canonical_name
                FROM gallery_filters;
                """;
            seedCanonicalAliases.ExecuteNonQuery();
        }
        using (var mapTitles = connection.CreateCommand())
        {
            mapTitles.Transaction = transaction;
            mapTitles.CommandText = """
                INSERT OR IGNORE INTO gallery_item_filters (gid, filter_id)
                SELECT i.gid, alias.filter_id
                FROM items AS i
                JOIN gallery_filter_aliases AS alias
                  ON alias.filter_type = 'title' AND alias.alias = TRIM(COALESCE(i.title, ''))
                WHERE TRIM(COALESCE(i.title, '')) <> '';
                """;
            mapTitles.ExecuteNonQuery();
        }
        SeedScopedCharacterFilters(connection, transaction);
        using (var seedVisibility = connection.CreateCommand())
        {
            seedVisibility.Transaction = transaction;
            seedVisibility.CommandText = """
                INSERT OR IGNORE INTO gallery_filter_section_visibility (filter_id, gallery_category, is_visible)
                SELECT item_filter.filter_id, i.category, 1
                FROM items AS i
                JOIN gallery_item_filters AS item_filter ON item_filter.gid = i.gid
                WHERE TRIM(COALESCE(i.category, '')) <> '';
                """;
            seedVisibility.ExecuteNonQuery();
        }
        SynchronizeGalleryFilterCombinationRecords(connection, transaction);
        SynchronizeGalleryItemFilterCombinationRecords(connection, transaction);
        transaction.Commit();
        using (var saveSignature = connection.CreateCommand())
        {
            saveSignature.CommandText = """
                INSERT INTO gallery_filter_metadata (metadata_key, metadata_value)
                VALUES ('seed_signature', $signature)
                ON CONFLICT(metadata_key) DO UPDATE SET metadata_value = excluded.metadata_value;
                """;
            saveSignature.Parameters.AddWithValue("$signature", signature);
            saveSignature.ExecuteNonQuery();
        }
        _externalGalleryFilterSeedSignature = signature;
    }

    private static void EnsureExternalGalleryCharacterFilterScope(SqliteConnection connection)
    {
        string? tableSql;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'gallery_filters';";
            tableSql = command.ExecuteScalar() as string;
        }

        if (string.IsNullOrWhiteSpace(tableSql) ||
            !tableSql.Contains("UNIQUE (filter_type, canonical_name)", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The former global Character-name constraint merges unrelated characters. Rebuild the filter tables
        // while retaining Title IDs, then map each Character through the Title assigned to its item.
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = """
                    DROP TABLE IF EXISTS gallery_item_filters_v2;
                    DROP TABLE IF EXISTS gallery_filter_section_visibility_v2;
                    DROP TABLE IF EXISTS gallery_filter_aliases_v2;
                    DROP TABLE IF EXISTS gallery_filters_v2;
                    DROP TABLE IF EXISTS gallery_item_filter_combinations;
                    DROP TABLE IF EXISTS gallery_filter_combinations;
                    DROP TABLE IF EXISTS character_filter_remap;

                    CREATE TABLE gallery_filters_v2 (
                        filter_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        filter_type TEXT NOT NULL CHECK (filter_type IN ('title', 'character')),
                        canonical_name TEXT NOT NULL COLLATE NOCASE,
                        filter_category_id INTEGER REFERENCES filter_categories(category_id) ON DELETE SET NULL,
                        category_id INTEGER REFERENCES filter_categories(category_id) ON DELETE SET NULL,
                        parent_filter_id INTEGER REFERENCES gallery_filters_v2(filter_id) ON DELETE SET NULL,
                        created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                    );
                    CREATE TABLE gallery_filter_aliases_v2 (
                        filter_id INTEGER NOT NULL REFERENCES gallery_filters_v2(filter_id) ON DELETE CASCADE,
                        filter_type TEXT NOT NULL CHECK (filter_type IN ('title', 'character')),
                        alias TEXT NOT NULL COLLATE NOCASE,
                        PRIMARY KEY (filter_id, alias)
                    );
                    CREATE TABLE gallery_filter_section_visibility_v2 (
                        filter_id INTEGER NOT NULL REFERENCES gallery_filters_v2(filter_id) ON DELETE CASCADE,
                        gallery_category TEXT NOT NULL,
                        is_visible INTEGER NOT NULL DEFAULT 1,
                        PRIMARY KEY (filter_id, gallery_category)
                    );
                    CREATE TABLE gallery_item_filters_v2 (
                        gid TEXT NOT NULL REFERENCES items(gid) ON DELETE CASCADE,
                        filter_id INTEGER NOT NULL REFERENCES gallery_filters_v2(filter_id) ON DELETE CASCADE,
                        PRIMARY KEY (gid, filter_id)
                    );
                    """;
                create.ExecuteNonQuery();
            }

            using (var copyTitles = connection.CreateCommand())
            {
                copyTitles.Transaction = transaction;
                copyTitles.CommandText = """
                    INSERT INTO gallery_filters_v2 (filter_id, filter_type, canonical_name, filter_category_id, category_id, parent_filter_id, created_at, updated_at)
                    SELECT filter_id, filter_type, canonical_name, filter_category_id, category_id, NULL, created_at, updated_at
                    FROM gallery_filters
                    WHERE filter_type = 'title';
                    INSERT OR IGNORE INTO gallery_filter_aliases_v2 (filter_id, filter_type, alias)
                    SELECT alias.filter_id, alias.filter_type, alias.alias
                    FROM gallery_filter_aliases AS alias
                    JOIN gallery_filters AS filter ON filter.filter_id = alias.filter_id
                    WHERE filter.filter_type = 'title';
                    INSERT OR IGNORE INTO gallery_item_filters_v2 (gid, filter_id)
                    SELECT item_filter.gid, item_filter.filter_id
                    FROM gallery_item_filters AS item_filter
                    JOIN gallery_filters AS filter ON filter.filter_id = item_filter.filter_id
                    WHERE filter.filter_type = 'title';
                    INSERT OR IGNORE INTO gallery_filter_section_visibility_v2 (filter_id, gallery_category, is_visible)
                    SELECT visibility.filter_id, visibility.gallery_category, visibility.is_visible
                    FROM gallery_filter_section_visibility AS visibility
                    JOIN gallery_filters AS filter ON filter.filter_id = visibility.filter_id
                    WHERE filter.filter_type = 'title';
                    """;
                copyTitles.ExecuteNonQuery();
            }

            using (var copyCharacters = connection.CreateCommand())
            {
                copyCharacters.Transaction = transaction;
                copyCharacters.CommandText = """
                    INSERT INTO gallery_filters_v2 (filter_type, canonical_name, filter_category_id, category_id, parent_filter_id, created_at, updated_at)
                    SELECT 'character', character_filter.canonical_name,
                           character_filter.filter_category_id, character_filter.category_id,
                           title_filter.filter_id, character_filter.created_at, character_filter.updated_at
                    FROM gallery_filters AS character_filter
                    JOIN gallery_item_filters AS character_map ON character_map.filter_id = character_filter.filter_id
                    JOIN gallery_item_filters AS title_map ON title_map.gid = character_map.gid
                    JOIN gallery_filters AS old_title ON old_title.filter_id = title_map.filter_id AND old_title.filter_type = 'title'
                    JOIN gallery_filters_v2 AS title_filter ON title_filter.filter_id = old_title.filter_id
                    WHERE character_filter.filter_type = 'character'
                    GROUP BY character_filter.filter_id, title_filter.filter_id;

                    CREATE TEMP TABLE character_filter_remap AS
                    SELECT DISTINCT character_filter.filter_id AS old_filter_id,
                                    title_filter.filter_id AS parent_title_id,
                                    replacement.filter_id AS new_filter_id
                    FROM gallery_filters AS character_filter
                    JOIN gallery_item_filters AS character_map ON character_map.filter_id = character_filter.filter_id
                    JOIN gallery_item_filters AS title_map ON title_map.gid = character_map.gid
                    JOIN gallery_filters AS old_title ON old_title.filter_id = title_map.filter_id AND old_title.filter_type = 'title'
                    JOIN gallery_filters_v2 AS title_filter ON title_filter.filter_id = old_title.filter_id
                    JOIN gallery_filters_v2 AS replacement
                      ON replacement.filter_type = 'character'
                     AND replacement.parent_filter_id = title_filter.filter_id
                     AND replacement.canonical_name = character_filter.canonical_name COLLATE NOCASE
                    WHERE character_filter.filter_type = 'character';

                    INSERT OR IGNORE INTO gallery_filter_aliases_v2 (filter_id, filter_type, alias)
                    SELECT remap.new_filter_id, 'character', alias.alias
                    FROM character_filter_remap AS remap
                    JOIN gallery_filter_aliases AS alias ON alias.filter_id = remap.old_filter_id
                    WHERE alias.filter_type = 'character';
                    INSERT OR IGNORE INTO gallery_item_filters_v2 (gid, filter_id)
                    SELECT character_map.gid, remap.new_filter_id
                    FROM gallery_item_filters AS character_map
                    JOIN gallery_filters AS character_filter
                      ON character_filter.filter_id = character_map.filter_id
                     AND character_filter.filter_type = 'character'
                    JOIN gallery_item_filters AS title_map ON title_map.gid = character_map.gid
                    JOIN gallery_filters AS title_filter
                      ON title_filter.filter_id = title_map.filter_id
                     AND title_filter.filter_type = 'title'
                    JOIN character_filter_remap AS remap
                      ON remap.old_filter_id = character_filter.filter_id
                     AND remap.parent_title_id = title_filter.filter_id;
                    INSERT OR IGNORE INTO gallery_filter_section_visibility_v2 (filter_id, gallery_category, is_visible)
                    SELECT remap.new_filter_id, visibility.gallery_category, visibility.is_visible
                    FROM gallery_filter_section_visibility AS visibility
                    JOIN character_filter_remap AS remap ON remap.old_filter_id = visibility.filter_id;
                    """;
                copyCharacters.ExecuteNonQuery();
            }

            using (var copyUnmappedCharacters = connection.CreateCommand())
            {
                copyUnmappedCharacters.Transaction = transaction;
                copyUnmappedCharacters.CommandText = """
                    INSERT INTO gallery_filters_v2 (filter_type, canonical_name, filter_category_id, category_id, parent_filter_id, created_at, updated_at)
                    SELECT old_filter.filter_type, old_filter.canonical_name,
                           old_filter.filter_category_id, old_filter.category_id, old_filter.parent_filter_id,
                           old_filter.created_at, old_filter.updated_at
                    FROM gallery_filters AS old_filter
                    WHERE old_filter.filter_type = 'character'
                      AND old_filter.parent_filter_id IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM gallery_filters_v2 AS replacement
                          WHERE replacement.filter_type = 'character'
                            AND replacement.parent_filter_id = old_filter.parent_filter_id
                            AND replacement.canonical_name = old_filter.canonical_name COLLATE NOCASE
                      );
                    INSERT OR IGNORE INTO gallery_filter_aliases_v2 (filter_id, filter_type, alias)
                    SELECT replacement.filter_id, 'character', alias.alias
                    FROM gallery_filters AS old_filter
                    JOIN gallery_filters_v2 AS replacement
                      ON replacement.filter_type = 'character'
                     AND replacement.parent_filter_id = old_filter.parent_filter_id
                     AND replacement.canonical_name = old_filter.canonical_name COLLATE NOCASE
                    JOIN gallery_filter_aliases AS alias ON alias.filter_id = old_filter.filter_id
                    WHERE old_filter.filter_type = 'character';
                    INSERT OR IGNORE INTO gallery_filter_section_visibility_v2 (filter_id, gallery_category, is_visible)
                    SELECT replacement.filter_id, visibility.gallery_category, visibility.is_visible
                    FROM gallery_filters AS old_filter
                    JOIN gallery_filters_v2 AS replacement
                      ON replacement.filter_type = 'character'
                     AND replacement.parent_filter_id = old_filter.parent_filter_id
                     AND replacement.canonical_name = old_filter.canonical_name COLLATE NOCASE
                    JOIN gallery_filter_section_visibility AS visibility ON visibility.filter_id = old_filter.filter_id
                    WHERE old_filter.filter_type = 'character';
                    """;
                copyUnmappedCharacters.ExecuteNonQuery();
            }

            using (var replace = connection.CreateCommand())
            {
                replace.Transaction = transaction;
                replace.CommandText = """
                    DROP TABLE gallery_item_filters;
                    DROP TABLE gallery_filter_section_visibility;
                    DROP TABLE gallery_filter_aliases;
                    DROP TABLE gallery_filters;
                    ALTER TABLE gallery_filters_v2 RENAME TO gallery_filters;
                    ALTER TABLE gallery_filter_aliases_v2 RENAME TO gallery_filter_aliases;
                    ALTER TABLE gallery_filter_section_visibility_v2 RENAME TO gallery_filter_section_visibility;
                    ALTER TABLE gallery_item_filters_v2 RENAME TO gallery_item_filters;
                    CREATE UNIQUE INDEX ux_gallery_title_canonical_name
                        ON gallery_filters(canonical_name) WHERE filter_type = 'title';
                    CREATE UNIQUE INDEX ux_gallery_character_parent_canonical_name
                        ON gallery_filters(parent_filter_id, canonical_name) WHERE filter_type = 'character';
                    CREATE INDEX idx_gallery_filters_type_name ON gallery_filters(filter_type, canonical_name);
                    CREATE INDEX idx_gallery_filter_aliases_alias ON gallery_filter_aliases(filter_type, alias);
                    CREATE INDEX idx_gallery_filter_visibility_category ON gallery_filter_section_visibility(gallery_category, filter_id);
                    CREATE INDEX idx_gallery_item_filters_filter ON gallery_item_filters(filter_id, gid);
                    DROP TABLE character_filter_remap;
                    """;
                replace.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally
        {
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }
    }

    private static void EnsureExternalGalleryFilterCategoryColumns(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "filter_categories")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("position"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE filter_categories ADD COLUMN position INTEGER NOT NULL DEFAULT 0;";
            addColumn.ExecuteNonQuery();
        }

        using (var positionCheck = connection.CreateCommand())
        {
            positionCheck.CommandText = "SELECT COUNT(*), COUNT(DISTINCT position), COALESCE(MIN(position), 0), COALESCE(MAX(position), -1) FROM filter_categories;";
            using var reader = positionCheck.ExecuteReader();
            reader.Read();
            var count = reader.GetInt64(0);
            var distinctCount = reader.GetInt64(1);
            var minPosition = reader.GetInt64(2);
            var maxPosition = reader.GetInt64(3);
            if (count == 0 || (count == distinctCount && minPosition == 0 && maxPosition == count - 1))
            {
                return;
            }
        }

        using var normalizePositions = connection.CreateCommand();
        normalizePositions.CommandText = """
            WITH ordered AS (
                SELECT category_id, ROW_NUMBER() OVER (ORDER BY position, name COLLATE NOCASE) - 1 AS next_position
                FROM filter_categories
            )
            UPDATE filter_categories
            SET position = (SELECT next_position FROM ordered WHERE ordered.category_id = filter_categories.category_id)
            WHERE category_id IN (SELECT category_id FROM ordered);
            """;
        normalizePositions.ExecuteNonQuery();
    }

    private static void EnsureExternalGalleryFilterCategoryReferences(SqliteConnection connection)
    {
        var columns = ReadColumns(connection, "gallery_filters")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("filtercategoryid"))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText = "ALTER TABLE gallery_filters ADD COLUMN filter_category_id INTEGER REFERENCES filter_categories(category_id) ON DELETE SET NULL;";
            addColumn.ExecuteNonQuery();
        }

        if (columns.Contains("categoryid"))
        {
            using var migrate = connection.CreateCommand();
            migrate.CommandText = "UPDATE gallery_filters SET filter_category_id = category_id WHERE filter_category_id IS NULL AND category_id IS NOT NULL;";
            migrate.ExecuteNonQuery();
        }
    }

    private static void EnsureExternalGalleryFilterCombinationSchema(SqliteConnection connection)
    {
        var combinationTableExists = false;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'gallery_filter_combinations';";
            combinationTableExists = Convert.ToInt64(check.ExecuteScalar()) > 0;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS gallery_filter_combinations (
                    combination_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    filter_category_id INTEGER REFERENCES filter_categories(category_id) ON DELETE SET NULL,
                    title_filter_id INTEGER NOT NULL REFERENCES gallery_filters(filter_id) ON DELETE CASCADE,
                    character_filter_id INTEGER REFERENCES gallery_filters(filter_id) ON DELETE CASCADE,
                    use_flg INTEGER NOT NULL DEFAULT 1 CHECK (use_flg IN (0, 1)),
                    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS gallery_item_filter_combinations (
                    gid TEXT NOT NULL REFERENCES items(gid) ON DELETE CASCADE,
                    combination_id INTEGER NOT NULL REFERENCES gallery_filter_combinations(combination_id) ON DELETE CASCADE,
                    PRIMARY KEY (gid, combination_id)
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_gallery_filter_combination_title
                    ON gallery_filter_combinations(COALESCE(filter_category_id, -1), title_filter_id)
                    WHERE character_filter_id IS NULL;
                CREATE UNIQUE INDEX IF NOT EXISTS ux_gallery_filter_combination_character
                    ON gallery_filter_combinations(COALESCE(filter_category_id, -1), title_filter_id, character_filter_id)
                    WHERE character_filter_id IS NOT NULL;
                CREATE INDEX IF NOT EXISTS idx_gallery_filter_combinations_title
                    ON gallery_filter_combinations(title_filter_id, character_filter_id);
                CREATE INDEX IF NOT EXISTS idx_gallery_filter_combinations_character
                    ON gallery_filter_combinations(character_filter_id, title_filter_id);
                CREATE INDEX IF NOT EXISTS idx_gallery_item_filter_combinations_combination
                    ON gallery_item_filter_combinations(combination_id, gid);
                """;
            command.ExecuteNonQuery();
        }

        var columns = ReadColumns(connection, "gallery_filter_combinations")
            .Select(column => column.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!columns.Contains("useflg"))
        {
            using var addUseFlag = connection.CreateCommand();
            addUseFlag.CommandText = "ALTER TABLE gallery_filter_combinations ADD COLUMN use_flg INTEGER NOT NULL DEFAULT 1 CHECK (use_flg IN (0, 1));";
            addUseFlag.ExecuteNonQuery();
        }

        var mappingMigrationComplete = false;
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT metadata_value FROM gallery_filter_metadata WHERE metadata_key = 'combination_item_map_version';";
            mappingMigrationComplete = string.Equals(check.ExecuteScalar() as string, "1", StringComparison.Ordinal);
        }

        if (!combinationTableExists || !mappingMigrationComplete)
        {
            using var transaction = connection.BeginTransaction();
            SynchronizeGalleryFilterCombinationRecords(connection, transaction);
            SynchronizeGalleryItemFilterCombinationRecords(connection, transaction);
            using (var saveMigration = connection.CreateCommand())
            {
                saveMigration.Transaction = transaction;
                saveMigration.CommandText = """
                    INSERT INTO gallery_filter_metadata (metadata_key, metadata_value)
                    VALUES ('combination_item_map_version', '1')
                    ON CONFLICT(metadata_key) DO UPDATE SET metadata_value = excluded.metadata_value;
                    """;
                saveMigration.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    private static void SynchronizeGalleryFilterCombinationRecords(SqliteConnection connection, SqliteTransaction transaction)
    {
        MergeDuplicateGalleryFilterCombinations(connection, transaction);

        using (var updateCategory = connection.CreateCommand())
        {
            updateCategory.Transaction = transaction;
            updateCategory.CommandText = """
                UPDATE gallery_filter_combinations
                SET filter_category_id = (
                        SELECT title_filter.filter_category_id
                        FROM gallery_filters AS title_filter
                        WHERE title_filter.filter_id = gallery_filter_combinations.title_filter_id
                    ),
                    updated_at = CURRENT_TIMESTAMP;
                """;
            updateCategory.ExecuteNonQuery();
        }

        using (var insertTitles = connection.CreateCommand())
        {
            insertTitles.Transaction = transaction;
            insertTitles.CommandText = """
                INSERT OR IGNORE INTO gallery_filter_combinations (filter_category_id, title_filter_id, character_filter_id)
                SELECT title_filter.filter_category_id, title_filter.filter_id, NULL
                FROM gallery_filters AS title_filter
                WHERE title_filter.filter_type = 'title';
                """;
            insertTitles.ExecuteNonQuery();
        }

        using (var insertCharacters = connection.CreateCommand())
        {
            insertCharacters.Transaction = transaction;
            insertCharacters.CommandText = """
                INSERT OR IGNORE INTO gallery_filter_combinations (filter_category_id, title_filter_id, character_filter_id)
                SELECT title_filter.filter_category_id, title_filter.filter_id, character_filter.filter_id
                FROM gallery_filters AS character_filter
                JOIN gallery_filters AS title_filter ON title_filter.filter_id = character_filter.parent_filter_id
                WHERE character_filter.filter_type = 'character'
                  AND title_filter.filter_type = 'title';
                """;
            insertCharacters.ExecuteNonQuery();
        }
    }

    private static void MergeDuplicateGalleryFilterCombinations(SqliteConnection connection, SqliteTransaction transaction)
    {
        var duplicateGroups = new List<(long TitleFilterId, long? CharacterFilterId)>();
        using (var findDuplicates = connection.CreateCommand())
        {
            findDuplicates.Transaction = transaction;
            findDuplicates.CommandText = """
                SELECT title_filter_id, character_filter_id
                FROM gallery_filter_combinations
                GROUP BY title_filter_id, character_filter_id
                HAVING COUNT(*) > 1;
                """;
            using var reader = findDuplicates.ExecuteReader();
            while (reader.Read())
            {
                duplicateGroups.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1)));
            }
        }

        foreach (var group in duplicateGroups)
        {
            var combinationIds = new List<long>();
            using (var list = connection.CreateCommand())
            {
                list.Transaction = transaction;
                list.CommandText = """
                    SELECT combination_id
                    FROM gallery_filter_combinations
                    WHERE title_filter_id = $titleId
                      AND ((character_filter_id IS NULL AND $characterId IS NULL) OR character_filter_id = $characterId)
                    ORDER BY combination_id;
                    """;
                list.Parameters.AddWithValue("$titleId", group.TitleFilterId);
                list.Parameters.AddWithValue("$characterId", (object?)group.CharacterFilterId ?? DBNull.Value);
                using var reader = list.ExecuteReader();
                while (reader.Read())
                {
                    combinationIds.Add(reader.GetInt64(0));
                }
            }

            if (combinationIds.Count < 2)
            {
                continue;
            }

            var targetCombinationId = combinationIds[0];
            foreach (var sourceCombinationId in combinationIds.Skip(1))
            {
                using (var copyMappings = connection.CreateCommand())
                {
                    copyMappings.Transaction = transaction;
                    copyMappings.CommandText = "INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id) SELECT gid, $targetId FROM gallery_item_filter_combinations WHERE combination_id = $sourceId;";
                    copyMappings.Parameters.AddWithValue("$targetId", targetCombinationId);
                    copyMappings.Parameters.AddWithValue("$sourceId", sourceCombinationId);
                    copyMappings.ExecuteNonQuery();
                }
                using (var mergeUseFlag = connection.CreateCommand())
                {
                    mergeUseFlag.Transaction = transaction;
                    mergeUseFlag.CommandText = "UPDATE gallery_filter_combinations SET use_flg = MAX(use_flg, COALESCE((SELECT use_flg FROM gallery_filter_combinations WHERE combination_id = $sourceId), 0)), updated_at = CURRENT_TIMESTAMP WHERE combination_id = $targetId;";
                    mergeUseFlag.Parameters.AddWithValue("$sourceId", sourceCombinationId);
                    mergeUseFlag.Parameters.AddWithValue("$targetId", targetCombinationId);
                    mergeUseFlag.ExecuteNonQuery();
                }
                using var deleteSource = connection.CreateCommand();
                deleteSource.Transaction = transaction;
                deleteSource.CommandText = "DELETE FROM gallery_filter_combinations WHERE combination_id = $sourceId;";
                deleteSource.Parameters.AddWithValue("$sourceId", sourceCombinationId);
                deleteSource.ExecuteNonQuery();
            }
        }
    }

    private static void SynchronizeGalleryItemFilterCombinationRecords(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var mapItems = connection.CreateCommand();
        mapItems.Transaction = transaction;
        mapItems.CommandText = """
            INSERT OR IGNORE INTO gallery_item_filter_combinations (gid, combination_id)
            SELECT title_map.gid, combination.combination_id
            FROM gallery_item_filters AS title_map
            JOIN gallery_filters AS title_filter
              ON title_filter.filter_id = title_map.filter_id
             AND title_filter.filter_type = 'title'
            LEFT JOIN gallery_item_filters AS character_map ON character_map.gid = title_map.gid
            LEFT JOIN gallery_filters AS character_filter
              ON character_filter.filter_id = character_map.filter_id
             AND character_filter.filter_type = 'character'
             AND character_filter.parent_filter_id = title_filter.filter_id
            JOIN gallery_filter_combinations AS combination
              ON combination.title_filter_id = title_filter.filter_id
             AND (
                    combination.character_filter_id = character_filter.filter_id
                    OR (character_filter.filter_id IS NULL AND combination.character_filter_id IS NULL)
                )
            WHERE NOT EXISTS (
                SELECT 1
                FROM gallery_item_filter_combinations AS existing
                WHERE existing.gid = title_map.gid
            )
              AND (
                    character_filter.filter_id IS NOT NULL
                    OR NOT EXISTS (
                        SELECT 1
                        FROM gallery_item_filters AS mapped_character
                        JOIN gallery_filters AS matching_character
                          ON matching_character.filter_id = mapped_character.filter_id
                         AND matching_character.filter_type = 'character'
                         AND matching_character.parent_filter_id = title_filter.filter_id
                        WHERE mapped_character.gid = title_map.gid
                    )
                );
            """;
        mapItems.ExecuteNonQuery();
    }

    private static SqliteConnection OpenExternalReadWriteConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout = 5000;";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private static SqliteConnection OpenStandaloneConnection(string databasePath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ConnectionString);
        connection.Open();

        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout = 10000;";
        timeout.ExecuteNonQuery();
        return connection;
    }

    private static void ValidateSqliteDatabase(SqliteConnection connection, string description)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var result = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description}の整合性確認に失敗しました: {result ?? "応答なし"}");
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static void DeleteSqliteSidecar(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private IReadOnlyList<GalleryItemDto> TryListExternalItems()
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = ExternalGalleryDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ConnectionString);
            connection.Open();

            var table = FindBestExternalTable(connection);
            return table is null
                ? []
                : ReadExternalItems(connection, table);
        }
        catch
        {
            return [];
        }
    }

    private static ExternalTable? FindBestExternalTable(SqliteConnection connection)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type IN ('table', 'view')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        using var reader = tableCommand.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var columns = ReadColumns(connection, table);
            var pathColumn = PickColumn(columns, ["path", "file_path", "filepath", "source_path", "folder_path", "archive_path"]);
            pathColumn ??= columns.FirstOrDefault(c => c.NormalizedName.Contains("path"));
            if (pathColumn is null)
            {
                continue;
            }

            return new ExternalTable(
                table,
                columns,
                pathColumn,
                PickColumn(columns, ["title", "name", "display_name", "basename", "filename", "stem"]),
                PickColumn(columns, ["id", "item_id", "book_id", "entry_id"]),
                PickColumn(columns, ["kind", "type", "category"]),
                PickColumn(columns, ["item_count", "count", "file_count", "page_count", "image_count", "total"]),
                PickColumn(columns, ["tags", "tag_names", "tag_list"]));
        }

        return null;
    }

    private static IReadOnlyList<ExternalColumn> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";

        using var reader = command.ExecuteReader();
        var columns = new List<ExternalColumn>();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            columns.Add(new ExternalColumn(name, NormalizeName(name)));
        }

        return columns;
    }

    private IReadOnlyList<GalleryItemDto> ReadExternalItems(SqliteConnection connection, ExternalTable table)
    {
        var idExpr = table.IdColumn is null
            ? $"ROW_NUMBER() OVER (ORDER BY {QuoteIdentifier(table.PathColumn.Name)})"
            : QuoteIdentifier(table.IdColumn.Name);
        var titleExpr = table.TitleColumn is null
            ? "NULL"
            : QuoteIdentifier(table.TitleColumn.Name);
        var countExpr = table.CountColumn is null
            ? "0"
            : QuoteIdentifier(table.CountColumn.Name);
        var kindExpr = table.KindColumn is null
            ? "NULL"
            : QuoteIdentifier(table.KindColumn.Name);
        var tagsExpr = table.TagsColumn is null
            ? "''"
            : QuoteIdentifier(table.TagsColumn.Name);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                {idExpr} AS id,
                CAST({titleExpr} AS TEXT) AS title,
                CAST({table.PathColumn.NameQuote()} AS TEXT) AS path,
                CAST({countExpr} AS INTEGER) AS item_count,
                CAST({kindExpr} AS TEXT) AS kind,
                CAST({tagsExpr} AS TEXT) AS tags
            FROM {QuoteIdentifier(table.Name)}
            WHERE {table.PathColumn.NameQuote()} IS NOT NULL
              AND TRIM(CAST({table.PathColumn.NameQuote()} AS TEXT)) <> ''
            LIMIT 500;
            """;

        using var reader = command.ExecuteReader();
        var items = new List<GalleryItemDto>();
        while (reader.Read())
        {
            var path = reader.GetString(2);
            var title = reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1))
                ? System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))
                : reader.GetString(1);
            var kind = reader.IsDBNull(4) ? InferKind(path) : NormalizeKind(reader.GetString(4), path);
            var tags = reader.IsDBNull(5)
                ? Array.Empty<string>()
                : SplitTags(reader.GetString(5));
            items.Add(new GalleryItemDto(
                ReadLongOrFallback(reader, 0, items.Count + 1),
                title,
                kind,
                path,
                ReadIntOrFallback(reader, 3, 0),
                tags,
                kind == "archive" ? "#93c5fd" : "#6ee7b7",
                null));
        }

        return items;
    }

    private static string ComputeHash(string value, Func<byte[], byte[]> hashData)
    {
        var bytes = hashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int ReadIntOrFallback(SqliteDataReader reader, int ordinal, int fallback)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? fallback : Convert.ToInt32(reader.GetInt64(ordinal));
        }
        catch
        {
            return fallback;
        }
    }

    private static long ReadLongOrFallback(SqliteDataReader reader, int ordinal, int fallback)
    {
        try
        {
            return reader.IsDBNull(ordinal) ? fallback : reader.GetInt64(ordinal);
        }
        catch
        {
            return fallback;
        }
    }

    private static string InferKind(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return extension is ".zip" or ".rar" or ".7z" or ".cbz" or ".cbr"
            ? "archive"
            : "folder";
    }

    private static string NormalizeKind(string value, string path)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains("zip") ||
            normalized.Contains("archive") ||
            normalized.Contains("rar") ||
            normalized.Contains("7z"))
        {
            return "archive";
        }

        if (normalized.Contains("folder") || normalized.Contains("directory") || normalized.Contains("dir"))
        {
            return "folder";
        }

        return InferKind(path);
    }

    private static string[] SplitTags(string value)
    {
        return value
            .Split(['|', ',', ';', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ExternalColumn? PickColumn(IReadOnlyList<ExternalColumn> columns, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            var normalized = NormalizeName(name);
            var exact = columns.FirstOrDefault(c => c.NormalizedName == normalized);
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var name in names)
        {
            var normalized = NormalizeName(name);
            var partial = columns.FirstOrDefault(c => c.NormalizedName.Contains(normalized));
            if (partial is not null)
            {
                return partial;
            }
        }

        return null;
    }

    private static string NormalizeName(string name)
    {
        return name.Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string QuoteIdentifier(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void PersistApplicationSettings()
    {
        using var connection = OpenConnection();
        _jsonSettingsStore.SaveApplicationSettings(connection);
    }

    private void PersistUiState()
    {
        using var connection = OpenConnection();
        _jsonSettingsStore.SaveUiState(connection);
    }

    private sealed record ExternalColumn(string Name, string NormalizedName)
    {
        public string NameQuote() => QuoteIdentifier(Name);
    }

    private sealed record ExternalTable(
        string Name,
        IReadOnlyList<ExternalColumn> Columns,
        ExternalColumn PathColumn,
        ExternalColumn? TitleColumn,
        ExternalColumn? IdColumn,
        ExternalColumn? KindColumn,
        ExternalColumn? CountColumn,
        ExternalColumn? TagsColumn);
}
