<script lang="ts">
  import { flushSync, onMount, tick } from 'svelte';
  import { get } from 'svelte/store';
  import { createVirtualizer } from '@tanstack/svelte-virtual';
  import {
    Archive,
    AlarmClock,
    ArrowRight,
    ArrowUp,
    Bookmark,
    BookOpenText,
    CalendarCheck,
    Check,
    ChartNoAxesCombined,
    ChevronDown,
    ChevronLeft,
    ChevronRight,
    ChevronUp,
    CloudUpload,
    Columns2,
    ContactRound,
    Code2,
    Copy,
    Coins,
    Download,
    ExternalLink,
    File,
    FileText,
    Folder,
    FolderPlus,
    FolderOpen,
    Footprints,
    GripVertical,
    Grid3X3,
    Hash,
    Keyboard,
    Layers3,
    LayoutGrid,
    ListFilter,
    Link,
    Languages,
    Minus,
    OctagonAlert,
    PanelLeft,
    Palette,
    Pencil,
    Pin,
    Plus,
    RefreshCw,
    Search,
    Settings,
    Sparkles,
    Star,
    StickyNote,
    Tags,
    Trash2,
    Trophy,
    TriangleAlert,
    Upload,
    UserPlus,
    UserRound,
    UsersRound,
    Wrench,
    X
  } from 'lucide-svelte';
  import { postHostMessage } from './lib/hostBridge';
  import {
    type AppLanguage,
    applySystemLanguage,
    normalizeLanguage,
    startSystemLocalization,
    stopSystemLocalization,
    translateSystemText
  } from './lib/i18n';

  type ActiveView = 'library' | 'bookmarks' | 'creators' | 'creatorTracking' | 'explorer' | 'filters' | 'tags' | 'userMetrics' | 'board' | 'calendar' | 'settings' | 'userGuide';
  type BookmarkableView = 'library' | 'explorer' | 'creators' | 'creatorTracking';
  type StickyNoteView = BookmarkableView | 'userMetrics';
  type SettingsSection = 'programs' | 'gestures' | 'keyboardShortcuts' | 'calendar' | 'gid' | 'theme' | 'language' | 'tabCandidates' | 'galleryTargets' | 'creatorTracking' | 'winrar' | 'ffmpeg' | 'thumbnailCache' | 'sqliteDatabase' | 'searchEngine';
  type UserGuideSectionId = 'overview' | 'firstSteps' | 'gallery' | 'explorer' | 'organize' | 'creators' | 'bookmarks' | 'metrics' | 'settings' | 'data' | 'shortcuts' | 'troubleshooting' | 'acknowledgements';
  type ColorTheme = 'light' | 'dark';
  type ThemeSettings = {
    theme: ColorTheme;
    mainColor: string;
    subColor: string;
  };

  type PCloudSnapshot = {
    fileId: number;
    fileName: string;
    sizeBytes: number;
    createdAt: string;
  };

  type DatabaseScanSchedule = {
    id: string;
    weekdays: number[];
    time: string;
    categories: string[];
    lastStartedAt?: string | null;
  };

  type GalleryItem = {
    id: number;
    title: string;
    kind: 'folder' | 'archive';
    path: string;
    count: number;
    tags: string[];
    accent: string;
    thumbnailUri?: string | null;
  };

  type GalleryWork = {
    id: string;
    path: string;
    name: string;
    category: string;
    topFolder: string;
    creator: string;
    title: string;
    character: string;
    rating: number;
    imageCount: number;
    durationSeconds: number | null;
    lastAccessTime: string;
    lastWriteTime: string;
    tags: string[];
  };

  type GalleryReverseFilterMode = 'title' | 'character';
  type GalleryReverseCharacterFilter = {
    value: string;
    label: string;
    title: string;
  };
  type GalleryReverseFilterRequest = {
    requestId: string;
    mode: GalleryReverseFilterMode;
    workName: string;
  };

  type GalleryCreatorSummary = {
    id: string;
    category: string;
    creator: string;
    creatorFolder: string;
    coreTitles: string[];
    coreTags: string[];
    titleComposition: GalleryCreatorCompositionSlice[];
    tagComposition: GalleryCreatorCompositionSlice[];
    totalRating: number;
    fileCount: number;
    zipFileCount: number;
    totalImageCount: number;
    averageImageCount: number;
    ratedFileCount: number;
    maxRating: number;
    lastAccessTime: string;
    lastUpdatedTime: string;
    trackingLastActivityOn: string;
    hasCreatorTracking: boolean;
    sinceLastCheckDays: number;
    followWarnFlg: boolean;
    followAlertFlg: boolean;
    trackingDays: number;
    totalSpend: number;
    spendCurrency: string;
    trackingSites: string[];
    evaluationMetrics: Record<CreatorTrackingMetricKey, number>;
    personalRating: number;
    ratingBucket: string;
    searchText: string;
  };

  type GalleryCreatorCompositionSlice = {
    label: string;
    count: number;
    isCore: boolean;
  };

  type GalleryCreatorSummarySortKey = 'rating' | 'files' | 'average' | 'name' | 'updated' | 'sinceCheck' | 'trackingDays' | 'spend';
  type GalleryCreatorSummarySortDirection = 'asc' | 'desc';
  type GalleryCreatorSummarySortCriterion = { key: GalleryCreatorSummarySortKey; direction: GalleryCreatorSummarySortDirection };
  type GalleryCreatorSummaryReminderFilter = '' | 'warning' | 'alert';

  type UserMetricsEntity = 'creators' | 'titles' | 'characters' | 'tags';
  type UserMetricsRankItem = {
    label: string;
    fileCount: number;
    imageCount: number;
    totalRating: number;
    averageRating: number;
    recentFileCount: number;
    spend: number;
    trackingDays: number;
  };
  type UserMetricsRankingSet = Record<UserMetricsEntity, UserMetricsRankItem[]>;
  type UserMetricsTrendPoint = {
    period: string;
    fileCount: number;
    imageCount: number;
    spend: number;
    cumulativeSpend: number;
  };
  type UserMetricsMetricRanking = {
    key: string;
    items: Array<{ creator: string; score: number }>;
  };
  type UserMetricsDashboard = {
    category: string;
    generatedAt: string;
    totalFiles: number;
    totalImages: number;
    totalRating: number;
    ratedFiles: number;
    creatorCount: number;
    titleCount: number;
    characterCount: number;
    tagCount: number;
    fileRankings: UserMetricsRankingSet;
    imageRankings: UserMetricsRankingSet;
    ratingRankings: UserMetricsRankingSet;
    trends: UserMetricsTrendPoint[];
    metricRankings: UserMetricsMetricRanking[];
    siteRankings: Array<{ label: string; creatorCount: number }>;
    spendRankings: UserMetricsRankItem[];
    followDayRankings: UserMetricsRankItem[];
    titleBubbles: Array<{ title: string; totalRating: number; targetTagFileCount: number; fileCount: number; averageRating: number }>;
    insights: Array<{ title: string; value: string; description: string; tone: string }>;
    displayCurrency: string;
    targetTag: string;
  };

  type GalleryCreatorCoreFilterOption = {
    value: string;
    count: number;
  };

  type CreatorTrackingActivityLink = {
    label: string;
    url: string;
    status: string;
    note: string;
    followUpEnabled: boolean;
    followUpDays: number;
  };

  type CreatorTrackingStorageUsage = 'Gallery' | 'Stockroom' | 'Temporary';

  type CreatorTrackingStorageLocation = {
    id: string;
    usage: CreatorTrackingStorageUsage;
    path: string;
  };

  type CreatorTrackingSubscription = {
    id: string;
    platform: string;
    plan: string;
    currency: string;
    amount: number;
    billingFrequency: string;
    startedOn: string;
    renewalOn: string;
    endingPlanned: boolean;
    reminder: boolean;
    wishlist: boolean;
    isActive: boolean;
  };

  type CreatorTrackingPurchase = {
    id: string;
    platform: string;
    productName: string;
    currency: string;
    amount: number;
    purchasedOn: string;
    wishlist: boolean;
  };

  type CalendarSettings = {
    weekStartDay: number;
  };

  type GoogleCalendarSyncSettings = {
    autoSyncEnabled: boolean;
    clientId: string;
    hasClientSecret: boolean;
    hasRefreshToken: boolean;
    calendarId: string;
    redirectUri: string;
    lastSyncedAt: string;
    lastSyncError: string;
  };

  type CalendarSubscriptionEvent = {
    id: string;
    creator: string;
    displayName: string;
    platform: string;
    plan: string;
    currency: string;
    amount: number;
    renewalOn: string;
    endingPlanned: boolean;
    reminder: boolean;
  };

  type CalendarViewMode = 'month' | 'focus';

  type CreatorTrackingDashboardContext = {
    category: string;
    creators: string[];
    creatorCount: number;
    fileCount: number;
    fileRank: number;
    totalImageCount: number;
    totalImageCountRank: number;
    totalRating: number;
    totalRatingRank: number;
  };

  type CreatorTrackingArchiveMonth = {
    month: string;
    fileCount: number;
    imageCount: number;
  };

  type CreatorTrackingArchiveSnapshot = {
    date: string;
    fileCount: number;
    imageCount: number;
  };

  type CreatorTrackingDashboard = {
    category: string;
    creatorCount: number;
    fileCount: number;
    fileRank: number;
    totalImageCount: number;
    totalImageCountRank: number;
    totalRating: number;
    totalRatingRank: number;
    trackingDays: number;
    trackingDaysRank: number;
    totalSpend: number;
    totalSpendRank: number;
    recentThreeMonthSpend: number;
    recentThreeMonthSpendRank: number;
    currency: string;
    hasMissingExchangeRates: boolean;
    archiveMonths: CreatorTrackingArchiveMonth[];
    archiveSnapshots?: CreatorTrackingArchiveSnapshot[];
  };

  type CreatorTrackingMetricKey = 'situation' | 'continuity' | 'consistency' | 'quality' | 'texture' | 'volume';

  type CreatorTrackingMetricDefinition = {
    key: CreatorTrackingMetricKey;
    label: string;
    weight: number;
  };

  type CreatorTrackingSettingsMetricDraft = CreatorTrackingMetricDefinition & {
    weightPercent: number | null;
  };

  type CreatorTrackingActivityPlaceSetting = {
    label: string;
    placeholder: string;
    iconDataUri: string;
  };

  type CreatorTrackingSettingsDraft = {
    metrics: CreatorTrackingSettingsMetricDraft[];
    scoreMultiplier: number;
    scoreDecimalPlaces: number;
    displayCurrency: string;
    recentSpendMonths: number;
    exchangeRateProvider: string;
    goldRankPercent: number;
    silverRankPercent: number;
    bronzeRankPercent: number;
    archiveScale: 'week' | 'month' | 'year';
    compositionLabelLimit: number;
    activityPlaces: CreatorTrackingActivityPlaceSetting[];
    followPolicyOptions: string[];
  };

  type CreatorTrackingTab = {
    id: string;
    creator: string;
    summary: GalleryCreatorSummary;
    tracking: CreatorTracking | null;
    dashboardContext: CreatorTrackingDashboardContext | null;
    dashboard: CreatorTrackingDashboard | null;
    isLoading: boolean;
    isSaving: boolean;
    dirty: boolean;
    error: string;
    billingView: 'subscriptions' | 'purchases';
    archiveScale: 'week' | 'month' | 'year';
    requestId: string;
  };

  type CreatorTracking = {
    creator: string;
    displayName: string;
    alternateName: string;
    trackingStatus: string;
    activityStatus: string;
    followUpStatus: string;
    lastCheckedOn: string;
    lastActivityOn: string;
    activitySummary: string;
    activityLinks: CreatorTrackingActivityLink[];
    mainStoragePath: string;
    workStoragePath: string;
    unsortedStoragePath: string;
    storageLocations: CreatorTrackingStorageLocation[];
    evaluationMetrics: Record<CreatorTrackingMetricKey, number>;
    personalRating: number;
    evaluationMemo: string;
    subscriptionHistory: CreatorTrackingSubscription[];
    purchaseHistory: CreatorTrackingPurchase[];
    monthlySupportAmount: number;
    lifetimeSpend: number;
    currency: string;
    supportStartedOn: string;
    supportEndedOn: string;
    supportMemo: string;
    updatedAt: string;
  };

  type GalleryFilterOption = {
    value: string;
    count: number;
    label?: string | null;
  };

  type GalleryFilterPart = 'ratings' | 'tags' | 'creators' | 'titles' | 'characters';
  type GalleryExpandableFilterKind = 'creator' | 'title' | 'character' | 'tag';

  type GalleryFilterSortKey = 'rating' | 'files' | 'name';
  type GalleryFilterSortDirection = 'asc' | 'desc';
  type GalleryFilterSortCriterion = {
    key: GalleryFilterSortKey;
    direction: GalleryFilterSortDirection;
  };

  type GalleryThumbnailSortKey = 'rating' | 'images' | 'accessed' | 'path';
  type GalleryThumbnailSortDirection = 'asc' | 'desc';
  type GalleryThumbnailSortCriterion = {
    key: GalleryThumbnailSortKey;
    direction: GalleryThumbnailSortDirection;
  };

  type FilterEditorDefinition = {
    id: number;
    filterType: 'title' | 'character';
    canonicalName: string;
    categoryName: string;
    parentTitleId: number | null;
    parentTitleName: string;
    aliases: string[];
    visibleCategories: string[];
    itemCount: number;
  };

  type FilterEditorCategory = {
    id: number;
    name: string;
    position: number;
    titleCount: number;
    characterCount: number;
  };

  type SearchEngineSettings = {
    provider: 'google' | 'brave' | 'gemini';
    googleSearchUrlTemplate: string;
    braveApiKey: string;
    geminiApiKey: string;
  };

  type FilterCsvPreviewRow = {
    rowNumber: number;
    filterCategoryId?: number | null;
    categoryName: string;
    titleName: string;
    characterName: string;
    newCategoryName?: string;
    add?: boolean;
    mergeFilterCategoryId?: number | null;
  };

  type FilterCsvScope = 'category' | 'combination';

  type FilterCsvImportAnalysisRow = {
    rowNumber: number;
    action: string;
    sourceCategory: string;
    sourceTitle: string;
    sourceCharacter: string;
    targetCategory: string;
    targetTitle: string;
    targetCharacter: string;
    detail: string;
  };

  type FilterCsvImportAnalysis = {
    totalRows: number;
    addCount: number;
    updateCount: number;
    explicitMergeCount: number;
    automaticTitleMergeCount: number;
    automaticCharacterMoveCount: number;
    duplicateAddSkipCount: number;
    rows: FilterCsvImportAnalysisRow[];
    cases: Record<string, FilterCsvImportAnalysisRow[]>;
  };

  type TagManagementDefinition = {
    id: number;
    tag: string;
    useFlag: number;
    position: number;
    categories: string[];
    itemCount: number;
  };

  type TagManagementImportRow = {
    rowNumber: number;
    tagId: number | null;
    tag: string;
    newTag: string;
    useFlag: number | null;
  };

  type StandardNameSearchResult = {
    name: string;
    source: string;
    url: string | null;
    detail: string | null;
  };

  type AllocationRow = FilterEditorDefinition & {
    titleName: string;
  };

  type AllocationCategoryGroup = {
    category: string;
    rows: AllocationRow[];
  };

  type AllocationVirtualItem =
    | { kind: 'category'; key: string; category: string }
    | { kind: 'title'; key: string; row: AllocationRow };

  type PendingFilterEditorNavigation = {
    view: ActiveView;
    gallerySection?: string;
  };

  type GalleryTagAssignmentOption = {
    id: number;
    tag: string;
    workCount: number;
    searchText: string;
  };

  type GalleryTagAssignment = {
    requestId: string;
    sourcePath: string;
    works: GalleryWork[];
    category: string;
    creator: string;
    title: string;
    creatorTitleTags: GalleryTagAssignmentOption[];
    availableTags: GalleryTagAssignmentOption[];
    commonAssignedTags: GalleryTagAssignmentOption[];
    creatorQuery: string;
    query: string;
    selectedTagId: number | null;
    selectedAssignedTagIds: number[];
    assignedTagsPanelOpen: boolean;
    isLoading: boolean;
    isSaving: boolean;
  };

  type GalleryTitleAssignmentOption = {
    id: number;
    title: string;
    categoryName: string;
    workCount: number;
    searchText: string;
  };

  type GalleryTitleAssignment = {
    requestId: string;
    characterRequestId: string;
    works: GalleryWork[];
    category: string;
    creators: string[];
    creatorTitles: GalleryTitleAssignmentOption[];
    availableTitles: GalleryTitleAssignmentOption[];
    commonAssignedTitles: GalleryTitleAssignmentOption[];
    creatorTitleCharacters: GalleryCharacterAssignmentOption[];
    availableCharacters: GalleryCharacterAssignmentOption[];
    creatorQuery: string;
    query: string;
    characterCreatorQuery: string;
    characterQuery: string;
    categoryFilter: string;
    selectedTitleId: number | null;
    selectedCharacterId: number | null;
    selectedAssignedTitleIds: number[];
    assignedTitlesPanelOpen: boolean;
    characterPanelOpen: boolean;
    isLoading: boolean;
    isCharacterLoading: boolean;
    isSaving: boolean;
  };

  type GalleryCharacterAssignmentOption = {
    id: number;
    character: string;
    title: string;
    workCount: number;
    searchText: string;
  };

  type GalleryCharacterAssignment = {
    requestId: string;
    sourcePath: string;
    works: GalleryWork[];
    category: string;
    creators: string[];
    titles: string[];
    creatorTitleCharacters: GalleryCharacterAssignmentOption[];
    availableCharacters: GalleryCharacterAssignmentOption[];
    commonAssignedCharacters: GalleryCharacterAssignmentOption[];
    creatorQuery: string;
    query: string;
    selectedCharacterId: number | null;
    selectedAssignedCharacterIds: number[];
    assignedCharactersPanelOpen: boolean;
    isLoading: boolean;
    isSaving: boolean;
  };

  type GalleryScanTarget = {
    category: string;
    path: string;
    position: number;
  };

  type GallerySectionDefinition = {
    id: string;
    label: string;
    position: number;
  };

  type GallerySectionDraft = {
    key: string;
    label: string;
  };

  type GalleryScanSettings = {
    category: string;
    supportedExtensions: string;
    cardAspect: 'portrait' | 'landscape';
    enabledFilters: string[];
    fileNameLines: number;
    creatorLabel: string;
    titleLabel: string;
    characterLabel: string;
    tagLabel: string;
    coreTitleLabel: string;
    coreTagsLabel: string;
  };

  type ThumbnailCropAdjustment = {
    category: string;
    horizontalOffsetPercent: number;
    verticalOffsetPercent: number;
    scalePercent: number;
  };

  type GalleryContextMenu = {
    x: number;
    y: number;
    work: GalleryWork;
  };

  type ExternalAppRule = {
    id: number;
    name: string;
    executablePath: string;
    launchOptions: string;
    allowMultiple: boolean;
    clickExtensions: string;
    doubleClickExtensions: string;
    contextMenuExtensions: string;
  };

  type WinRarSettings = {
    executablePath: string;
    supportedExtensions: string;
    showOpenInContextMenu: boolean;
  };

  type FfmpegSettings = {
    executablePath: string;
    supportedExtensions: string;
  };

  type GidSettings = {
    targetExtensions: string;
    digitCount: number;
  };

  type GidMigrationPreview = {
    targetDigitCount: number;
    itemCount: number;
    fileRenameCount: number;
    databaseOnlyCount: number;
    readyFileCount: number;
    alreadyRenamedFileCount: number;
    missingFileCount: number;
    conflictingFileCount: number;
    mismatchedGidCount: number;
    isAlreadyMigrated: boolean;
    canExecute: boolean;
    issues: string[];
  };

  type ExplorerEntry = {
    name: string;
    romanizedName: string;
    path: string;
    isDirectory: boolean;
    extension: string;
    size: number | null;
    pageCount: number | null;
    createdAt: string;
    accessedAt: string;
    modifiedAt: string;
  };

  type ExplorerDetailColumnId =
    | 'icon'
    | 'name'
    | 'pages'
    | 'rating'
    | 'created'
    | 'accessed'
    | 'modified'
    | 'size'
    | 'resolution'
    | 'ratio'
    | 'width'
    | 'height'
    | 'length'
    | 'fps'
    | 'gid'
    | 'type';

  type ExplorerDetailColumn = {
    id: ExplorerDetailColumnId;
    label: string;
    width: string;
    sort?: 'name' | 'modified' | 'size' | 'type';
  };

  type MouseGestureCommand = 'parent' | 'back' | 'forward' | 'clearFilter' | 'refresh' | 'copyPath' | 'none';

  type MouseGestureBinding = {
    gesture: string;
    command: MouseGestureCommand;
  };

  type MouseGestureSettings = {
    enabled: boolean;
    lineColor: string;
    lineWidth: number;
    threshold: number;
    bindings: MouseGestureBinding[];
  };

  type KeyboardShortcutCommand =
    | 'openGlobalSearch'
    | 'navigateBack'
    | 'navigateExplorerParent'
    | 'createExplorerFolder'
    | 'selectNextTab'
    | 'selectPreviousTab'
    | 'copyExplorerSelection'
    | 'cutExplorerSelection'
    | 'pasteExplorerSelection'
    | 'selectAllExplorerEntries'
    | 'renameExplorerEntry'
    | 'deleteExplorerSelection';

  type KeyboardShortcutSettings = Record<KeyboardShortcutCommand, string>;

  type KeyboardShortcutDefinition = {
    command: KeyboardShortcutCommand;
    label: string;
    scope: string;
  };

  const explorerDetailColumnDefinitions: ExplorerDetailColumn[] = [
    { id: 'icon', label: 'アイコン', width: '34px' },
    { id: 'name', label: '名前', width: 'minmax(240px, 1fr)', sort: 'name' },
    { id: 'pages', label: 'ページ', width: '70px' },
    { id: 'rating', label: 'レート', width: '70px' },
    { id: 'created', label: '作成日時', width: '165px' },
    { id: 'accessed', label: 'アクセス日時', width: '165px' },
    { id: 'modified', label: '更新日時', width: '165px', sort: 'modified' },
    { id: 'size', label: 'サイズ', width: '100px', sort: 'size' },
    { id: 'resolution', label: '解像度', width: '105px' },
    { id: 'ratio', label: '比', width: '72px' },
    { id: 'width', label: '幅', width: '70px' },
    { id: 'height', label: '高さ', width: '70px' },
    { id: 'length', label: '長さ', width: '80px' },
    { id: 'fps', label: 'FPS', width: '68px' },
    { id: 'gid', label: 'gid', width: '150px' },
    { id: 'type', label: '種類', width: '110px', sort: 'type' }
  ];

  const defaultExplorerDetailColumns: ExplorerDetailColumnId[] = ['icon', 'name', 'pages', 'gid', 'modified', 'type', 'size'];
  const defaultMouseGestureSettings: MouseGestureSettings = {
    enabled: true,
    lineColor: '#caff19',
    lineWidth: 4,
    threshold: 50,
    bindings: [{ gesture: '↑', command: 'parent' }]
  };
  const keyboardShortcutDefinitions: KeyboardShortcutDefinition[] = [
    { command: 'selectNextTab', label: '次のタブへ移動', scope: 'Explorer / Creator Tracking' },
    { command: 'selectPreviousTab', label: '前のタブへ移動', scope: 'Explorer / Creator Tracking' },
    { command: 'openGlobalSearch', label: '検索を開く', scope: 'アプリ全体' },
    { command: 'navigateBack', label: '前の画面へ戻る', scope: 'アプリ全体' },
    { command: 'navigateExplorerParent', label: '親フォルダへ移動', scope: 'Explorer' },
    { command: 'createExplorerFolder', label: '新しいフォルダを作成', scope: 'Explorer' },
    { command: 'copyExplorerSelection', label: '選択項目をコピー', scope: 'Explorer' },
    { command: 'cutExplorerSelection', label: '選択項目を切り取り', scope: 'Explorer' },
    { command: 'pasteExplorerSelection', label: '貼り付け', scope: 'Explorer' },
    { command: 'selectAllExplorerEntries', label: 'すべて選択', scope: 'Explorer' },
    { command: 'renameExplorerEntry', label: '名前を変更', scope: 'Explorer' },
    { command: 'deleteExplorerSelection', label: '選択項目を削除', scope: 'Explorer' }
  ];
  const defaultKeyboardShortcutSettings: KeyboardShortcutSettings = {
    openGlobalSearch: 'Ctrl+F',
    navigateBack: 'Backspace',
    navigateExplorerParent: 'Alt+ArrowUp',
    createExplorerFolder: 'Ctrl+N',
    selectNextTab: 'Ctrl+Tab',
    selectPreviousTab: 'Ctrl+Shift+Tab',
    copyExplorerSelection: 'Ctrl+C',
    cutExplorerSelection: 'Ctrl+X',
    pasteExplorerSelection: 'Ctrl+V',
    selectAllExplorerEntries: 'Ctrl+A',
    renameExplorerEntry: 'F2',
    deleteExplorerSelection: 'Delete'
  };

  const defaultGallerySections: GallerySectionDefinition[] = [
    { id: 'gallery', label: 'Gallery' }
  ].map((section, position) => ({ ...section, position }));
  const defaultGallerySectionId = defaultGallerySections[0].id;
  const userMetricsEntityOptions: Array<{ key: UserMetricsEntity; label: string }> = [
    { key: 'creators', label: 'Creator' },
    { key: 'titles', label: 'Title' },
    { key: 'characters', label: 'Character' },
    { key: 'tags', label: 'Tag' }
  ];

  const galleryCreatorRatingBuckets = [
    { value: '10plus', label: '★10+' },
    { value: '7-9', label: '★9～7' },
    { value: '4-6', label: '★6～4' },
    { value: '3', label: '★★★' },
    { value: '2', label: '★★' },
    { value: '1', label: '★' },
    { value: '0', label: 'NR' }
  ];
  const galleryCreatorSummarySortDefinitions: Array<{ key: GalleryCreatorSummarySortKey; label: string }> = [
    { key: 'rating', label: 'Total★' },
    { key: 'files', label: 'Files' },
    { key: 'average', label: 'Avg' },
    { key: 'name', label: 'Abc' },
    { key: 'updated', label: 'Last Updated' },
    { key: 'sinceCheck', label: 'Since last check' },
    { key: 'trackingDays', label: 'Follow Days' },
    { key: 'spend', label: 'Total Spend' }
  ];
  const creatorTrackingMetricDefinitions: CreatorTrackingMetricDefinition[] = [
    { key: 'situation', label: '', weight: 0 },
    { key: 'consistency', label: '', weight: 0 },
    { key: 'continuity', label: '', weight: 0 },
    { key: 'quality', label: '', weight: 0 },
    { key: 'texture', label: '', weight: 0 },
    { key: 'volume', label: '', weight: 0 }
  ];

  const defaultCreatorTrackingSettingsDraft: CreatorTrackingSettingsDraft = {
    metrics: creatorTrackingMetricDefinitions.map((metric) => ({
      ...metric,
      weightPercent: null
    })),
    scoreMultiplier: 1.25,
    scoreDecimalPlaces: 1,
    displayCurrency: 'JPY',
    recentSpendMonths: 3,
    exchangeRateProvider: 'frankfurter',
    goldRankPercent: 5,
    silverRankPercent: 10,
    bronzeRankPercent: 15,
    archiveScale: 'month',
    compositionLabelLimit: 5,
    followPolicyOptions: [],
    activityPlaces: []
  };

  const creatorTrackingCompositionColors = [
    '#b8d94a',
    '#5ba7ef',
    '#61c9b4',
    '#dca45d',
    '#9386ce',
    '#cf746f',
    '#728eae',
    '#9aaa62'
  ];

  const galleryCardAspectDefaults: Record<string, 'portrait' | 'landscape'> = {
    gallery: 'portrait'
  };

  const defaultThemeSettings: ThemeSettings = {
    theme: 'dark',
    mainColor: '#caff19',
    subColor: '#ffb342'
  };

  const themeRecommendations: Record<ColorTheme, Array<Pick<ThemeSettings, 'mainColor' | 'subColor'>>> = {
    dark: [
      { mainColor: '#caff19', subColor: '#ffb342' },
      { mainColor: '#67e8f9', subColor: '#fb7185' },
      { mainColor: '#a78bfa', subColor: '#fbbf24' },
      { mainColor: '#5eead4', subColor: '#f472b6' },
      { mainColor: '#93c5fd', subColor: '#f59e0b' }
    ],
    light: [
      { mainColor: '#6f8f17', subColor: '#c56522' },
      { mainColor: '#087f8c', subColor: '#c2415d' },
      { mainColor: '#6550b9', subColor: '#b86b09' },
      { mainColor: '#087869', subColor: '#b83f86' },
      { mainColor: '#3267a8', subColor: '#b45309' }
    ]
  };

  const settingsSectionDetails: Record<SettingsSection, { title: string; description: string }> = {
    programs: { title: '起動プログラム', description: 'クリック起動と右クリックメニューへの表示を個別に設定します' },
    gestures: { title: 'マウスジェスチャ', description: '右ボタンを押したままのジェスチャと軌跡表示を設定します' },
    keyboardShortcuts: { title: 'キーボードショートカット', description: 'アプリ内のキーボード操作に割り当てるキーを設定します' },
    gid: { title: 'gid管理', description: 'gid発番対象の拡張子と既存GIDの桁数変更を管理します' },
    theme: { title: 'テーマ', description: 'アプリ全体の明暗テーマとアクセントカラーを設定します' },
    language: { title: 'Language', description: 'アプリのシステムUIで使用する言語を設定します' },
    tabCandidates: { title: '新規タブ候補', description: 'Explorer の新規タブメニューに表示するフォルダを登録します' },
    galleryTargets: { title: '区分別の設定', description: '区分ごとの走査対象、使用フィルタ、カード表示とサムネイルの調整を設定します' },
    creatorTracking: { title: 'Creator Tracking', description: '評価・集計・活動場所・表示に関する既定値を設定します' },
    calendar: { title: 'Calendar', description: 'サブスク更新予定の表示とGoogleカレンダー連携用の出力を設定します' },
    winrar: { title: 'WinRAR設定', description: 'WinRAR の実行ファイルと右クリックメニューで扱う書庫形式を設定します' },
    ffmpeg: { title: 'FFmpeg設定', description: '動画サムネイル生成に使用する FFmpeg の実行ファイルと対応形式を設定します' },
    thumbnailCache: { title: 'サムネイルキャッシュ', description: '対象ディレクトリ配下のフォルダと対応ファイルのサムネイルを保存します' },
    sqliteDatabase: { title: 'データベース', description: '本体DB、キャッシュDB、走査スケジュール、クラウドバックアップを管理します' },
    searchEngine: { title: '検索エンジン', description: 'フィルタエディタで標準名を調べる検索方法を設定します' }
  };

  const userGuideSections: Array<{ id: UserGuideSectionId; label: string; number: string }> = [
    { id: 'overview', label: 'はじめに', number: '01' },
    { id: 'firstSteps', label: '初期設定と基本の流れ', number: '02' },
    { id: 'gallery', label: 'Gallery', number: '03' },
    { id: 'explorer', label: 'Explorer', number: '04' },
    { id: 'organize', label: 'Filters・Tags', number: '05' },
    { id: 'creators', label: 'Creators・Tracking', number: '06' },
    { id: 'bookmarks', label: 'Bookmark・付箋', number: '07' },
    { id: 'metrics', label: 'User Metrics', number: '08' },
    { id: 'settings', label: 'Settings', number: '09' },
    { id: 'data', label: 'DB・キャッシュ・バックアップ', number: '10' },
    { id: 'shortcuts', label: 'ショートカット', number: '11' },
    { id: 'troubleshooting', label: '困ったときは', number: '12' },
    { id: 'acknowledgements', label: '謝辞', number: '13' }
  ];

  const mouseGestureCommands: Array<{ value: MouseGestureCommand; label: string }> = [
    { value: 'parent', label: '親フォルダへ移動' },
    { value: 'back', label: '戻る' },
    { value: 'forward', label: '進む' },
    { value: 'clearFilter', label: 'フィルター消去' },
    { value: 'refresh', label: '再読み込み' },
    { value: 'copyPath', label: 'フォルダパスをクリップボードに格納' },
    { value: 'none', label: 'なし' }
  ];

  type ExplorerTab = {
    id: string;
    path: string;
    label: string;
  };

  type ExplorerBookmark = {
    path: string;
    label: string;
  };

  type ViewBookmark = {
    id: number;
    name: string;
    viewType: BookmarkableView;
    stateJson: string;
    thumbnailDataUrl: string;
    windowWidth: number;
    windowHeight: number;
    windowIsMaximized: boolean;
    position: number;
    createdAt: string;
    updatedAt: string;
  };

  type StickyNoteColorKey = 'amber' | 'lime' | 'sky' | 'teal' | 'violet' | 'coral';

  type StickyNoteItem = {
    id: number;
    viewType: StickyNoteView;
    contextKey: string;
    contextLabel: string;
    content: string;
    x: number;
    y: number;
    width: number;
    height: number;
    colorKey: StickyNoteColorKey;
    contentMode: 'plain' | 'markdown';
    createdAt: string;
    updatedAt: string;
  };

  type StickyNoteBoardItem = {
    note: StickyNoteItem;
    hasLiveNote: boolean;
    bookmarkCount: number;
  };

  type StickyNoteResizeEdge = 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w' | 'nw';

  type StickyNoteInteraction = {
    id: number;
    mode: 'move' | 'resize';
    edge?: StickyNoteResizeEdge;
    pointerX: number;
    pointerY: number;
    x: number;
    y: number;
    width: number;
    height: number;
  };

  const stickyNoteResizeEdges: StickyNoteResizeEdge[] = ['n', 'ne', 'e', 'se', 's', 'sw', 'w', 'nw'];
  const stickyNoteTitlebarHeight = 34;
  const galleryWorkStickyContextSeparator = '::work::';
  const stickyNotePalette: Array<{
    key: StickyNoteColorKey;
    label: string;
    color: string;
    border: string;
    text: string;
  }> = [
    { key: 'amber', label: 'アンバー', color: '#e0b95d', border: '#9d7628', text: '#302715' },
    { key: 'lime', label: 'ライム', color: '#b8d75f', border: '#78962d', text: '#233015' },
    { key: 'sky', label: 'スカイ', color: '#75b9e8', border: '#3f7dac', text: '#172b3d' },
    { key: 'teal', label: 'ティール', color: '#70cbbf', border: '#398f85', text: '#17312e' },
    { key: 'violet', label: 'バイオレット', color: '#a99ce2', border: '#6f61aa', text: '#27203d' },
    { key: 'coral', label: 'コーラル', color: '#e58f91', border: '#a94f58', text: '#3b1d20' }
  ];

  type BookmarkCapture = {
    viewType: BookmarkableView;
    viewLabel: string;
    suggestedName: string;
    stateJson: string;
  };

  const databaseScheduleWeekdays = [
    { value: 0, label: '日曜' },
    { value: 1, label: '月曜' },
    { value: 2, label: '火曜' },
    { value: 3, label: '水曜' },
    { value: 4, label: '木曜' },
    { value: 5, label: '金曜' },
    { value: 6, label: '土曜' }
  ];

  type NavigationHistoryEntry = {
    view: ActiveView;
    capture: BookmarkCapture | null;
    settingsSection: SettingsSection;
    userGuideSection: UserGuideSectionId;
    userMetricsCategory: string;
  };

  type GlobalSearchMode = 'normal' | 'creator';

  type GalleryBookmarkState = {
    version: number;
    section: string;
    ratingFilters: number[];
    tagFilters: string[];
    creatorFilters: string[];
    titleFilters: string[];
    characterFilters: string[];
    filterSorts: GalleryFilterSortCriterion[];
    thumbnailSorts: GalleryThumbnailSortCriterion[];
    expanded: Record<GalleryExpandableFilterKind, boolean>;
    collapsePins: Record<GalleryExpandableFilterKind, boolean>;
    pinned?: { creators: string[]; titles: string[] };
    promoted: { creators: string[]; titles: string[]; characters: string[]; tags: string[] };
    cardColumns?: number;
    scrollTop?: number;
    loadedCount?: number;
    query?: string;
    stickyNotes?: StickyNoteItem[];
  };

  type CreatorSummaryBookmarkState = {
    version: number;
    section: string;
    ratings: string[];
    coreTitles: string[];
    coreTags: string[];
    sites?: string[];
    overallRatings?: number[];
    metricScores?: Partial<Record<CreatorTrackingMetricKey, number[]>>;
    followReminder?: GalleryCreatorSummaryReminderFilter;
    query: string;
    sorts?: GalleryCreatorSummarySortCriterion[];
    sort?: GalleryCreatorSummarySortKey;
    sortDirections?: Partial<Record<GalleryCreatorSummarySortKey, GalleryCreatorSummarySortDirection>>;
    expanded: { coreTitles: boolean; coreTags: boolean; more?: boolean };
    stickyNotes?: StickyNoteItem[];
  };

  type ExplorerBookmarkState = {
    version: number;
    tabs: Array<{ path: string; label: string }>;
    activeIndex: number;
    split: { leftIndex: number; rightIndex: number; focusedPane: 'left' | 'right' } | null;
    query: string;
    splitQuery: string;
    sort: string;
    sortDirection: 'asc' | 'desc';
    cardColumns: number;
    tabScrollPositions?: Array<{ index: number; gridTop: number; detailTop: number }>;
    splitScroll?: { leftTop: number; rightTop: number };
    stickyNotes?: StickyNoteItem[];
  };

  type CreatorTrackingBookmarkState = {
    version: number;
    tabs: Array<{
      creator: string;
      category?: string;
      creatorFolder?: string;
      billingView: 'subscriptions' | 'purchases';
      archiveScale: 'week' | 'month' | 'year';
    }>;
    activeIndex: number;
    stickyNotes?: StickyNoteItem[];
  };

  type ExplorerSplit = {
    leftTabId: string;
    rightTabId: string;
    rightPath: string;
    rightParentPath: string | null;
    rightEntries: ExplorerEntry[];
    rightSelectedPaths: string[];
    rightIsLoading: boolean;
    rightIsTruncated: boolean;
  };

  type PendingMoveRefresh = {
    sourcePane: 'left' | 'right';
    sourcePath: string;
    destinationPane: 'left' | 'right';
    destinationPath: string;
  };

  type ExplorerContextMenu = {
    entry: ExplorerEntry;
    pane: 'left' | 'right';
    x: number;
    y: number;
  };

  type GidAssignmentConfirmation = {
    pane: 'left' | 'right';
    folderPaths: string[];
    extensions: string[];
  };

  type CreatorFolderConversionConfirmation = {
    pane: 'left' | 'right';
    folderPaths: string[];
  };

  type CreatorReassignmentConfirmation = {
    pane: 'left' | 'right';
    folderPaths: string[];
    sourceCreator: string;
    targetCreator: string;
  };

  type ExplorerBlankContextMenu = {
    pane: 'left' | 'right';
    x: number;
    y: number;
  };

  type ExplorerTabContextMenu = {
    tab: ExplorerTab;
    creator: string;
    creatorFolder: string;
    category: string;
    x: number;
    y: number;
  };

  type PendingExplorerPathsOpen = {
    requestId: string;
    paths: string[];
    requireAll: boolean;
    splitWhenTwo: boolean;
    failureMessage: string;
  };

  type WinRarCompressionRequest = {
    pane: 'left' | 'right';
    folderPaths: string[];
    folderNames: string[];
  };

  type NewTabCandidate = {
    path: string;
    label: string;
    position: number;
    kind: 'folder' | 'separator';
  };

  type ExplorerEntryDrag = {
    paths: string[];
    sourcePane: 'left' | 'right';
    sourcePath: string;
  };

  type ExplorerScrollPosition = {
    gridTop: number;
    detailTop: number;
  };

  type EntryNameParts = {
    displayName: string;
    identifier: string;
    extension: string;
    tagBeforeExtension: boolean;
  };

  let hostStatus = 'Starting';
  let activeView: ActiveView = 'library';
  let galleryNavigationExpanded = true;
  let creatorsNavigationExpanded = true;
  let settingsSection: SettingsSection = 'theme';
  let activeUserGuideSection: UserGuideSectionId = 'overview';
  let settingsAppearanceExpanded = true;
  let settingsBehaviorExpanded = true;
  let settingsFilesExpanded = true;
  let settingsAdvancedExpanded = true;
  let appliedThemeSettings: ThemeSettings = { ...defaultThemeSettings };
  let themeSettingsDraft: ThemeSettings = { ...defaultThemeSettings };
  let themeSaveConfirmOpen = false;
  let themeSaveConfirmElement: HTMLDivElement | null = null;
  let themeSavePending = false;
  let appLanguage: AppLanguage = 'ja';
  let languageSavePending = false;
  let creatorTrackingSettingsDraft = structuredClone(defaultCreatorTrackingSettingsDraft);
  let items: GalleryItem[] = [];
  let isLoading = true;
  let thumbnailStatus = '';
  let generatedThumbnails = 0;
  let query = '';
  let gallerySections: GallerySectionDefinition[] = defaultGallerySections.map(section => ({ ...section }));
  let gallerySectionDrafts: GallerySectionDraft[] = [];
  let gallerySection = defaultGallerySectionId;
  let filterEditorView: 'filter' | 'category' = 'filter';
  let filterEditorAttribute: 'category' | 'title' | 'character' = 'title';
  let filterEditorSearch = '';
  let filterEditorCategoryFilters: string[] = [];
  let filterEditorCategoryFilterMenuOpen = false;
  let filterEditorCategories: FilterEditorCategory[] = [];
  let draggedFilterEditorCategory: FilterEditorCategory | null = null;
  let filterEditorTitles: FilterEditorDefinition[] = [];
  let filterEditorCharacters: FilterEditorDefinition[] = [];
  let filterEditorSelectedCategoryId: number | null = null;
  let filterEditorCategoryDraft = '';
  let filterEditorSelectedId: number | null = null;
  let filterEditorSelectedValue = '';
  let filterEditorCanonicalName = '';
  let filterEditorStandardNameQuery = '';
  let filterEditorStandardNameResults: StandardNameSearchResult[] = [];
  let filterEditorStandardNameSelected = '';
  let filterEditorCategoryName = '';
  let filterEditorParentTitleId: number | null = null;
  let filterEditorParentTitle = '';
  let filterEditorVisibleCategoriesDraft: string[] = [];
  let filterEditorAliases = '';
  let filterEditorMergeCandidates = new Set<number>();
  let filterEditorMergeSearch = '';
  let filterEditorCsvFileName = '';
  let filterEditorCsvHasHeader = false;
  let filterEditorCsvSuggestedHeader = false;
  let filterEditorCsvTotal = 0;
  let filterEditorCsvPreview: FilterCsvPreviewRow[] = [];
  let filterEditorCsvReady = false;
  let filterEditorCsvScope: FilterCsvScope = 'combination';
  let filterEditorCsvAnalysis: FilterCsvImportAnalysis | null = null;
  let filterEditorCsvCaseKey = 'update';
  let filterEditorTitleSort: 'name' | 'characterCount' = 'name';
  let filterEditorTitleSortDirection: 'asc' | 'desc' = 'desc';
  let filterEditorCharacterCounts = new Map<number, number>();
  let filterEditorRegisteredCharacters: FilterEditorDefinition[] = [];
  let allocationSortColumn = 'title';
  let allocationSortDirection: 'asc' | 'desc' = 'asc';
  let allocationExpandedCategories: Record<string, boolean> = {};
  let allocationRows: AllocationRow[] = [];
  let allocationGroups: AllocationCategoryGroup[] = [];
  let allocationVirtualItems: AllocationVirtualItem[] = [];
  let allocationVisibleRows: AllocationRow[] = [];
  let allocationScrollElement: HTMLDivElement | null = null;
  let allocationSelectedIds = new Set<number>();
  let allocationSelectionAnchorId: number | null = null;
  let allocationRowIndexById = new Map<number, number>();
  let pendingFilterEditorNavigation: PendingFilterEditorNavigation | null = null;
  let filterEditorNavigationCommitInProgress = false;
  let pendingFilterEditorDefinitionDeletion: FilterEditorDefinition | null = null;
  let tagManagementTags: TagManagementDefinition[] = [];
  let tagManagementSearch = '';
  let tagManagementSelectedId: number | null = null;
  let tagManagementDraft = '';
  let draggedTagManagementId: number | null = null;
  let tagManagementDropTargetId: number | null = null;
  let tagManagementSortColumn = 'tag';
  let tagManagementSortDirection: 'asc' | 'desc' = 'asc';
  let tagManagementSelectedIds = new Set<number>();
  let tagManagementSelectionAnchorId: number | null = null;
  let tagManagementRowIndexById = new Map<number, number>();
  let tagManagementCsvFileName = '';
  let tagManagementCsvHasHeader = false;
  let tagManagementCsvTotal = 0;
  let tagManagementCsvPreview: TagManagementImportRow[] = [];
  let tagManagementCsvReady = false;

  const allocationVirtualizer = createVirtualizer<HTMLDivElement, HTMLDivElement>({
    count: 0,
    getScrollElement: () => allocationScrollElement,
    estimateSize: () => 36,
    overscan: 12
  });
  let searchEngineSettings: SearchEngineSettings = {
    provider: 'google',
    googleSearchUrlTemplate: 'https://www.google.com/search?q={query}',
    braveApiKey: '',
    geminiApiKey: ''
  };
  let galleryWorks: GalleryWork[] = [];
  let galleryCreatorSummaries: GalleryCreatorSummary[] = [];
  let galleryCreatorSummarySection = defaultGallerySectionId;
  let galleryCreatorSummaryRatings: string[] = [];
  let galleryCreatorSummaryCoreTitles: string[] = [];
  let galleryCreatorSummaryCoreTags: string[] = [];
  let galleryCreatorSummarySites: string[] = [];
  let galleryCreatorSummaryOverallRatings: number[] = [];
  let galleryCreatorSummaryMetricScores = Object.fromEntries(
    creatorTrackingMetricDefinitions.map((metric) => [metric.key, [] as number[]])) as Record<CreatorTrackingMetricKey, number[]>;
  let galleryCreatorSummaryReminderFilter: GalleryCreatorSummaryReminderFilter = '';
  let galleryCreatorSummaryQuery = '';
  let galleryCreatorSummaryCoreTitlesExpanded = false;
  let galleryCreatorSummaryCoreTagsExpanded = false;
  let galleryCreatorSummaryMoreExpanded = false;
  let galleryCreatorSummarySorts: GalleryCreatorSummarySortCriterion[] = [{ key: 'rating', direction: 'desc' }];
  let galleryCreatorSummaryIsLoading = false;
  let galleryCreatorSummaryError = '';
  let galleryCreatorSummaryRequestId = '';
  let nextGalleryCreatorSummaryRequestId = 1;
  let userMetricsCategory = defaultGallerySectionId;
  let userMetricsDashboard: UserMetricsDashboard | null = null;
  let userMetricsIsLoading = false;
  let userMetricsError = '';
  let userMetricsRequestId = '';
  let nextUserMetricsRequestId = 1;
  let calendarSettings: CalendarSettings = { weekStartDay: 0 };
  let calendarWeekStartDraft = 0;
  let calendarEvents: CalendarSubscriptionEvent[] = [];
  let calendarIsLoading = false;
  let calendarError = '';
  let calendarRequestId = '';
  let calendarIcsRequestId = '';
  let nextCalendarRequestId = 1;
  let calendarMonthCursor = toLocalDateInputValue(new Date()).slice(0, 7);
  let calendarViewMode: CalendarViewMode = 'month';
  let googleCalendarAutoSyncEnabled = false;
  let googleCalendarSyncFeatureEnabled = false;
  let googleCalendarClientId = '';
  let googleCalendarClientSecretDraft = '';
  let googleCalendarHasClientSecret = false;
  let googleCalendarHasRefreshToken = false;
  let googleCalendarId = 'primary';
  let googleCalendarRedirectUri = '';
  let googleCalendarLastSyncedAt = '';
  let googleCalendarLastSyncError = '';
  let googleCalendarBusy = false;
  let googleCalendarStatus = '';
  let userMetricsFileEntity: UserMetricsEntity = 'creators';
  let userMetricsImageEntity: UserMetricsEntity = 'creators';
  let userMetricsRatingEntity: UserMetricsEntity = 'creators';
  let userMetricsMetricKey = 'overall';
  let creatorTrackingSummary: GalleryCreatorSummary | null = null;
  let creatorTracking: CreatorTracking | null = null;
  let creatorTrackingDashboardContext: CreatorTrackingDashboardContext | null = null;
  let creatorTrackingDashboard: CreatorTrackingDashboard | null = null;
  let creatorTrackingIsLoading = false;
  let creatorTrackingIsSaving = false;
  let creatorTrackingDirty = false;
  let creatorTrackingError = '';
  let creatorTrackingBillingView: 'subscriptions' | 'purchases' = 'subscriptions';
  let creatorTrackingArchiveScale: 'week' | 'month' | 'year' = 'month';
  let creatorTrackingRequestId = '';
  let nextCreatorTrackingRequestId = 1;
  let creatorTrackingRefreshRequestIds = new Set<string>();
  let creatorTrackingTabs: CreatorTrackingTab[] = [];
  let activeCreatorTrackingTabId = '';
  let creatorTrackingNewDialogOpen = false;
  let creatorTrackingNewCreator = '';
  let creatorTrackingNewCategory = galleryCreatorSummarySection;
  let creatorTrackingDeleteStep: 0 | 1 | 2 = 0;
  let creatorTrackingDeleteTarget: { creator: string; label: string } | null = null;
  let creatorTrackingDeleteRequestId = '';
  let creatorTrackingDeleteInProgress = false;
  let creatorTrackingArchiveScrollElement: HTMLDivElement | null = null;
  let draggedCreatorTrackingActivityLinkIndex: number | null = null;
  let draggedCreatorTrackingBillingRow: { view: 'subscriptions' | 'purchases'; index: number } | null = null;
  let creatorTrackingIconFetchRequests: Record<string, number> = {};
  let creatorTrackingCompositionRefreshPending = false;
  let nextCreatorTrackingIconFetchRequestId = 1;
  let galleryTotal = 0;
  let galleryRatings: GalleryFilterOption[] = [];
  let galleryRatingCounts = new Map<number, number>();
  let galleryFilterSnapshotSections = new Set<string>();
  let galleryFilterCommitInProgress = false;
  let galleryFilterConfigurationDirty = false;
  let galleryTags: GalleryFilterOption[] = [];
  let galleryCreators: GalleryFilterOption[] = [];
  let galleryTitles: GalleryFilterOption[] = [];
  let galleryCharacters: GalleryFilterOption[] = [];
  const galleryRatingValues = [6, 5, 4, 3, 2, 1, 0];
  let galleryRatingFilters: number[] = [];
  let galleryTagFilters: string[] = [];
  let galleryCreatorFilters: string[] = [];
  let galleryTitleFilters: string[] = [];
  let galleryCharacterFilters: string[] = [];
  let galleryQuery = '';
  let galleryCreatorFiltersExpanded = false;
  let galleryTitleFiltersExpanded = false;
  let galleryCharacterFiltersExpanded = false;
  let galleryTagFiltersExpanded = false;
  let galleryCollapsePins: Record<GalleryExpandableFilterKind, boolean> = {
    creator: false,
    title: false,
    character: false,
    tag: false
  };
  let galleryCreatorFilterButtonsElement: HTMLDivElement | null = null;
  let galleryTitleFilterButtonsElement: HTMLDivElement | null = null;
  let galleryCharacterFilterButtonsElement: HTMLDivElement | null = null;
  let galleryTagFilterButtonsElement: HTMLDivElement | null = null;
  let galleryCreatorHasHiddenFilters = false;
  let galleryTitleHasHiddenFilters = false;
  let galleryCharacterHasHiddenFilters = false;
  let galleryTagHasHiddenFilters = false;
  let galleryPromotedCreators: string[] = [];
  let galleryPromotedTitles: string[] = [];
  let galleryPromotedCharacters: string[] = [];
  let galleryPromotedTags: string[] = [];
  let galleryTagAssignment: GalleryTagAssignment | null = null;
  let visibleGalleryTagAssignmentCreatorTitleTags: GalleryTagAssignmentOption[] = [];
  let visibleGalleryTagAssignmentAvailableTags: GalleryTagAssignmentOption[] = [];
  let galleryTagAssignmentCreatorFilterCache: {
    source: GalleryTagAssignmentOption[];
    query: string;
    result: GalleryTagAssignmentOption[];
  } | null = null;
  let galleryTagAssignmentAvailableFilterCache: {
    source: GalleryTagAssignmentOption[];
    query: string;
    result: GalleryTagAssignmentOption[];
  } | null = null;
  let galleryTagAssignmentReturnPending = false;
  let galleryTitleAssignment: GalleryTitleAssignment | null = null;
  let visibleGalleryTitleAssignmentCreatorTitles: GalleryTitleAssignmentOption[] = [];
  let visibleGalleryTitleAssignmentAvailableTitles: GalleryTitleAssignmentOption[] = [];
  let visibleGalleryTitleAssignmentCreatorTitleCharacters: GalleryCharacterAssignmentOption[] = [];
  let visibleGalleryTitleAssignmentAvailableCharacters: GalleryCharacterAssignmentOption[] = [];
  let galleryTitleAssignmentCategoryOptions: string[] = [];
  let galleryTitleAssignmentCreatorFilterCache: {
    source: GalleryTitleAssignmentOption[];
    query: string;
    result: GalleryTitleAssignmentOption[];
  } | null = null;
  let galleryTitleAssignmentAvailableFilterCache: {
    source: GalleryTitleAssignmentOption[];
    query: string;
    categoryFilter: string;
    result: GalleryTitleAssignmentOption[];
  } | null = null;
  const galleryTitleAssignmentCategoryCache = new WeakMap<GalleryTitleAssignmentOption[], string[]>();
  let galleryTitleAssignmentCharacterCreatorFilterCache: {
    source: GalleryCharacterAssignmentOption[];
    query: string;
    result: GalleryCharacterAssignmentOption[];
  } | null = null;
  let galleryTitleAssignmentCharacterAvailableFilterCache: {
    source: GalleryCharacterAssignmentOption[];
    query: string;
    result: GalleryCharacterAssignmentOption[];
  } | null = null;
  let galleryCharacterAssignment: GalleryCharacterAssignment | null = null;
  let visibleGalleryCharacterAssignmentCreatorTitleCharacters: GalleryCharacterAssignmentOption[] = [];
  let visibleGalleryCharacterAssignmentAvailableCharacters: GalleryCharacterAssignmentOption[] = [];
  let galleryCharacterAssignmentCreatorFilterCache: {
    source: GalleryCharacterAssignmentOption[];
    query: string;
    result: GalleryCharacterAssignmentOption[];
  } | null = null;
  let galleryCharacterAssignmentAvailableFilterCache: {
    source: GalleryCharacterAssignmentOption[];
    query: string;
    result: GalleryCharacterAssignmentOption[];
  } | null = null;
  let galleryTitleAssignmentReturnPending = false;
  let pendingGalleryTitleAssignmentCategory = '';
  let pendingGalleryTitleAssignmentSelectedTitleId: number | null = null;
  let pendingGalleryTitleAssignmentSelectedCharacterId: number | null = null;
  let galleryCharacterAssignmentReturnPending = false;
  let pendingGalleryCharacterAssignmentCategory = '';
  let pendingGalleryCharacterAssignmentSelectedId: number | null = null;
  let galleryContextMenu: GalleryContextMenu | null = null;
  let galleryContextTargetWork: GalleryWork | null = null;
  let galleryReverseFilterRequest: GalleryReverseFilterRequest | null = null;
  let galleryDeleteConfirmation = false;
  let galleryDeleteInProgress = false;
  let gallerySingleClickLaunches: Record<string, number> = {};
  let nextGalleryTagAssignmentRequestId = 1;
  let nextGalleryTitleAssignmentRequestId = 1;
  let nextGalleryTitleAssignmentCharacterRequestId = 1;
  let nextGalleryCharacterAssignmentRequestId = 1;
  let nextGalleryReverseFilterRequestId = 1;
  let galleryPinnedCreators: string[] = [];
  let galleryPinnedTitles: string[] = [];
  let galleryContentElement: HTMLElement | null = null;
  let pendingGalleryBookmarkScrollTop: number | null = null;
  let pendingGalleryBookmarkLoadedCount = 0;
  let selectedGalleryWorkIds = new Set<string>();
  let gallerySelectionAnchorId = '';
  let pendingGalleryRatingIds = new Set<string>();
  let pendingGalleryRatingDeltas: Record<string, 1 | -1> = {};
  let galleryRatingLitIds = new Set<string>();
  let galleryRatingEffects: Record<string, 'increase' | 'decrease'> = {};
  let galleryCardColumnModes: Record<string, number> = {
    gallery: 7
  };
  let galleryCardColumns = 7;
  let lastGalleryCardWheelChangeAt = 0;
  let galleryFilterSorts: GalleryFilterSortCriterion[] = [];
  let galleryThumbnailSorts: GalleryThumbnailSortCriterion[] = [];
  let galleryIsLoading = false;
  let galleryRequestId = '';
  let nextGalleryRequestId = 1;
  const requestedGalleryThumbnailIds = new Set<string>();
  const unavailableGalleryThumbnailIds = new Set<string>();
  const pendingGalleryThumbnailRequests = new Map<string, { id: string; path: string; category: string }>();
  const galleryThumbnailObserverCallbacks = new WeakMap<Element, () => void>();
  const galleryThumbnailRequestBatchSize = 12;
  let galleryThumbnailObserver: IntersectionObserver | null = null;
  let galleryThumbnailFlushFrame = 0;
  let galleryThumbnails: Record<string, string> = {};
  let galleryThumbnailRevision = 0;
  let galleryScanTargets: GalleryScanTarget[] = [];
  let galleryScanSettings: GalleryScanSettings[] = [];
  let galleryTargetCategory = defaultGallerySectionId;
  let galleryTargetDraft = '';
  let galleryTargetExtensions = '';
  let galleryTargetCardAspect: 'portrait' | 'landscape' = 'portrait';
  let galleryTargetEnabledFilters: string[] = ['rating', 'creator', 'title', 'character', 'tag', 'core_title', 'core_tags'];
  let galleryTargetFileNameLines = 3;
  let galleryTargetCreatorLabel = 'Creator';
  let galleryTargetTitleLabel = 'Title';
  let galleryTargetCharacterLabel = 'Character';
  let galleryTargetTagLabel = 'Tag';
  let galleryTargetCoreTitleLabel = 'Core title';
  let galleryTargetCoreTagsLabel = 'Core tags';
  let thumbnailCropAdjustments: ThumbnailCropAdjustment[] = [];
  let thumbnailAdjustmentCategory = defaultGallerySectionId;
  let thumbnailAdjustmentHorizontalOffset = 0;
  let thumbnailAdjustmentVerticalOffset = -10;
  let thumbnailAdjustmentScale = 100;
  let externalAppRules: ExternalAppRule[] = [];
  let winRarSettings: WinRarSettings = {
    executablePath: '',
    supportedExtensions: 'zip,rar,7z,cbz,cbr',
    showOpenInContextMenu: false
  };
  let ffmpegSettings: FfmpegSettings = {
    executablePath: '',
    supportedExtensions: 'mp4,mkv,avi,mov,wmv,webm,flv,m4v,mpeg,mpg,ts'
  };
  let gidSettings: GidSettings = {
    targetExtensions: 'zip;rar;7z;cbz;cbr',
    digitCount: 6
  };
  let gidMigrationTargetDigitCount = 6;
  let gidMigrationPreview: GidMigrationPreview | null = null;
  let gidMigrationPreviewInProgress = false;
  let gidMigrationInProgress = false;
  let gidMigrationProgress = '';
  let gidMigrationProgressPhase = '';
  let gidMigrationPhaseCompleted = 0;
  let gidMigrationPhaseTotal = 0;
  let gidMigrationOverallPercent = 0;
  let gidMigrationDatabasePercent = 0;
  let gidMigrationElapsedSeconds = 0;
  let gidMigrationEstimatedRemainingSeconds: number | null = null;
  let gidMigrationProgressStartedAt = 0;
  let gidMigrationProgressTimer: ReturnType<typeof setInterval> | undefined;
  $: gidMigrationEstimatedRemainingSeconds = gidMigrationOverallPercent >= 1 && gidMigrationOverallPercent < 100
    ? Math.max(0, Math.round(gidMigrationElapsedSeconds * (100 - gidMigrationOverallPercent) / gidMigrationOverallPercent))
    : null;
  let ffmpegAvailable = false;
  let thumbnailCacheTargets: string[] = [];
  let thumbnailCacheTargetDraft = '';
  let thumbnailCacheRoot = '';
  let thumbnailCacheRootDraft = '';
  let thumbnailCacheStatus = '';
  let sqliteDatabasePath = '';
  let sqliteDatabasePathDraft = '';
  let sqliteCacheDatabasePath = '';
  let sqliteConfiguredCacheDatabasePath = '';
  let sqliteCacheDatabasePathDraft = '';
  let sqliteCacheDatabaseRestartRequired = false;
  let sqliteDatabaseStatus = '';
  let sqliteDatabaseProgressLog: string[] = [];
  let sqliteDatabaseBusy = false;
  let sqliteDatabaseUpdateInProgress = false;
  let sqliteDatabaseCancelRequested = false;
  let sqliteDatabaseUpdateCategories: string[] = [];
  let databaseScanSchedules: DatabaseScanSchedule[] = [];
  let databaseScanScheduleSaving = false;
  let databaseScheduledScanInProgress = false;
  let databaseScheduledScanStartedAt = 0;
  let databaseScheduledScanEstimatedSeconds: number | null = null;
  let databaseScheduledScanBaseMessage = '';
  let databaseScheduledScanToastTimer: ReturnType<typeof setInterval> | undefined;
  let sqliteMergeDatabasePath = '';
  let sqliteMergeCanonical: 'current' | 'selected' = 'current';
  let pCloudApiHost = 'eapi.pcloud.com';
  let pCloudTargetFolder = '';
  let pCloudClientId = '';
  let pCloudAccessTokenDraft = '';
  let pCloudOAuthRedirectUri = '';
  let pCloudHasAccessToken = false;
  let pCloudAccountEmail = '';
  let pCloudUsedQuotaBytes: number | null = null;
  let pCloudQuotaBytes: number | null = null;
  let pCloudLastBackupAt = '';
  let pCloudLastBackupFileName = '';
  let pCloudAutoBackupEnabled = false;
  let pCloudCheckIntervalMinutes = 60;
  let pCloudBackupIntervalDays = 1;
  let pCloudMaximumSnapshots = 7;
  let pCloudIdleThresholdMinutes = 10;
  let pCloudSnapshots: PCloudSnapshot[] = [];
  let pCloudSnapshotsLoaded = false;
  let pCloudBusy = false;
  let pCloudStatus = '';
  let pCloudAutoBackupToastMessage = '';
  let draggedProgramRule: ExternalAppRule | null = null;
  let programId: number | null = null;
  let programName = '';
  let programExecutablePath = '';
  let programLaunchOptions = '"{path}"';
  let programAllowMultiple = false;
  let programClickExtensions = 'zip';
  let programDoubleClickExtensions = '';
  let programContextMenuExtensions = '';
  let explorerSingleClickLaunches: Record<string, number> = {};
  let newTabCandidates: NewTabCandidate[] = [];
  let newTabCandidatePath = '';
  let newTabCandidateLabel = '';
  let selectedNewTabCandidatePath = '';
  let draggedNewTabCandidate: NewTabCandidate | null = null;
  let explorerEntries: ExplorerEntry[] = [];
  let explorerRoots: string[] = [];
  let explorerPath = '';
  let explorerParentPath: string | null = null;
  let explorerPathDraft = '';
  let explorerPathEditing = false;
  let explorerQuery = '';
  let splitExplorerQuery = '';
  let explorerCardColumns = 5;
  let explorerThumbnailPriority = 0;
  let explorerSort = 'name';
  let explorerSortDirection: 'asc' | 'desc' = 'asc';
  let explorerIsLoading = false;
  let explorerIsTruncated = false;
  let explorerToastMessage = '';
  let explorerToastKind: 'success' | 'error' | 'progress' = 'success';
  let explorerToastAction: 'cancelDatabaseScan' | null = null;
  let explorerToastTimer: ReturnType<typeof setTimeout> | undefined;
  let explorerPasteInProgress: { pane: 'left' | 'right'; path: string } | null = null;
  let winRarProgressLabel = '';
  let winRarProgressSeconds = 0;
  let winRarProgressTimer: ReturnType<typeof setInterval> | undefined;
  let rarToZipBatchInProgress = false;
  let selectedPaths: string[] = [];
  let explorerSelectionAnchorPath = '';
  let splitSelectionAnchorPath = '';
  let renamingEntry: ExplorerEntry | null = null;
  let renameValue = '';
  let renameIdentifier = '';
  let renameExtension = '';
  let renameTagBeforeExtension = false;
  let deleteConfirmation = false;
  let splitRenamingEntry: ExplorerEntry | null = null;
  let splitRenameValue = '';
  let splitRenameIdentifier = '';
  let splitRenameExtension = '';
  let splitRenameTagBeforeExtension = false;
  let pendingRenameNavigation: { pane: 'left' | 'right'; targetPath: string } | null = null;
  let splitDeleteConfirmation = false;
  let explorerTabs: ExplorerTab[] = [];
  let activeExplorerTabId = '';
  let nextExplorerTabId = 1;
  let explorerTabsRestored = false;
  let explorerSplit: ExplorerSplit | null = null;
  let splitFocusedPane: 'left' | 'right' = 'left';
  let cutClipboardSource: { pane: 'left' | 'right'; path: string } | null = null;
  let pendingMoveRefresh: PendingMoveRefresh | null = null;
  let explorerBookmarks: ExplorerBookmark[] = [];
  let viewBookmarks: ViewBookmark[] = [];
  let stickyNotes: StickyNoteItem[] = [];
  let stickyNoteInteraction: StickyNoteInteraction | null = null;
  let stickyNotePaletteOpenId: number | null = null;
  const stickyNoteSaveTimers = new Map<number, ReturnType<typeof setTimeout>>();
  let stickyNoteBoardItems: StickyNoteBoardItem[] = [];
  let stickyNoteBoardMode: 'all' | 'grouped' = 'all';
  let stickyNoteBoardIsLoading = false;
  let stickyNoteBoardError = '';
  let markdownEditingNoteIds = new Set<number>();
  const stickyNoteBoardSaveTimers = new Map<number, ReturnType<typeof setTimeout>>();
  let bookmarkCapture: BookmarkCapture | null = null;
  let bookmarkNameDraft = '';
  let bookmarkSaveDialogOpen = false;
  let bookmarkDeleteCandidate: ViewBookmark | null = null;
  let bookmarkMutationMessage = '';
  let bookmarkRestoreWarnings: string[] = [];
  let navigationBackStack: NavigationHistoryEntry[] = [];
  let navigationForwardStack: NavigationHistoryEntry[] = [];
  let navigationHistoryRestoring = false;
  let globalSearchOpen = false;
  let globalSearchMode: GlobalSearchMode = 'normal';
  let globalSearchQuery = '';
  let globalSearchInputElement: HTMLInputElement | null = null;
  let pendingGalleryBookmarkRestore: { bookmark: ViewBookmark; state: GalleryBookmarkState; requestId: string } | null = null;
  let pendingExplorerBookmarkRestore: { bookmark: ViewBookmark; state: ExplorerBookmarkState; requestId: string } | null = null;
  let pendingCreatorSummaryBookmarkRestore: { bookmark: ViewBookmark; state: CreatorSummaryBookmarkState } | null = null;
  let pendingCreatorTrackingBookmarkRestore: { bookmark: ViewBookmark; state: CreatorTrackingBookmarkState } | null = null;
  let pendingCreatorTrackingSessionRestore: { state: CreatorTrackingBookmarkState; activate: boolean } | null = null;
  let pendingGalleryCreatorTracking: { creator: string; category: string; creatorFolder?: string } | null = null;
  let pendingGalleryCreatorStorageRequest: { requestId: string; creator: string } | null = null;
  let nextViewBookmarkRestoreRequestId = 1;
  let explorerBookmarksExpanded = false;
  let draggedExplorerTab: ExplorerTab | null = null;
  let draggedExplorerBookmark: ExplorerBookmark | null = null;
  let explorerGestureStart: { x: number; y: number } | null = null;
  let suppressExplorerContextMenu = false;
  let splitGestureStart: { x: number; y: number } | null = null;
  let suppressSplitContextMenu = false;
  let gestureTrail: Array<{ x: number; y: number }> = [];
  let explorerContextMenu: ExplorerContextMenu | null = null;
  let explorerBlankContextMenu: ExplorerBlankContextMenu | null = null;
  let explorerTabContextMenu: ExplorerTabContextMenu | null = null;
  let pendingExplorerPathsOpen: PendingExplorerPathsOpen | null = null;
  let nextExplorerPathsRequestId = 1;
  let explorerNewTabMenuOpen = false;
  let explorerDriveMenuOpen = false;
  let explorerDriveMenuPosition = { x: 0, y: 0 };
  let draggedExplorerEntries: ExplorerEntryDrag | null = null;
  let explorerDropTargetPath = '';
  let explorerThumbnailSubmenuOpen = false;
  let visibleGalleryWorkStickyContextKeys = new Set<string>();
  $: activeStickyNoteContextKey = activeView === 'library'
    ? normalizeStickyNoteContextKey('library', gallerySection)
    : activeView === 'creators'
      ? normalizeStickyNoteContextKey('creators', galleryCreatorSummarySection)
      : activeView === 'explorer'
        ? normalizeStickyNoteContextKey('explorer', explorerSplit && splitFocusedPane === 'right' ? explorerSplit.rightPath : explorerPath)
        : activeView === 'creatorTracking'
          ? normalizeStickyNoteContextKey(
              'creatorTracking',
              creatorTrackingTabs.find((tab) => tab.id === activeCreatorTrackingTabId)?.creator
                ?? creatorTracking?.creator
                ?? '')
          : activeView === 'userMetrics'
            ? normalizeStickyNoteContextKey('userMetrics', userMetricsCategory)
            : '';
  $: visibleGalleryWorkStickyContextKeys = new Set(
    visibleGalleryWorks.map((work) => getGalleryWorkStickyNoteContextKey(gallerySection, work.id)));
  $: visibleStickyNotes = stickyNotes.filter((note) =>
    note.viewType === activeView && (
      note.contextKey === activeStickyNoteContextKey ||
      (activeView === 'library' && visibleGalleryWorkStickyContextKeys.has(note.contextKey))));
  $: sortedStickyNoteBoardItems = [...stickyNoteBoardItems].sort(compareStickyNoteBoardItems);
  $: stickyNoteBoardGroups = getStickyNoteBoardGroups(sortedStickyNoteBoardItems);
  $: stickyNoteBoardDisplayGroups = stickyNoteBoardMode === 'grouped'
    ? stickyNoteBoardGroups
    : [{ viewType: null, label: '', items: sortedStickyNoteBoardItems }];
  let explorerCompressionSubmenuOpen = false;
  let explorerFolderCreateSubmenuOpen = false;
  let explorerDbManagementSubmenuOpen = false;
  let explorerGidSubmenuOpen = false;
  let gidAssignmentConfirmation: GidAssignmentConfirmation | null = null;
  let gidAssignmentInProgress = false;
  let creatorFolderConversionConfirmation: CreatorFolderConversionConfirmation | null = null;
  let creatorFolderConversionInProgress = false;
  let creatorReassignmentConfirmation: CreatorReassignmentConfirmation | null = null;
  let creatorReassignmentInProgress = false;
  let pendingWinRarIndividualCompression: WinRarCompressionRequest | null = null;
  let pendingWinRarPackageCompression: WinRarCompressionRequest | null = null;
  let winRarPackageName = '';
  let explorerColumnMenu: { x: number; y: number } | null = null;
  let explorerDetailColumns: ExplorerDetailColumnId[] = [...defaultExplorerDetailColumns];
  let draggedExplorerDetailColumn: ExplorerDetailColumnId | null = null;
  let mouseGestureSettings: MouseGestureSettings = structuredClone(defaultMouseGestureSettings);
  let keyboardShortcutSettings: KeyboardShortcutSettings = structuredClone(defaultKeyboardShortcutSettings);
  let newGestureDirection = '↑';
  let newGestureCommand: MouseGestureCommand = 'parent';
  let explorerHistoryPaths: string[] = [];
  let explorerHistoryIndex = -1;
  let splitExplorerHistoryPaths: string[] = [];
  let splitExplorerHistoryIndex = -1;
  let explorerGridPaneElement: HTMLDivElement | null = null;
  let explorerDetailPaneElement: HTMLDivElement | null = null;
  let explorerSplitLeftPaneElement: HTMLDivElement | null = null;
  let explorerSplitRightPaneElement: HTMLDivElement | null = null;
  let explorerFilterInputElement: HTMLInputElement | null = null;
  let gallerySearchInputElement: HTMLInputElement | null = null;
  let galleryCreatorSummarySearchInputElement: HTMLInputElement | null = null;
  let explorerPathInputElement: HTMLInputElement | null = null;
  let explorerTabScrollPositions: Record<string, ExplorerScrollPosition> = {};
  let pendingExplorerScrollRestoreTabId: string | null = null;
  let pendingExplorerSplitScroll: { leftTop: number; rightTop: number } | null = null;
  const requestedThumbnailIds = new Set<number>();
  const requestedExplorerThumbnailPaths = new Set<string>();
  const unavailableExplorerThumbnailPaths = new Set<string>();
  let explorerThumbnails: Record<string, string> = {};

  $: filteredItems = filterItems(items, query);
  $: galleryRatingCounts = new Map(galleryRatings.map((option) => [Number(option.value), option.count]));
  $: visibleGalleryWorks = filterGalleryWorks(galleryWorks, galleryQuery);
  $: creatorTrackingSettingsWeightTotal = creatorTrackingSettingsDraft.metrics.reduce((total, metric) => total + Number(metric.weightPercent || 0), 0);
  $: selectedGalleryScanTargets = galleryScanTargets.filter((target) => target.category === galleryTargetCategory);
  $: filteredExplorerEntries = sortExplorerEntries(
    filterExplorerEntries(explorerEntries, explorerQuery),
    explorerSort,
    explorerSortDirection
  );
  $: selectedExplorerEntries = explorerEntries.filter((entry) => selectedPaths.includes(entry.path));
  $: gidTargetExtensions = parseGidTargetExtensions(gidSettings.targetExtensions);
  $: splitRightEntries = explorerSplit
    ? sortExplorerEntries(
        filterExplorerEntries(explorerSplit.rightEntries, splitExplorerQuery),
        explorerSort,
        explorerSortDirection
      )
    : [];
  $: splitLeftTab = explorerSplit
    ? explorerTabs.find((tab) => tab.id === explorerSplit?.leftTabId)
    : undefined;
  $: splitRightTab = explorerSplit
    ? explorerTabs.find((tab) => tab.id === explorerSplit?.rightTabId)
    : undefined;
  $: gestureTrailPoints = gestureTrail.map((point) => `${point.x},${point.y}`).join(' ');
  $: activeExplorerQuery = explorerSplit && splitFocusedPane === 'right'
    ? splitExplorerQuery
    : explorerQuery;
  $: explorerFilterPlaceholder = explorerSplit && splitFocusedPane === 'right'
    ? '右側のフォルダを検索'
    : '現在のフォルダを検索';
  $: explorerCardRowHeight = ({ 4: 325, 5: 265, 6: 222, 7: 202 } as Record<number, number>)[explorerCardColumns];
  // Each mode names a fixed card width derived from the 1800px initial window.
  // CSS auto-fill adds columns when more room is available instead of stretching cards.
  $: explorerCardWidth = ({ 4: 390, 5: 280, 6: 235, 7: 200 } as Record<number, number>)[explorerCardColumns];
  $: explorerGridStyle = `--explorer-card-width: ${explorerCardWidth}px; --explorer-card-row-height: ${explorerCardRowHeight}px;`;
  $: galleryCardColumns = galleryCardColumnModes[gallerySection] ?? 7;
  $: filterEditorDefinitions = filterEditorAttribute === 'title' ? filterEditorTitles : filterEditorCharacters;
  $: filterEditorCharacterCounts = getFilterEditorCharacterCounts(filterEditorCharacters);
  $: filterEditorOptions = getFilterEditorOptions(
    filterEditorDefinitions,
    filterEditorSearch,
    filterEditorCategoryFilters,
    filterEditorCharacterCounts,
    filterEditorAttribute === 'title',
    filterEditorTitleSort,
    filterEditorTitleSortDirection
  );
  $: filterEditorCategoryGroups = groupFilterEditorOptions(filterEditorOptions);
  $: filterEditorMergeOptions = getFilterEditorMergeOptions(filterEditorOptions, filterEditorSelectedId, filterEditorSelectedValue, filterEditorMergeSearch);
  $: filterEditorParentTitleOptions = filterEditorTitles.filter((title) => title.categoryName === (filterEditorCategoryName || '未分類'));
  $: filterEditorCategoryOptions = filterEditorCategories.filter((category) => category.name.toLocaleLowerCase('ja-JP').includes(filterEditorSearch.toLocaleLowerCase('ja-JP')));
  $: filterEditorCategoryFilterOptions = getFilterEditorCategoryFilterOptions(filterEditorDefinitions, filterEditorCategories);
  $: filterEditorRegisteredCharacters = filterEditorAttribute === 'title' && filterEditorSelectedId !== null
    ? filterEditorCharacters
      .filter((character) => character.parentTitleId === filterEditorSelectedId)
      .sort((left, right) => left.canonicalName.localeCompare(right.canonicalName, 'ja-JP'))
    : [];
  $: allocationSections = gallerySections.filter((section) => isGalleryFilterEnabled('title', section.id));
  $: allocationRows = getAllocationRows(filterEditorSearch, filterEditorTitles);
  $: allocationGroups = groupAllocationRows(allocationRows, allocationSortColumn, allocationSortDirection);
  $: allocationVirtualItems = getAllocationVirtualItems(allocationGroups, allocationExpandedCategories);
  $: allocationVisibleRows = allocationVirtualItems
    .filter((item): item is Extract<AllocationVirtualItem, { kind: 'title' }> => item.kind === 'title')
    .map((item) => item.row);
  $: allocationRowIndexById = new Map(allocationVisibleRows.map((row, index) => [row.id, index]));
  $: tagManagementListTags = getTagManagementListRows(tagManagementTags, tagManagementSearch);
  $: tagManagementVisibleTags = getTagManagementRows(
    tagManagementTags,
    tagManagementSearch,
    tagManagementSortColumn,
    tagManagementSortDirection
  );
  $: tagManagementRowIndexById = new Map(tagManagementVisibleTags.map((tag, index) => [tag.id, index]));
  $: {
    const scrollElement = allocationScrollElement;
    get(allocationVirtualizer).setOptions({
      count: allocationVirtualItems.length,
      getScrollElement: () => scrollElement
    });
  }
  $: galleryCardLayout = ({
    5: { height: 560, body: 150, titleLines: 3, titleLineHeight: 20, titleSize: '0.95rem', metaSize: '0.8rem', subSize: '0.75rem' },
    6: { height: 470, body: 136, titleLines: 3, titleLineHeight: 20, titleSize: '0.95rem', metaSize: '0.8rem', subSize: '0.75rem' },
    7: { height: 400, body: 126, titleLines: 3, titleLineHeight: 20, titleSize: '0.95rem', metaSize: '0.8rem', subSize: '0.75rem' },
    8: { height: 350, body: 116, titleLines: 2, titleLineHeight: 20, titleSize: '0.95rem', metaSize: 'calc(0.8rem - 1px)', subSize: '0.75rem' },
    9: { height: 315, body: 108, titleLines: 2, titleLineHeight: 18, titleSize: '0.87rem', metaSize: 'calc(0.8rem - 1px)', subSize: 'calc(0.75rem - 1px)' }
  } as Record<number, { height: number; body: number; titleLines: number; titleLineHeight: number; titleSize: string; metaSize: string; subSize: string }>)[galleryCardColumns];
  $: galleryCardAspect = getGalleryCardAspect(gallerySection);
  $: visibleGalleryTitleAssignmentCreatorTitles = galleryTitleAssignment
    ? getGalleryTitleAssignmentCreatorTitles(galleryTitleAssignment)
    : [];
  $: visibleGalleryTitleAssignmentAvailableTitles = galleryTitleAssignment
    ? getGalleryTitleAssignmentAvailableTitles(galleryTitleAssignment)
    : [];
  $: visibleGalleryTitleAssignmentCreatorTitleCharacters = galleryTitleAssignment
    ? getGalleryTitleAssignmentCreatorTitleCharacters(galleryTitleAssignment)
    : [];
  $: visibleGalleryTitleAssignmentAvailableCharacters = galleryTitleAssignment
    ? getGalleryTitleAssignmentAvailableCharacters(galleryTitleAssignment)
    : [];
  $: selectedGalleryTitleAssignmentTitleName = galleryTitleAssignment?.selectedTitleId
    ? ([...galleryTitleAssignment.creatorTitles, ...galleryTitleAssignment.availableTitles, ...galleryTitleAssignment.commonAssignedTitles]
      .find((option) => option.id === galleryTitleAssignment?.selectedTitleId)?.title ?? '')
    : '';
  $: galleryTitleAssignmentCategoryOptions = galleryTitleAssignment
    ? getGalleryTitleAssignmentCategories(galleryTitleAssignment)
    : [];
  $: visibleGalleryCharacterAssignmentCreatorTitleCharacters = galleryCharacterAssignment
    ? getGalleryCharacterAssignmentCreatorTitleCharacters(galleryCharacterAssignment)
    : [];
  $: visibleGalleryCharacterAssignmentAvailableCharacters = galleryCharacterAssignment
    ? getGalleryCharacterAssignmentAvailableCharacters(galleryCharacterAssignment)
    : [];
  $: galleryFileNameLines = getGalleryFileNameLines(gallerySection);
  $: galleryCardLineDelta = (galleryFileNameLines - galleryCardLayout.titleLines) * galleryCardLayout.titleLineHeight;
  $: galleryCardHeight = galleryCardLayout.height + galleryCardLineDelta;
  $: galleryCardBodyHeight = galleryCardLayout.body + galleryCardLineDelta;
  $: galleryLandscapeBodyHeight = 126 + (galleryFileNameLines - 3) * 18;
  $: galleryCardWidth = ({ 5: 280, 6: 235, 7: 200, 8: 175, 9: 155 } as Record<number, number>)[galleryCardColumns];
  $: galleryGridStyle = `--gallery-card-width: ${galleryCardWidth}px; --gallery-card-height: ${galleryCardHeight}px; --gallery-card-body-height: ${galleryCardBodyHeight}px; --gallery-landscape-card-body-height: ${galleryLandscapeBodyHeight}px; --gallery-card-title-lines: ${galleryFileNameLines}; --gallery-card-title-height: ${galleryFileNameLines * 1.32}em; --gallery-card-title-size: ${galleryCardLayout.titleSize}; --gallery-card-meta-size: ${galleryCardLayout.metaSize}; --gallery-card-sub-size: ${galleryCardLayout.subSize};`;
  $: galleryCreatorSummaryCoreTitleOptions = getGalleryCreatorCoreFilterOptions(galleryCreatorSummaries, galleryCreatorSummarySection, 'coreTitles');
  $: galleryCreatorSummaryCoreTagOptions = getGalleryCreatorCoreFilterOptions(galleryCreatorSummaries, galleryCreatorSummarySection, 'coreTags');
  $: visibleGalleryCreatorSummaries = getVisibleGalleryCreatorSummaries(
    galleryCreatorSummaries,
    galleryCreatorSummarySection,
    galleryCreatorSummaryRatings,
    galleryCreatorSummaryCoreTitles,
    galleryCreatorSummaryCoreTags,
    galleryCreatorSummarySites,
    galleryCreatorSummaryOverallRatings,
    galleryCreatorSummaryMetricScores,
    galleryCreatorSummaryReminderFilter,
    galleryCreatorSummaryQuery,
    galleryCreatorSummarySorts);
  $: galleryCreatorSummaryCardColumns = galleryCardColumnModes[galleryCreatorSummarySection] ?? 7;
  $: galleryCreatorSummaryCardWidth = ({ 5: 280, 6: 235, 7: 200, 8: 175, 9: 155 } as Record<number, number>)[galleryCreatorSummaryCardColumns] ?? 200;
  $: galleryCreatorSummaryCardHeight = ({ 5: 496, 6: 424, 7: 368, 8: 328, 9: 300 } as Record<number, number>)[galleryCreatorSummaryCardColumns] ?? 368;
  $: galleryCreatorSummaryCardAspect = getGalleryCardAspect(galleryCreatorSummarySection);
  $: galleryCreatorSummaryGridStyle = `--gallery-card-width: ${galleryCreatorSummaryCardWidth}px; --gallery-card-height: ${galleryCreatorSummaryCardHeight}px; --gallery-card-body-height: 149px; --gallery-landscape-card-body-height: 149px; --gallery-card-title-lines: 2; --gallery-card-title-height: 2.64em; --gallery-card-title-size: 0.95rem; --gallery-card-meta-size: 0.8rem; --gallery-card-sub-size: 0.75rem;`;
  $: visibleGalleryTagAssignmentCreatorTitleTags = galleryTagAssignment
    ? getGalleryTagAssignmentCreatorTitleTags(galleryTagAssignment)
    : [];
  $: visibleGalleryTagAssignmentAvailableTags = galleryTagAssignment
    ? getGalleryTagAssignmentAvailableTags(galleryTagAssignment)
    : [];
  $: explorerBreadcrumbs = getExplorerBreadcrumbs(explorerPath);
  $: renameMaximumLength = Math.max(1, 255 - renameExtension.length - (renameIdentifier ? `{gid=${renameIdentifier}}`.length : 0));
  $: splitRenameMaximumLength = Math.max(1, 255 - splitRenameExtension.length - (splitRenameIdentifier ? `{gid=${splitRenameIdentifier}}`.length : 0));
  $: visibleExplorerDetailColumns = explorerDetailColumns
    .map((columnId) => explorerDetailColumnDefinitions.find((column) => column.id === columnId))
    .filter((column): column is ExplorerDetailColumn => column !== undefined);
  $: explorerDetailGridStyle = `--detail-grid-template: ${visibleExplorerDetailColumns.map((column) => column.width).join(' ')};`;

  onMount(() => {
    applyThemeToDocument(appliedThemeSettings);
    startSystemLocalization(appLanguage);
    let lastUserActivityReportAt = 0;
    const reportUserActivity = () => {
      const now = Date.now();
      if (now - lastUserActivityReportAt < 10_000) return;
      lastUserActivityReportAt = now;
      postHostMessage({ type: 'app.userActivity' });
    };
    window.galleryBrowserFlushCreatorTracking = () => {
      flushStickyNoteSaves();
      flushCreatorTrackingBeforeExit();
    };
    window.chrome?.webview?.addEventListener('message', (event) => {
      if (event.data?.type === 'app.context') {
        hostStatus = `${event.data.appName} ${event.data.version}`;
      }

      if (event.data?.type === 'gallery.list.result') {
        items = event.data.items ?? [];
        if (event.data.thumbnailStats) {
          thumbnailStatus = `${event.data.thumbnailStats.found} / ${event.data.thumbnailStats.total} thumbnails`;
        }
        isLoading = false;
      }

      if (event.data?.type === 'gallery.works.result' && event.data.requestId === galleryRequestId) {
        const page = event.data.page;
        const nextItems: GalleryWork[] = page?.items ?? [];
        applyCachedGalleryThumbnailUris(event.data.thumbnailUris);
        if ((event.data.offset ?? 0) === 0) {
          galleryWorks = nextItems;
        }
        else {
          const currentIds = new Set(galleryWorks.map((item) => item.id));
          galleryWorks = [...galleryWorks, ...nextItems.filter((item) => !currentIds.has(item.id))];
        }
        galleryTotal = page?.total ?? 0;
        galleryIsLoading = false;
        continueGalleryBookmarkScrollRestore();
      }

      if (event.data?.type === 'gallery.works.filters.result' && event.data.requestId === pendingGalleryBookmarkRestore?.requestId) {
        restoreGalleryBookmarkWithFilters(event.data.filters ?? null);
        return;
      }

      if (event.data?.type === 'gallery.works.filters.result' && event.data.requestId === galleryRequestId) {
        const filters = event.data.filters;
        const filterParts = Array.isArray(event.data.filterParts)
          ? event.data.filterParts as GalleryFilterPart[]
          : [];
        const includesPart = (part: GalleryFilterPart) => filterParts.length === 0 || filterParts.includes(part);
        if (includesPart('ratings')) galleryRatings = filters?.ratings ?? [];
        if (includesPart('tags')) galleryTags = filters?.tags ?? [];
        if (includesPart('creators')) galleryCreators = filters?.creators ?? [];
        if (includesPart('titles')) galleryTitles = filters?.titles ?? [];
        if (includesPart('characters')) galleryCharacters = filters?.characters ?? [];
        if (event.data.isSnapshot === true || filterParts.length === 0) {
          galleryFilterSnapshotSections = new Set([...galleryFilterSnapshotSections, event.data.category]);
        }
        if (galleryFilterCommitInProgress && event.data.isSnapshot === true) {
          galleryFilterCommitInProgress = false;
          galleryFilterConfigurationDirty = false;
          showExplorerToast('Galleryへフィルタ設定を反映しました。', 'success');
        }
        scheduleGalleryFilterVisibilityCheck();
      }

      if (event.data?.type === 'gallery.works.error' && event.data.requestId === galleryRequestId) {
        galleryIsLoading = false;
        showExplorerToast(event.data.message ?? 'Galleryを読み込めませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.works.filters.error' && event.data.requestId === galleryRequestId) {
        galleryFilterCommitInProgress = false;
        showExplorerToast(event.data.message ?? 'Galleryのフィルタ候補を読み込めませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.works.filters.error' && event.data.requestId === pendingGalleryBookmarkRestore?.requestId) {
        bookmarkRestoreWarnings = [...bookmarkRestoreWarnings, '現在のフィルター候補を確認できなかったため、保存値を可能な範囲で適用しました。'];
        restoreGalleryBookmarkWithFilters(null);
        return;
      }

      if (event.data?.type === 'gallery.reverseFilters.result' && event.data.requestId === galleryReverseFilterRequest?.requestId) {
        const request = galleryReverseFilterRequest;
        galleryReverseFilterRequest = null;
        applyGalleryReverseFilters(
          request.mode,
          Array.isArray(event.data.titles) ? event.data.titles : [],
          Array.isArray(event.data.characters) ? event.data.characters : [],
          request.workName);
      }

      if (event.data?.type === 'gallery.reverseFilters.error' && event.data.requestId === galleryReverseFilterRequest?.requestId) {
        galleryReverseFilterRequest = null;
        showExplorerToast(event.data.message ?? '作品に登録されたフィルターを取得できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.creatorSummary.result' && event.data.requestId === galleryCreatorSummaryRequestId) {
        const incomingItems: GalleryCreatorSummary[] = Array.isArray(event.data.items)
          ? event.data.items.map((item: Partial<GalleryCreatorSummary>) => ({
              ...item,
              trackingLastActivityOn: String(item.trackingLastActivityOn ?? ''),
              hasCreatorTracking: item.hasCreatorTracking === true,
              sinceLastCheckDays: Number(item.sinceLastCheckDays ?? -1),
              followWarnFlg: item.followWarnFlg === true,
              followAlertFlg: item.followAlertFlg === true,
              trackingDays: Number(item.trackingDays ?? -1),
              totalSpend: Number(item.totalSpend ?? 0),
              spendCurrency: String(item.spendCurrency ?? 'JPY'),
              trackingSites: Array.isArray(item.trackingSites) ? item.trackingSites : [],
              evaluationMetrics: item.evaluationMetrics ?? {} as Record<CreatorTrackingMetricKey, number>,
              personalRating: Number(item.personalRating ?? 0)
            } as GalleryCreatorSummary))
          : [];
        applyCachedGalleryThumbnailUris(event.data.thumbnailUris);
        galleryCreatorSummaries = event.data.reset === true
          ? incomingItems
          : [...galleryCreatorSummaries, ...incomingItems];
        galleryCreatorSummaryError = '';
        galleryCreatorSummaryIsLoading = event.data.isLast === false;
        if (incomingItems.length > 0 && creatorTrackingTabs.length > 0) {
          const incomingById = new Map(incomingItems.map(item => [item.id, item]));
          creatorTrackingTabs = creatorTrackingTabs.map(tab => {
            const summary = incomingById.get(tab.summary.id);
            return summary ? { ...tab, summary } : tab;
          });
          if (creatorTrackingSummary) {
            creatorTrackingSummary = incomingById.get(creatorTrackingSummary.id) ?? creatorTrackingSummary;
          }
        }
        if (pendingGalleryCreatorTracking) {
          const pending = pendingGalleryCreatorTracking;
          const summary = incomingItems.find((item) =>
            item.category === pending.category &&
            item.creator.trim().localeCompare(pending.creator.trim(), 'ja-JP', { sensitivity: 'base' }) === 0);
          if (summary) {
            pendingGalleryCreatorTracking = null;
            openCreatorTrackingForSummary(summary);
          }
          else if (event.data.isLast !== false) {
            pendingGalleryCreatorTracking = null;
            if (pending.creatorFolder) {
              openCreatorTrackingForSummary(createCreatorTrackingTemplateSummary(
                pending.creator,
                pending.category,
                pending.creatorFolder));
            }
            else {
              showExplorerToast(`Creator「${pending.creator}」の集計情報を取得できませんでした。`, 'error');
            }
          }
        }
        if (event.data.isLast !== false && pendingCreatorTrackingBookmarkRestore) {
          const pending = pendingCreatorTrackingBookmarkRestore;
          restoreCreatorTrackingBookmark(pending.bookmark, pending.state);
        }
        else if (event.data.isLast !== false && pendingCreatorTrackingSessionRestore) {
          const pending = pendingCreatorTrackingSessionRestore;
          pendingCreatorTrackingSessionRestore = null;
          restoreCreatorTrackingSession(pending.state, pending.activate);
        }
        else if (event.data.isLast !== false && pendingCreatorSummaryBookmarkRestore) {
          const pending = pendingCreatorSummaryBookmarkRestore;
          pendingCreatorSummaryBookmarkRestore = null;
          restoreCreatorSummaryBookmark(pending.bookmark, pending.state);
        }
      }

      if (event.data?.type === 'gallery.creatorSummary.error' && event.data.requestId === galleryCreatorSummaryRequestId) {
        galleryCreatorSummaryIsLoading = false;
        galleryCreatorSummaryError = event.data.message ?? 'Creatorsを読み込めませんでした。';
        if (pendingCreatorSummaryBookmarkRestore || pendingCreatorTrackingBookmarkRestore) {
          bookmarkRestoreWarnings = [`Creator情報を読み込めなかったため、Bookmarkを復元できませんでした: ${galleryCreatorSummaryError}`];
          pendingCreatorSummaryBookmarkRestore = null;
          pendingCreatorTrackingBookmarkRestore = null;
        }
        if (pendingCreatorTrackingSessionRestore) {
          const pending = pendingCreatorTrackingSessionRestore;
          pendingCreatorTrackingSessionRestore = null;
          restoreCreatorTrackingSession(pending.state, pending.activate);
        }
        const pending = pendingGalleryCreatorTracking;
        pendingGalleryCreatorTracking = null;
        if (pending?.creatorFolder) {
          openCreatorTrackingForSummary(createCreatorTrackingTemplateSummary(
            pending.creator,
            pending.category,
            pending.creatorFolder));
          showExplorerToast('Creator集計を取得できなかったため、作者フォルダの情報からCreator Trackingを作成します。', 'success');
        }
        else {
          showExplorerToast(galleryCreatorSummaryError, 'error');
        }
      }

      if (event.data?.type === 'user.metrics.result' && event.data.requestId === userMetricsRequestId) {
        userMetricsDashboard = event.data.dashboard as UserMetricsDashboard;
        userMetricsCategory = userMetricsDashboard?.category ?? userMetricsCategory;
        userMetricsError = '';
        userMetricsIsLoading = false;
      }

      if (event.data?.type === 'user.metrics.error' && event.data.requestId === userMetricsRequestId) {
        userMetricsDashboard = null;
        userMetricsError = event.data.message ?? 'User Metricsを読み込めませんでした。';
        userMetricsIsLoading = false;
        showExplorerToast(userMetricsError, 'error');
      }

      if (event.data?.type === 'calendar.subscriptions.result' && event.data.requestId === calendarRequestId) {
        calendarSettings = parseCalendarSettings(event.data.settings);
        calendarWeekStartDraft = calendarSettings.weekStartDay;
        calendarEvents = Array.isArray(event.data.events)
          ? event.data.events.map(normalizeCalendarEvent).filter((item): item is CalendarSubscriptionEvent => item !== null)
          : [];
        calendarIsLoading = false;
        calendarError = '';
      }

      if (event.data?.type === 'calendar.subscriptions.error' && event.data.requestId === calendarRequestId) {
        calendarEvents = [];
        calendarIsLoading = false;
        calendarError = event.data.message ?? 'Calendarを読み込めませんでした。';
        showExplorerToast(calendarError, 'error');
      }

      if (event.data?.type === 'calendar.ics.exported' && event.data.requestId === calendarIcsRequestId) {
        showExplorerToast(`${event.data.fileName ?? 'iCalendarファイル'}を保存しました。Googleカレンダーへインポートできます。`, 'success');
      }

      if (event.data?.type === 'calendar.ics.error' && event.data.requestId === calendarIcsRequestId) {
        showExplorerToast(event.data.message ?? 'iCalendarファイルを保存できませんでした。', 'error');
      }

      if (event.data?.type === 'creator.tracking.result') {
        const resultTab = creatorTrackingTabs.find(tab => tab.requestId === event.data.requestId);
        if (resultTab) {
          const refreshed = creatorTrackingRefreshRequestIds.has(event.data.requestId);
          if (refreshed) {
            creatorTrackingRefreshRequestIds = new Set(
              [...creatorTrackingRefreshRequestIds].filter(requestId => requestId !== event.data.requestId));
          }
          const tracking = normalizeCreatorTracking(event.data.tracking as CreatorTracking);
          const dashboard = event.data.dashboard as CreatorTrackingDashboard ?? null;
          const summary = event.data.summary
            ? event.data.summary as GalleryCreatorSummary
            : resultTab.summary;
          const dashboardContext = event.data.dashboardContext
            ? event.data.dashboardContext as CreatorTrackingDashboardContext
            : resultTab.dashboardContext;
          synchronizeCreatorSummaryReminderState(summary, tracking);
          creatorTrackingTabs = creatorTrackingTabs.map(tab => tab.id === resultTab.id
            ? { ...tab, summary, tracking, dashboardContext, dashboard, isLoading: false, isSaving: false, dirty: false, error: '' }
            : tab);
          if (event.data.summary) {
            galleryCreatorSummaries = galleryCreatorSummaries.map(item => item.id === summary.id ? summary : item);
          }
          if (resultTab.id === activeCreatorTrackingTabId) {
            creatorTrackingSummary = summary;
            creatorTrackingDashboardContext = dashboardContext;
            creatorTracking = tracking;
            creatorTrackingDashboard = dashboard;
            creatorTrackingIsLoading = false;
            creatorTrackingIsSaving = false;
            creatorTrackingDirty = false;
            creatorTrackingError = '';
            queueMicrotask(scrollCreatorTrackingArchiveToEnd);
          }
          if (event.data.created === true) {
            showExplorerToast(`${tracking.displayName || tracking.creator}のCreator Trackingを作成しました。`, 'success');
          }
          else if (refreshed) {
            const scanMessage = String(event.data.scanMessage ?? '').trim();
            showExplorerToast(
              scanMessage
                ? `${tracking.displayName || tracking.creator}の最新データを反映しました。${scanMessage}`
                : `${tracking.displayName || tracking.creator}の最新データを反映しました。`,
              'success');
          }
        }
      }

      if (event.data?.type === 'creator.tracking.saved') {
        const savedTab = creatorTrackingTabs.find(tab => tab.requestId === event.data.requestId);
        if (savedTab) {
          const refreshed = creatorTrackingRefreshRequestIds.has(event.data.requestId);
          if (refreshed) {
            creatorTrackingRefreshRequestIds = new Set(
              [...creatorTrackingRefreshRequestIds].filter(requestId => requestId !== event.data.requestId));
          }
          const tracking = normalizeCreatorTracking(event.data.tracking as CreatorTracking);
          const dashboard = event.data.dashboard as CreatorTrackingDashboard ?? null;
          const summary = event.data.summary
            ? event.data.summary as GalleryCreatorSummary
            : savedTab.summary;
          const dashboardContext = event.data.dashboardContext
            ? event.data.dashboardContext as CreatorTrackingDashboardContext
            : savedTab.dashboardContext;
          synchronizeCreatorSummaryReminderState(summary, tracking);
          const preserveNewerDraft = savedTab.dirty;
          creatorTrackingTabs = creatorTrackingTabs.map(tab => tab.id === savedTab.id
            ? {
                ...tab,
                summary,
                tracking: preserveNewerDraft ? tab.tracking : tracking,
                dashboardContext,
                dashboard,
                isSaving: false,
                dirty: preserveNewerDraft,
                error: ''
              }
            : tab);
          if (event.data.summary) {
            galleryCreatorSummaries = galleryCreatorSummaries.map(item => item.id === summary.id ? summary : item);
          }
          if (savedTab.id === activeCreatorTrackingTabId) {
            creatorTrackingSummary = summary;
            creatorTrackingDashboardContext = dashboardContext;
            if (!preserveNewerDraft) creatorTracking = tracking;
            creatorTrackingDashboard = dashboard;
            creatorTrackingIsSaving = false;
            creatorTrackingDirty = preserveNewerDraft;
            creatorTrackingError = '';
            queueMicrotask(scrollCreatorTrackingArchiveToEnd);
          }
          showExplorerToast(
            refreshed
              ? `${tracking.displayName || tracking.creator}の変更内容を保存し、最新データを反映しました。${String(event.data.scanMessage ?? '').trim()}`
              : `${tracking.displayName || tracking.creator}のCreator Trackingを保存しました。`,
            'success');
          if (preserveNewerDraft) {
            queueMicrotask(() => saveCreatorTrackingTab(savedTab.id));
          }
        }
      }

      if (event.data?.type === 'creator.tracking.deleted') {
        if (event.data.requestId !== creatorTrackingDeleteRequestId) {
          return;
        }

        const result = event.data.result ?? {};
        const deletedCreator = String(result.creator ?? creatorTrackingDeleteTarget?.creator ?? '').trim();
        const deletedWorks = Number(result.worksDeleted ?? 0);
        const deletedTrackingRows = Number(result.creatorTrackingRowsDeleted ?? 0);
        creatorTrackingDeleteInProgress = false;
        creatorTrackingDeleteStep = 0;
        creatorTrackingDeleteTarget = null;
        creatorTrackingDeleteRequestId = '';
        removeDeletedCreatorTrackingTabs(deletedCreator);
        galleryFilterSnapshotSections = new Set();
        loadGalleryCreatorSummaries(true);
        loadGalleryWorks(false, galleryRatingFilters, true);
        if (userMetricsDashboard) loadUserMetrics(userMetricsCategory, true);
        showExplorerToast(
          `Creator「${deletedCreator}」のDBデータを削除しました。Creator Tracking ${deletedTrackingRows}件 / 作品 ${deletedWorks}件`,
          'success');
      }

      if (event.data?.type === 'creator.tracking.delete.error') {
        if (event.data.requestId !== creatorTrackingDeleteRequestId) {
          return;
        }

        creatorTrackingDeleteInProgress = false;
        showExplorerToast(event.data.message ?? 'Creatorデータを削除できませんでした。', 'error');
      }

      if (event.data?.type === 'creator.tracking.error') {
        const errorTab = creatorTrackingTabs.find(tab => tab.requestId === event.data.requestId);
        if (errorTab) {
          if (creatorTrackingRefreshRequestIds.has(event.data.requestId)) {
            creatorTrackingRefreshRequestIds = new Set(
              [...creatorTrackingRefreshRequestIds].filter(requestId => requestId !== event.data.requestId));
          }
          const message = event.data.message ?? 'Creator Trackingを読み込めませんでした。';
          creatorTrackingTabs = creatorTrackingTabs.map(tab => tab.id === errorTab.id
            ? { ...tab, isLoading: false, isSaving: false, error: message }
            : tab);
          if (errorTab.id === activeCreatorTrackingTabId) {
            creatorTrackingIsLoading = false;
            creatorTrackingIsSaving = false;
            creatorTrackingError = message;
          }
          showExplorerToast(message, 'error');
        }
      }

      if (event.data?.type === 'creator.tracking.url.error') {
        showExplorerToast(event.data.message ?? 'URLを開けませんでした。', 'error');
      }

      if (event.data?.type === 'creator.tracking.flush.error') {
        showExplorerToast(event.data.message ?? '終了前にCreator Trackingを保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.creatorTracking.result') {
        const compositionLabelLimit = Math.max(1, Math.min(8, Math.round(Number(event.data.compositionLabelLimit) || 5)));
        const followPolicyOptions = Array.isArray(event.data.followPolicyOptions)
          ? event.data.followPolicyOptions.map((value: unknown) => String(value ?? '').trim()).filter(Boolean)
          : defaultCreatorTrackingSettingsDraft.followPolicyOptions;
        const receivedMetrics = Array.isArray(event.data.metrics) ? event.data.metrics : [];
        const metrics = creatorTrackingMetricDefinitions.map((definition) => {
          const received = receivedMetrics.find(
            (candidate: Partial<CreatorTrackingSettingsMetricDraft>) => candidate.key === definition.key);
          const weightPercent = received?.weightPercent === null || received?.weightPercent === undefined
            ? null
            : Math.max(0, Math.min(100, Number(received.weightPercent) || 0));
          return {
            ...definition,
            label: String(received?.label ?? ''),
            weight: (weightPercent ?? 0) / 100,
            weightPercent
          };
        });
        if (event.data.updated !== true) {
          const activityPlaces = Array.isArray(event.data.activityPlaces)
            ? event.data.activityPlaces.map((place: Partial<CreatorTrackingActivityPlaceSetting>) => ({
                label: String(place.label ?? ''),
                placeholder: String(place.placeholder ?? ''),
                iconDataUri: typeof place.iconDataUri === 'string' ? place.iconDataUri : ''
              }))
            : [];
          creatorTrackingSettingsDraft = {
            ...creatorTrackingSettingsDraft,
            metrics,
            activityPlaces,
            compositionLabelLimit,
            followPolicyOptions
          };
        }
        else {
          creatorTrackingSettingsDraft = {
            ...creatorTrackingSettingsDraft,
            metrics,
            compositionLabelLimit,
            followPolicyOptions
          };
          if (creatorTrackingCompositionRefreshPending) {
            creatorTrackingCompositionRefreshPending = false;
            if (galleryCreatorSummaries.length > 0 || creatorTrackingTabs.length > 0) {
              loadGalleryCreatorSummaries();
            }
          }
        }
      }

      if (event.data?.type === 'settings.creatorTracking.error') {
        creatorTrackingCompositionRefreshPending = false;
        showExplorerToast(event.data.message ?? 'Creator Tracking設定を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.creatorTracking.icon.result') {
        const requestId = String(event.data.requestId ?? '');
        const index = creatorTrackingIconFetchRequests[requestId];
        if (index !== undefined) {
          const iconDataUri = String(event.data.iconDataUri ?? '');
          creatorTrackingSettingsDraft = {
            ...creatorTrackingSettingsDraft,
            activityPlaces: creatorTrackingSettingsDraft.activityPlaces.map((place, candidateIndex) =>
              candidateIndex === index ? { ...place, iconDataUri } : place)
          };
          const { [requestId]: _, ...remainingRequests } = creatorTrackingIconFetchRequests;
          creatorTrackingIconFetchRequests = remainingRequests;
          saveCreatorTrackingSettings();
        }
      }

      if (event.data?.type === 'settings.creatorTracking.icon.error') {
        const requestId = String(event.data.requestId ?? '');
        const { [requestId]: _, ...remainingRequests } = creatorTrackingIconFetchRequests;
        creatorTrackingIconFetchRequests = remainingRequests;
        showExplorerToast(event.data.message ?? 'サイトアイコンを取得できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.tagAssignment.options.result' && event.data.requestId === galleryTagAssignment?.requestId) {
        const commonAssignedTags: GalleryTagAssignmentOption[] = event.data.commonAssignedTags ?? [];
        const commonTagIds = new Set(commonAssignedTags.map((option) => option.id));
        galleryTagAssignment = {
          ...galleryTagAssignment,
          creator: String(event.data.creator ?? galleryTagAssignment.creator),
          title: String(event.data.title ?? galleryTagAssignment.title),
          creatorTitleTags: event.data.creatorTitleTags ?? [],
          availableTags: event.data.availableTags ?? [],
          commonAssignedTags,
          selectedAssignedTagIds: galleryTagAssignment.selectedAssignedTagIds.filter((id) => commonTagIds.has(id)),
          isLoading: false
        };
      }

      if (event.data?.type === 'gallery.tagAssignment.options.error' && event.data.requestId === galleryTagAssignment?.requestId) {
        galleryTagAssignment = null;
        galleryTagAssignmentReturnPending = false;
        showExplorerToast(event.data.message ?? 'Tag候補を取得できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.tagAssignment.applied' && galleryTagAssignment) {
        const selectedTag = [...galleryTagAssignment.creatorTitleTags, ...galleryTagAssignment.availableTags]
          .find((option) => option.id === galleryTagAssignment?.selectedTagId);
        const addedCount = Number(event.data.addedCount ?? 0);
        const skippedCount = Number(event.data.skippedCount ?? 0);
        const removedCount = Number(event.data.removedCount ?? 0);
        galleryTagAssignment = null;
        galleryTagAssignmentReturnPending = false;
        galleryContextMenu = null;
        const resultParts = [`${addedCount} 件にTag「${selectedTag?.tag ?? ''}」を登録しました。`];
        if (removedCount > 0) resultParts.push(`${removedCount} 件のTagを解除しました。`);
        if (skippedCount > 0) resultParts.push(`${skippedCount} 件は既に登録済みのためスキップしました。`);
        showExplorerToast(resultParts.join(''), 'success');
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'gallery.tagAssignment.removed' && galleryTagAssignment) {
        const removedCount = Number(event.data.removedCount ?? 0);
        galleryTagAssignment = {
          ...galleryTagAssignment,
          selectedAssignedTagIds: [],
          isSaving: false
        };
        showExplorerToast(`${removedCount} 件のTagを解除しました。`, 'success');
        requestGalleryTagAssignmentOptions(galleryTagAssignment);
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'gallery.tagAssignment.error') {
        if (galleryTagAssignment) {
          galleryTagAssignment = { ...galleryTagAssignment, isSaving: false };
        }
        showExplorerToast(event.data.message ?? 'Tagを更新できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.titleAssignment.options.result' && event.data.requestId === galleryTitleAssignment?.requestId) {
        const commonAssignedTitles: GalleryTitleAssignmentOption[] = event.data.commonAssignedTitles ?? [];
        const commonTitleIds = new Set(commonAssignedTitles.map((option) => option.id));
        const creators = (event.data.creators ?? [])
          .map((creator: unknown) => String(creator).trim())
          .filter(Boolean);
        galleryTitleAssignment = {
          ...galleryTitleAssignment,
          creators: creators.length > 0 ? creators : galleryTitleAssignment.creators,
          creatorTitles: event.data.creatorTitles ?? [],
          availableTitles: event.data.availableTitles ?? [],
          commonAssignedTitles,
          selectedAssignedTitleIds: galleryTitleAssignment.selectedAssignedTitleIds.filter((id) => commonTitleIds.has(id)),
          isLoading: false
        };
        if (pendingGalleryTitleAssignmentSelectedTitleId !== null) {
          const selectedId = pendingGalleryTitleAssignmentSelectedTitleId;
          pendingGalleryTitleAssignmentSelectedTitleId = null;
          const hasSelectedTitle = [...galleryTitleAssignment.creatorTitles, ...galleryTitleAssignment.availableTitles, ...galleryTitleAssignment.commonAssignedTitles]
            .some((option) => option.id === selectedId);
          if (hasSelectedTitle) {
            selectGalleryTitleAssignment(selectedId);
          }
        }
      }

      if (event.data?.type === 'gallery.titleAssignment.options.error' && event.data.requestId === galleryTitleAssignment?.requestId) {
        galleryTitleAssignment = null;
        showExplorerToast(event.data.message ?? 'Title候補を取得できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.titleAssignment.characters.result' && event.data.requestId === galleryTitleAssignment?.characterRequestId) {
        galleryTitleAssignment = {
          ...galleryTitleAssignment,
          creatorTitleCharacters: event.data.creatorTitleCharacters ?? [],
          availableCharacters: event.data.availableCharacters ?? [],
          isCharacterLoading: false
        };
        if (pendingGalleryTitleAssignmentSelectedCharacterId !== null) {
          const selectedId = pendingGalleryTitleAssignmentSelectedCharacterId;
          pendingGalleryTitleAssignmentSelectedCharacterId = null;
          const hasSelectedCharacter = [...galleryTitleAssignment.creatorTitleCharacters, ...galleryTitleAssignment.availableCharacters]
            .some((option) => option.id === selectedId);
          if (hasSelectedCharacter) {
            galleryTitleAssignment = { ...galleryTitleAssignment, selectedCharacterId: selectedId };
          }
        }
      }

      if (event.data?.type === 'gallery.titleAssignment.characters.error' && event.data.requestId === galleryTitleAssignment?.characterRequestId) {
        galleryTitleAssignment = {
          ...galleryTitleAssignment,
          creatorTitleCharacters: [],
          availableCharacters: [],
          selectedCharacterId: null,
          isCharacterLoading: false
        };
        showExplorerToast(event.data.message ?? 'Character候補を取得できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.titleAssignment.applied' && galleryTitleAssignment) {
        const selectedTitle = [...galleryTitleAssignment.creatorTitles, ...galleryTitleAssignment.availableTitles]
          .find((option) => option.id === galleryTitleAssignment?.selectedTitleId);
        const selectedCharacter = [...galleryTitleAssignment.creatorTitleCharacters, ...galleryTitleAssignment.availableCharacters]
          .find((option) => option.id === galleryTitleAssignment?.selectedCharacterId);
        const addedCount = Number(event.data.addedCount ?? 0);
        const skippedCount = Number(event.data.skippedCount ?? 0);
        const removedCount = Number(event.data.removedCount ?? 0);
        const characterAddedCount = Number(event.data.characterAddedCount ?? 0);
        const characterSkippedCount = Number(event.data.characterSkippedCount ?? 0);
        galleryTitleAssignment = null;
        galleryContextMenu = null;
        const title = selectedTitle?.title ?? '';
        const resultParts = [`${addedCount} 件にTitle「${title}」を登録しました。`];
        if (selectedCharacter) {
          resultParts.push(`${characterAddedCount} 件にCharacter「${selectedCharacter.character}」を登録しました。`);
          if (characterSkippedCount > 0) resultParts.push(`${characterSkippedCount} 件のCharacterは既に登録済みのためスキップしました。`);
        }
        if (removedCount > 0) resultParts.push(`${removedCount} 件のTitle属性を解除しました。`);
        if (skippedCount > 0) resultParts.push(`${skippedCount} 件は既に登録済みのためスキップしました。`);
        const message = resultParts.join('');
        showExplorerToast(message, 'success');
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'gallery.titleAssignment.removed' && galleryTitleAssignment) {
        const removedCount = Number(event.data.removedCount ?? 0);
        galleryTitleAssignment = {
          ...galleryTitleAssignment,
          selectedAssignedTitleIds: [],
          isSaving: false
        };
        showExplorerToast(`${removedCount} 件のTitle属性を解除しました。`, 'success');
        requestGalleryTitleAssignmentOptions(galleryTitleAssignment);
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'gallery.titleAssignment.error') {
        if (galleryTitleAssignment) {
          galleryTitleAssignment = { ...galleryTitleAssignment, isSaving: false };
        }
        showExplorerToast(event.data.message ?? 'Title属性を登録できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.characterAssignment.options.result' && event.data.requestId === galleryCharacterAssignment?.requestId) {
        const commonAssignedCharacters: GalleryCharacterAssignmentOption[] = event.data.commonAssignedCharacters ?? [];
        const commonCharacterIds = new Set(commonAssignedCharacters.map((option) => option.id));
        const creators = (event.data.creators ?? [])
          .map((creator: unknown) => String(creator).trim())
          .filter(Boolean);
        const titles = (event.data.titles ?? [])
          .map((title: unknown) => String(title).trim())
          .filter(Boolean);
        galleryCharacterAssignment = {
          ...galleryCharacterAssignment,
          creators: creators.length > 0 ? creators : galleryCharacterAssignment.creators,
          titles: titles.length > 0 ? titles : galleryCharacterAssignment.titles,
          creatorTitleCharacters: event.data.creatorTitleCharacters ?? [],
          availableCharacters: event.data.availableCharacters ?? [],
          commonAssignedCharacters,
          selectedAssignedCharacterIds: galleryCharacterAssignment.selectedAssignedCharacterIds.filter((id) => commonCharacterIds.has(id)),
          isLoading: false
        };
        if (pendingGalleryCharacterAssignmentSelectedId !== null) {
          const selectedId = pendingGalleryCharacterAssignmentSelectedId;
          pendingGalleryCharacterAssignmentSelectedId = null;
          const hasSelectedCharacter = [...galleryCharacterAssignment.creatorTitleCharacters, ...galleryCharacterAssignment.availableCharacters]
            .some((option) => option.id === selectedId);
          if (hasSelectedCharacter) {
            galleryCharacterAssignment = { ...galleryCharacterAssignment, selectedCharacterId: selectedId };
          }
        }
      }

      if (event.data?.type === 'gallery.characterAssignment.options.error' && event.data.requestId === galleryCharacterAssignment?.requestId) {
        galleryCharacterAssignment = null;
        showExplorerToast(event.data.message ?? 'Character候補を取得できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.characterAssignment.applied' && galleryCharacterAssignment) {
        const selectedCharacter = [...galleryCharacterAssignment.creatorTitleCharacters, ...galleryCharacterAssignment.availableCharacters]
          .find((option) => option.id === galleryCharacterAssignment?.selectedCharacterId);
        const addedCount = Number(event.data.addedCount ?? 0);
        const skippedCount = Number(event.data.skippedCount ?? 0);
        const removedCount = Number(event.data.removedCount ?? 0);
        galleryCharacterAssignment = null;
        galleryContextMenu = null;
        const character = selectedCharacter?.character ?? '';
        const resultParts = [`${addedCount} 件にCharacter「${character}」を登録しました。`];
        if (removedCount > 0) resultParts.push(`${removedCount} 件のCharacter属性を解除しました。`);
        if (skippedCount > 0) resultParts.push(`${skippedCount} 件は既に登録済みのためスキップしました。`);
        showExplorerToast(resultParts.join(''), 'success');
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'gallery.characterAssignment.removed' && galleryCharacterAssignment) {
        const removedCount = Number(event.data.removedCount ?? 0);
        galleryCharacterAssignment = {
          ...galleryCharacterAssignment,
          selectedAssignedCharacterIds: [],
          isSaving: false
        };
        showExplorerToast(`${removedCount} 件のCharacter属性を解除しました。`, 'success');
        requestGalleryCharacterAssignmentOptions(galleryCharacterAssignment);
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'gallery.characterAssignment.error') {
        if (galleryCharacterAssignment) {
          galleryCharacterAssignment = { ...galleryCharacterAssignment, isSaving: false };
        }
        showExplorerToast(event.data.message ?? 'Character属性を登録できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.works.delete.result') {
        galleryDeleteInProgress = false;
        galleryDeleteConfirmation = false;
        galleryContextMenu = null;
        clearGalleryWorkSelection();
        showExplorerToast(`${Number(event.data.deletedRecords ?? 0)} 件を削除しました。`, 'success');
        loadGalleryWorks();
      }

      if (event.data?.type === 'gallery.works.delete.error') {
        galleryDeleteInProgress = false;
        showExplorerToast(event.data.message ?? 'ファイルを削除できませんでした。', 'error');
      }

      if (event.data?.type === 'gallery.work.thumbnail.result') {
        requestedGalleryThumbnailIds.delete(event.data.id);
        if (event.data.thumbnailUri) {
          unavailableGalleryThumbnailIds.delete(event.data.id);
          galleryThumbnails = { ...galleryThumbnails, [event.data.id]: event.data.thumbnailUri };
        }
        else {
          unavailableGalleryThumbnailIds.add(event.data.id);
        }
      }

      if (event.data?.type === 'gallery.work.rating.result') {
        const id = event.data.id as string;
        const rating = Number(event.data.rating ?? 0);
        const delta = pendingGalleryRatingDeltas[id];
        const pending = new Set(pendingGalleryRatingIds);
        pending.delete(id);
        pendingGalleryRatingIds = pending;
        const { [id]: _, ...remainingDeltas } = pendingGalleryRatingDeltas;
        pendingGalleryRatingDeltas = remainingDeltas;
        const baseline = Number(event.data.baseline ?? rating);
        const nextLitIds = new Set(galleryRatingLitIds);
        if (rating > baseline) nextLitIds.add(id);
        else nextLitIds.delete(id);
        galleryRatingLitIds = nextLitIds;
        galleryWorks = galleryWorks.map((work) => work.id === id ? { ...work, rating } : work);
        window.setTimeout(() => loadGalleryWorks(), 560);
      }

      if (event.data?.type === 'gallery.work.rating.error') {
        const id = event.data.id as string;
        const pending = new Set(pendingGalleryRatingIds);
        pending.delete(id);
        pendingGalleryRatingIds = pending;
        const { [id]: _, ...remainingDeltas } = pendingGalleryRatingDeltas;
        pendingGalleryRatingDeltas = remainingDeltas;
        showExplorerToast(event.data.message ?? '評価を更新できませんでした。', 'error');
      }

      if (event.data?.type === 'thumbnail.result') {
        const id = event.data.id;
        const thumbnailUri = event.data.thumbnailUri;
        if (thumbnailUri) {
          items = items.map((item) => (item.id === id ? { ...item, thumbnailUri } : item));
          generatedThumbnails += 1;
          thumbnailStatus = `${generatedThumbnails} generated this session`;
        }
      }

      if (event.data?.type === 'settings.externalApps.result') {
        externalAppRules = event.data.rules ?? [];
      }

      if (event.data?.type === 'filters.editor.result') {
        pendingFilterEditorDefinitionDeletion = null;
        filterEditorCategories = event.data.categories ?? [];
        filterEditorTitles = event.data.titles ?? [];
        filterEditorCharacters = event.data.characters ?? [];
        if (
          galleryCharacterAssignmentReturnPending &&
          galleryCharacterAssignment?.titles.length === 1 &&
          filterEditorAttribute === 'character' &&
          filterEditorSelectedId === null &&
          filterEditorParentTitleId === null)
        {
          const parent = filterEditorTitles.find(definition =>
            definition.canonicalName.localeCompare(galleryCharacterAssignment?.titles[0] ?? '', 'ja-JP', { sensitivity: 'base' }) === 0);
          if (parent) {
            filterEditorParentTitleId = parent.id;
            filterEditorParentTitle = parent.canonicalName;
            filterEditorCategoryName = parent.categoryName === '未分類' ? '' : parent.categoryName;
          }
        }
        const selectedCategoryId = Number(event.data.selectedCategoryId ?? filterEditorSelectedCategoryId);
        if (filterEditorAttribute === 'category') {
          const selectedCategory = Number.isFinite(selectedCategoryId) && selectedCategoryId > 0
            ? filterEditorCategories.find((category) => category.id === selectedCategoryId)
            : undefined;
          if (selectedCategory) {
            selectFilterEditorCategory(selectedCategory);
          }
          else {
            createFilterEditorCategory();
          }
        }
        const selectedId = Number(event.data.selectedId ?? filterEditorSelectedId);
        if (filterEditorAttribute !== 'category' && Number.isFinite(selectedId) && selectedId > 0) {
          filterEditorSelectedId = selectedId;
          const selected = [...filterEditorTitles, ...filterEditorCharacters]
            .find((definition) => definition.id === selectedId);
          if (selected) {
            selectFilterEditorDefinition(selected);
          }
          else {
            createFilterEditorDefinition(filterEditorAttribute);
          }
        }
        if (event.data.updated) {
          galleryFilterConfigurationDirty = true;
          loadGalleryWorks();
        }
        if (galleryTitleAssignmentReturnPending && event.data.updated && galleryTitleAssignment) {
          const selectedId = Number(event.data.selectedId ?? 0);
          pendingGalleryTitleAssignmentSelectedTitleId = Number.isFinite(selectedId) && selectedId > 0 ? selectedId : null;
          galleryTitleAssignmentReturnPending = false;
          pendingGalleryTitleAssignmentCategory = '';
          activeView = 'library';
          persistNavigationState();
          requestGalleryTitleAssignmentOptions(galleryTitleAssignment);
        }
        if (galleryCharacterAssignmentReturnPending && event.data.updated && galleryCharacterAssignment) {
          const selectedId = Number(event.data.selectedId ?? 0);
          pendingGalleryCharacterAssignmentSelectedId = Number.isFinite(selectedId) && selectedId > 0 ? selectedId : null;
          galleryCharacterAssignmentReturnPending = false;
          pendingGalleryCharacterAssignmentCategory = '';
          activeView = 'library';
          persistNavigationState();
          requestGalleryCharacterAssignmentOptions(galleryCharacterAssignment);
        }
        if (filterEditorNavigationCommitInProgress && pendingFilterEditorNavigation) {
          const navigation = pendingFilterEditorNavigation;
          filterEditorNavigationCommitInProgress = false;
          pendingFilterEditorNavigation = null;
          completeViewChange(navigation);
        }
      }

      if (event.data?.type === 'filters.editor.error') {
        filterEditorNavigationCommitInProgress = false;
        showExplorerToast(event.data.message ?? 'フィルタ定義を更新できませんでした。', 'error');
      }

      if (event.data?.type === 'filters.editor.csv.preview') {
        filterEditorCsvFileName = event.data.fileName ?? '';
        filterEditorCsvHasHeader = Boolean(event.data.hasHeader);
        filterEditorCsvSuggestedHeader = Boolean(event.data.suggestedHeader);
        filterEditorCsvTotal = Number(event.data.total ?? 0);
        filterEditorCsvPreview = event.data.rows ?? [];
        filterEditorCsvScope = event.data.scope === 'category' ? 'category' : 'combination';
        filterEditorCsvAnalysis = event.data.analysis ?? null;
        filterEditorCsvCaseKey = 'update';
        filterEditorCsvReady = true;
      }

      if (event.data?.type === 'filters.editor.standardName.results') {
        filterEditorStandardNameResults = event.data.results ?? [];
        filterEditorStandardNameSelected = filterEditorStandardNameResults[0]?.name ?? '';
        if (filterEditorStandardNameResults.length === 0) {
          showExplorerToast('候補が見つかりませんでした。検索語を変えてください。', 'error');
        }
      }

      if (event.data?.type === 'filters.editor.standardName.opened') {
        showExplorerToast('ブラウザで検索を開きました。', 'success');
      }

      if (event.data?.type === 'filters.editor.csv.imported') {
        filterEditorCsvReady = false;
        showExplorerToast(`${Number(event.data.count ?? 0)} 件のフィルタ項目を反映しました。`, 'success');
      }

      if (event.data?.type === 'filters.editor.list.exported') {
        showExplorerToast(`${event.data.fileName ?? 'アイテムリスト'} を出力しました。`, 'success');
      }

      if (event.data?.type === 'filters.editor.csv.error') {
        showExplorerToast(event.data.message ?? 'CSVを読み込めませんでした。', 'error');
      }

      if (event.data?.type === 'tags.manager.result') {
        tagManagementTags = event.data.tags ?? [];
        const selectedId = Number(event.data.selectedId ?? tagManagementSelectedId);
        const selected = Number.isFinite(selectedId) && selectedId > 0
          ? tagManagementTags.find((tag) => tag.id === selectedId)
          : undefined;
        if (selected) {
          selectTagManagementDefinition(selected);
        }
        else if (tagManagementSelectedId !== null) {
          createTagManagementDefinition();
        }
        if (event.data.updated) {
          const enabledTags = new Set(tagManagementTags.filter((tag) => tag.useFlag !== 0).map((tag) => tag.tag));
          galleryTagFilters = galleryTagFilters.filter((tag) => enabledTags.has(tag));
          galleryPromotedTags = galleryPromotedTags.filter((tag) => enabledTags.has(tag));
          loadGalleryWorks(false, galleryRatingFilters, true);
        }
      }

      if (event.data?.type === 'tags.manager.error') {
        showExplorerToast(event.data.message ?? 'Tagを更新できませんでした。', 'error');
      }

      if (event.data?.type === 'tags.manager.csv.preview') {
        tagManagementCsvFileName = event.data.fileName ?? '';
        tagManagementCsvHasHeader = Boolean(event.data.hasHeader);
        tagManagementCsvTotal = Number(event.data.total ?? 0);
        tagManagementCsvPreview = event.data.rows ?? [];
        tagManagementCsvReady = true;
      }

      if (event.data?.type === 'tags.manager.csv.imported') {
        tagManagementCsvReady = false;
        showExplorerToast(`${Number(event.data.count ?? 0)} 件のTagを反映しました。`, 'success');
      }

      if (event.data?.type === 'tags.manager.list.exported') {
        showExplorerToast(`${event.data.fileName ?? 'Tagリスト'} を出力しました。`, 'success');
      }

      if (event.data?.type === 'tags.manager.csv.error') {
        showExplorerToast(event.data.message ?? 'Tagリストを読み込めませんでした。', 'error');
      }

      if (event.data?.type === 'settings.searchEngine.result' && event.data.settings) {
        searchEngineSettings = {
          provider: event.data.settings.provider ?? 'google',
          googleSearchUrlTemplate: event.data.settings.googleSearchUrlTemplate ?? 'https://www.google.com/search?q={query}',
          braveApiKey: event.data.settings.braveApiKey ?? '',
          geminiApiKey: event.data.settings.geminiApiKey ?? ''
        };
      }

      if (event.data?.type === 'settings.searchEngine.error') {
        showExplorerToast(event.data.message ?? '検索エンジン設定を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.gid.result' && event.data.settings) {
        const currentDigitCount = Math.max(4, Math.min(8, Math.round(Number(event.data.settings.digitCount) || 6)));
        gidSettings = {
          targetExtensions: String(event.data.settings.targetExtensions ?? 'zip;rar;7z;cbz;cbr'),
          digitCount: currentDigitCount
        };
        if (!gidMigrationInProgress && !gidMigrationPreview) {
          gidMigrationTargetDigitCount = currentDigitCount;
        }
        if (event.data.updated) showExplorerToast('gid管理設定を保存しました。', 'success');
      }

      if (event.data?.type === 'settings.gid.error') {
        showExplorerToast(event.data.message ?? 'gid管理設定を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.gid.migration.preview.result' && event.data.preview) {
        gidMigrationPreviewInProgress = false;
        gidMigrationPreview = event.data.preview as GidMigrationPreview;
      }

      if (event.data?.type === 'settings.gid.migration.progress') {
        const completed = Number(event.data.completed ?? 0);
        const total = Number(event.data.total ?? 0);
        updateGidMigrationProgress(String(event.data.phase ?? ''), completed, total);
        gidMigrationProgress = String(event.data.message ?? (total > 0 ? `${completed}/${total}` : 'GID移行中...'));
      }

      if (event.data?.type === 'settings.gid.migration.result') {
        gidMigrationInProgress = false;
        gidMigrationPreviewInProgress = false;
        gidMigrationPreview = null;
        gidMigrationProgress = '';
        finishGidMigrationProgressTimer();
        const result = event.data.result;
        const deletedMissingItemCount = Number(result?.deletedMissingItemCount ?? 0);
        const targetDigitCount = Number(result?.targetDigitCount ?? gidMigrationTargetDigitCount);
        showExplorerToast(
          `${Number(result?.migratedItemCount ?? 0).toLocaleString()}件を${targetDigitCount}桁GIDへ移行しました。${deletedMissingItemCount > 0 ? `\n実体のないDB作品を${deletedMissingItemCount.toLocaleString()}件抹消しました。` : ''}\nDBバックアップ: ${String(result?.backupPath ?? '')}`,
          'success'
        );
      }

      if (event.data?.type === 'settings.gid.migration.error') {
        gidMigrationInProgress = false;
        gidMigrationPreviewInProgress = false;
        gidMigrationProgress = '';
        finishGidMigrationProgressTimer();
        showExplorerToast(event.data.message ?? 'GID移行を完了できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.gid.migration.closeBlocked') {
        showExplorerToast('GID移行中はアプリを終了できません。完了するまでお待ちください。', 'error');
      }

      if (event.data?.type === 'settings.winrar.result' && event.data.settings) {
        winRarSettings = {
          executablePath: event.data.settings.executablePath ?? '',
          supportedExtensions: removeExtensionDots(event.data.settings.supportedExtensions ?? 'zip,rar,7z,cbz,cbr'),
          showOpenInContextMenu: Boolean(event.data.settings.showOpenInContextMenu)
        };
      }

      if (event.data?.type === 'settings.winrar.pickExecutable.result' && typeof event.data.path === 'string') {
        winRarSettings = { ...winRarSettings, executablePath: event.data.path };
      }

      if (event.data?.type === 'settings.ffmpeg.result' && event.data.settings) {
        ffmpegSettings = {
          executablePath: event.data.settings.executablePath ?? '',
          supportedExtensions: removeExtensionDots(event.data.settings.supportedExtensions ?? 'mp4,mkv,avi,mov,wmv,webm,flv,m4v,mpeg,mpg,ts')
        };
        ffmpegAvailable = Boolean(event.data.isAvailable);
      }

      if (event.data?.type === 'settings.ffmpeg.pickExecutable.result' && typeof event.data.path === 'string') {
        ffmpegSettings = { ...ffmpegSettings, executablePath: event.data.path };
      }

      if (event.data?.type === 'settings.thumbnailCache.result') {
        thumbnailCacheTargets = event.data.targets ?? [];
        thumbnailCacheRoot = event.data.cacheRoot ?? '';
        thumbnailCacheRootDraft = '';
      }

      if (event.data?.type === 'settings.sqliteDatabase.result') {
        sqliteDatabasePath = event.data.settings?.databasePath ?? '';
        sqliteDatabasePathDraft = '';
        sqliteCacheDatabasePath = event.data.settings?.cacheDatabasePath ?? '';
        sqliteConfiguredCacheDatabasePath = event.data.settings?.configuredCacheDatabasePath ?? sqliteCacheDatabasePath;
        sqliteCacheDatabasePathDraft = '';
        sqliteCacheDatabaseRestartRequired = Boolean(event.data.settings?.cacheDatabaseRestartRequired);
        databaseScanSchedules = Array.isArray(event.data.settings?.scanSchedules)
          ? event.data.settings.scanSchedules.map((schedule: DatabaseScanSchedule) => ({
              id: String(schedule.id ?? ''),
              weekdays: Array.isArray(schedule.weekdays) ? schedule.weekdays.map(Number) : [],
              time: String(schedule.time ?? '03:00'),
              categories: Array.isArray(schedule.categories) ? schedule.categories.map(String) : [],
              lastStartedAt: schedule.lastStartedAt ?? null
            }))
          : [];
      }

      if (event.data?.type === 'settings.sqliteDatabase.schedules.saved') {
        databaseScanScheduleSaving = false;
        sqliteDatabaseStatus = event.data.message ?? 'フォルダ走査スケジュールを保存しました。';
      }

      if (event.data?.type === 'settings.sqliteDatabase.schedules.error') {
        databaseScanScheduleSaving = false;
        sqliteDatabaseStatus = event.data.message ?? 'フォルダ走査スケジュールを保存できませんでした。';
      }

      if (event.data?.type === 'settings.sqliteDatabase.schedule.started') {
        sqliteDatabaseBusy = true;
        sqliteDatabaseUpdateInProgress = true;
        sqliteDatabaseCancelRequested = false;
        sqliteDatabaseStatus = event.data.message ?? '定期フォルダ走査を開始しました。';
        sqliteDatabaseProgressLog = [sqliteDatabaseStatus];
        startDatabaseScheduledScanToast(
          sqliteDatabaseStatus,
          Number.isFinite(Number(event.data.estimatedDurationSeconds))
            ? Number(event.data.estimatedDurationSeconds)
            : null);
      }

      if (event.data?.type === 'settings.sqliteDatabase.schedule.finished') {
        const elapsedSeconds = Math.max(0, Number(event.data.elapsedSeconds ?? 0));
        finishDatabaseScheduledScanToast();
        sqliteDatabaseBusy = false;
        sqliteDatabaseUpdateInProgress = false;
        sqliteDatabaseCancelRequested = false;
        sqliteDatabaseStatus = event.data.message ?? '定期フォルダ走査を完了しました。';
        appendSqliteDatabaseProgress(sqliteDatabaseStatus);
        showExplorerToast(
          elapsedSeconds > 0
            ? `${sqliteDatabaseStatus}\n${translateSystemText('所要時間', appLanguage)} ${formatGidMigrationDuration(elapsedSeconds)}`
            : sqliteDatabaseStatus,
          'success',
          5_000);
        loadGalleryWorks();
        if (activeView === 'creators') loadGalleryCreatorSummaries(true);
      }

      if (event.data?.type === 'settings.sqliteDatabase.schedule.error') {
        finishDatabaseScheduledScanToast();
        sqliteDatabaseBusy = false;
        sqliteDatabaseUpdateInProgress = false;
        sqliteDatabaseCancelRequested = false;
        sqliteDatabaseStatus = event.data.message ?? '定期フォルダ走査を完了できませんでした。';
        appendSqliteDatabaseProgress(sqliteDatabaseStatus);
        showExplorerToast(sqliteDatabaseStatus, 'error', 7_000);
      }

      if (event.data?.type === 'settings.pcloud.result') {
        pCloudApiHost = event.data.settings?.apiHost ?? 'eapi.pcloud.com';
        pCloudTargetFolder = event.data.settings?.targetFolder ?? '';
        pCloudClientId = event.data.settings?.clientId ?? '';
        pCloudOAuthRedirectUri = event.data.settings?.redirectUri ?? '';
        pCloudHasAccessToken = Boolean(event.data.settings?.hasAccessToken);
        pCloudAccountEmail = event.data.settings?.accountEmail ?? '';
        pCloudUsedQuotaBytes = event.data.settings?.usedQuotaBytes ?? null;
        pCloudQuotaBytes = event.data.settings?.quotaBytes ?? null;
        pCloudLastBackupAt = event.data.settings?.lastBackupAt ?? '';
        pCloudLastBackupFileName = event.data.settings?.lastBackupFileName ?? '';
        pCloudAutoBackupEnabled = Boolean(event.data.settings?.autoBackupEnabled);
        pCloudCheckIntervalMinutes = Number(event.data.settings?.checkIntervalMinutes ?? 60);
        pCloudBackupIntervalDays = Number(event.data.settings?.backupIntervalDays ?? 1);
        pCloudMaximumSnapshots = Number(event.data.settings?.maximumSnapshots ?? 7);
        pCloudIdleThresholdMinutes = Number(event.data.settings?.idleThresholdMinutes ?? 10);
      }

      if (event.data?.type === 'settings.pcloud.snapshots.result') {
        pCloudSnapshots = Array.isArray(event.data.snapshots) ? event.data.snapshots : [];
        pCloudSnapshotsLoaded = true;
      }

      if (event.data?.type === 'settings.pcloud.operation.progress') {
        pCloudBusy = true;
        pCloudStatus = event.data.message ?? 'pCloudバックアップを処理中...';
      }

      if (event.data?.type === 'settings.pcloud.autoBackup.started') {
        pCloudBusy = true;
        pCloudStatus = event.data.message ?? 'pCloudへSQLiteDBをバックアップしています...';
        pCloudAutoBackupToastMessage = translateSystemText(pCloudStatus, appLanguage);
        showExplorerToast(pCloudStatus, 'progress', null);
      }

      if (event.data?.type === 'settings.pcloud.autoBackup.finished') {
        pCloudBusy = false;
        pCloudStatus = '';
        if (pCloudAutoBackupToastMessage && explorerToastKind === 'progress' && explorerToastMessage === pCloudAutoBackupToastMessage) {
          explorerToastMessage = '';
        }
        pCloudAutoBackupToastMessage = '';
      }

      if (event.data?.type === 'settings.pcloud.operation.result') {
        pCloudBusy = false;
        pCloudAccessTokenDraft = '';
        pCloudStatus = event.data.message ?? '';
      }

      if (event.data?.type === 'settings.pcloud.operation.error') {
        pCloudBusy = false;
        pCloudStatus = event.data.message ?? 'pCloudの操作を完了できませんでした。';
        if (event.data.action === 'autoBackup') {
          pCloudAutoBackupToastMessage = '';
          showExplorerToast(pCloudStatus, 'error', 5_000);
        }
      }

      if (event.data?.type === 'settings.sqliteDatabase.update.progress') {
        const message = event.data.message ?? 'SQLiteDBを更新中...';
        sqliteDatabaseStatus = message;
        appendSqliteDatabaseProgress(message);
      }
      if (event.data?.type === 'settings.sqliteDatabase.merge.picked') {
        sqliteMergeDatabasePath = String(event.data.path ?? '');
      }

      if (event.data?.type === 'settings.sqliteDatabase.operation.result') {
        sqliteDatabaseBusy = false;
        sqliteDatabaseUpdateInProgress = false;
        sqliteDatabaseCancelRequested = false;
        sqliteDatabaseStatus = event.data.message ?? '';
        if (event.data.message) {
          appendSqliteDatabaseProgress(event.data.message);
        }
        if (event.data.action === 'merge') {
          sqliteMergeDatabasePath = '';
        }
        if (event.data.action === 'update' || event.data.action === 'move' || event.data.action === 'merge') {
          loadGalleryWorks();
          if (activeView === 'creators') loadGalleryCreatorSummaries(true);
        }
      }

      if (event.data?.type === 'settings.sqliteDatabase.operation.error') {
        sqliteDatabaseBusy = false;
        sqliteDatabaseUpdateInProgress = false;
        sqliteDatabaseCancelRequested = false;
        sqliteDatabaseStatus = event.data.message ?? 'SQLiteDBの操作を完了できませんでした。';
        appendSqliteDatabaseProgress(sqliteDatabaseStatus);
      }

      if (event.data?.type === 'settings.sqliteDatabase.operation.cancelled') {
        const wasScheduledScan = databaseScheduledScanInProgress;
        finishDatabaseScheduledScanToast();
        sqliteDatabaseBusy = false;
        sqliteDatabaseUpdateInProgress = false;
        sqliteDatabaseCancelRequested = false;
        sqliteDatabaseStatus = event.data.message ?? 'SQLiteDBの更新を中断しました。';
        appendSqliteDatabaseProgress(sqliteDatabaseStatus);
        if (wasScheduledScan) {
          showExplorerToast(sqliteDatabaseStatus, 'success', 5_000);
        }
      }

      if (event.data?.type === 'settings.galleryTargets.result') {
        const sectionChanges = applyGallerySectionDefinitions(event.data.sections, event.data.selectedSectionId);
        galleryScanTargets = event.data.targets ?? [];
        galleryScanSettings = event.data.settings ?? [];
        selectGalleryTargetCategory(galleryTargetCategory);
        if (sectionChanges.galleryChanged) loadGalleryWorks();
        if (sectionChanges.creatorChanged && activeView === 'creators') loadGalleryCreatorSummaries();
        if (sectionChanges.metricsChanged && activeView === 'userMetrics') loadUserMetrics();
        if (event.data.sectionAction === 'created') {
          showExplorerToast('区分を追加しました。', 'success');
        }
        else if (event.data.sectionAction === 'renamed') {
          showExplorerToast('区分名を変更しました。', 'success');
        }
        else if (event.data.sectionAction === 'deleted') {
          showExplorerToast('区分を削除しました。本体DBの作品データは保持されています。', 'success');
        }
      }

      if (event.data?.type === 'settings.galleryTargets.error') {
        showExplorerToast(event.data.message ?? 'ギャラリー対象を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.gallerySections.error') {
        showExplorerToast(event.data.message ?? '区分を更新できませんでした。', 'error');
        postHostMessage({ type: 'settings.galleryTargets.list' });
      }

      if (event.data?.type === 'settings.thumbnailAdjustments.result') {
        thumbnailCropAdjustments = event.data.adjustments ?? [];
        selectThumbnailAdjustmentCategory(thumbnailAdjustmentCategory);
        if (event.data.updated) {
          galleryThumbnails = {};
          requestedGalleryThumbnailIds.clear();
          unavailableGalleryThumbnailIds.clear();
          galleryThumbnailRevision += 1;
          loadGalleryWorks();
        }
      }

      if (event.data?.type === 'settings.thumbnailAdjustments.error') {
        showExplorerToast(event.data.message ?? 'サムネイル調整を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.thumbnailCache.rebuild.progress') {
        thumbnailCacheStatus = `再構築中: ${event.data.completed ?? 0}/${event.data.total ?? 0}`;
      }

      if (event.data?.type === 'settings.thumbnailCache.operation.result') {
        thumbnailCacheStatus = event.data.message ?? '';
        if (event.data.action === 'rebuild' || event.data.action === 'moveRoot') {
          explorerThumbnails = {};
          requestedExplorerThumbnailPaths.clear();
          unavailableExplorerThumbnailPaths.clear();
          if (explorerPath) {
            loadExplorer(explorerPath);
          }
          if (explorerSplit) {
            loadSplitExplorer(explorerSplit.rightPath);
          }
        }
      }

      if (event.data?.type === 'settings.thumbnailCache.operation.error') {
        thumbnailCacheStatus = event.data.message ?? 'キャッシュ操作を完了できませんでした。';
      }

      if (event.data?.type === 'settings.externalApps.pickExecutable.result' && typeof event.data.path === 'string') {
        programExecutablePath = event.data.path;
      }

      if (event.data?.type === 'settings.newTabCandidates.result') {
        newTabCandidates = event.data.candidates ?? [];
      }

      if (event.data?.type === 'settings.newTabCandidates.error') {
        showExplorerToast(event.data.message ?? '新規タブ候補を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.theme.result') {
        appliedThemeSettings = parseThemeSettings(event.data.settings);
        themeSettingsDraft = { ...appliedThemeSettings };
        applyThemeToDocument(appliedThemeSettings);
        if (themeSavePending) {
          showExplorerToast('テーマ設定を保存し、アプリ全体へ適用しました。', 'success');
        }
        themeSavePending = false;
      }

      if (event.data?.type === 'settings.theme.error') {
        themeSavePending = false;
        showExplorerToast(event.data.message ?? 'テーマ設定を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.language.result') {
        appLanguage = normalizeLanguage(event.data.settings?.language);
        applySystemLanguage(appLanguage);
        if (languageSavePending || event.data.updated) {
          showExplorerToast('言語設定を保存しました。', 'success');
        }
        languageSavePending = false;
      }

      if (event.data?.type === 'settings.language.error') {
        languageSavePending = false;
        showExplorerToast(event.data.message ?? '言語設定を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'settings.calendar.result') {
        calendarSettings = parseCalendarSettings(event.data.settings);
        calendarWeekStartDraft = calendarSettings.weekStartDay;
        googleCalendarSyncFeatureEnabled = event.data.googleSyncFeatureEnabled !== false;
        applyGoogleCalendarSettings(event.data.google);
        if (event.data.updated) {
          showExplorerToast('Calendar設定を保存しました。', 'success');
          if (activeView === 'calendar') loadCalendarSubscriptions();
        }
      }

      if (event.data?.type === 'settings.calendar.error') {
        showExplorerToast(event.data.message ?? 'Calendar設定を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'calendar.google.operation.progress') {
        googleCalendarBusy = true;
        googleCalendarStatus = event.data.message ?? 'Google Calendarを処理しています...';
      }

      if (event.data?.type === 'calendar.google.operation.result') {
        googleCalendarBusy = false;
        googleCalendarClientSecretDraft = '';
        googleCalendarStatus = event.data.message ?? '';
        showExplorerToast(googleCalendarStatus, 'success');
      }

      if (event.data?.type === 'calendar.google.sync.started') {
        googleCalendarBusy = true;
        googleCalendarStatus = event.data.message ?? 'Google Calendarへ同期しています...';
      }

      if (event.data?.type === 'calendar.google.sync.result') {
        googleCalendarBusy = false;
        googleCalendarStatus = event.data.message ?? 'Google Calendarと同期しました。';
        if (activeView === 'calendar') loadCalendarSubscriptions();
        if (!event.data.automatic) showExplorerToast(googleCalendarStatus, 'success');
      }

      if (event.data?.type === 'calendar.google.operation.error') {
        googleCalendarBusy = false;
        googleCalendarStatus = event.data.message ?? 'Google Calendarの操作を完了できませんでした。';
        googleCalendarLastSyncError = googleCalendarStatus;
        if (!event.data.automatic) showExplorerToast(googleCalendarStatus, 'error');
      }

      if (event.data?.type === 'ui.navigation.result') {
        const restoredView = event.data.state?.activeView;
        if (restoredView === 'library' || restoredView === 'bookmarks' || restoredView === 'creators' || restoredView === 'creatorTracking' || restoredView === 'explorer' || restoredView === 'filters' || restoredView === 'tags' || restoredView === 'userMetrics' || restoredView === 'board' || restoredView === 'calendar' || restoredView === 'settings' || restoredView === 'userGuide') {
          activeView = restoredView;
          if (restoredView === 'creators' || restoredView === 'creatorTracking') {
            loadGalleryCreatorSummaries();
          }
          if (restoredView === 'userMetrics') {
            loadUserMetrics();
          }
          if (restoredView === 'board') {
            loadStickyNoteBoard();
          }
          if (restoredView === 'calendar') {
            loadCalendarSubscriptions();
          }
        }
        else if (restoredView === 'programs') {
          activeView = 'settings';
        }
        explorerBookmarksExpanded = Boolean(event.data.state?.explorerBookmarksExpanded);
        const restoredColumns = parseExplorerDetailColumns(event.data.state?.explorerDetailColumns);
        if (restoredColumns.length > 0) {
          explorerDetailColumns = restoredColumns.join(',') === 'icon,name,gid,modified,type,size'
            ? [...defaultExplorerDetailColumns]
            : restoredColumns;
        }
        mouseGestureSettings = parseMouseGestureSettings(event.data.state?.mouseGestureSettings);
        keyboardShortcutSettings = parseKeyboardShortcutSettings(event.data.state?.keyboardShortcutSettings);
        galleryCardColumnModes = parseGalleryCardColumnModes(event.data.state?.galleryCardColumns);
        galleryFilterSorts = parseGalleryFilterSortState(event.data.state?.galleryFilterSorts);
        galleryThumbnailSorts = parseGalleryThumbnailSortState(event.data.state?.galleryThumbnailSorts);
        const creatorTrackingTabsState = parseCreatorTrackingSessionState(event.data.state?.creatorTrackingTabs);
        if (creatorTrackingTabsState && creatorTrackingTabsState.tabs.length > 0) {
          pendingCreatorTrackingSessionRestore = { state: creatorTrackingTabsState, activate: restoredView === 'creatorTracking' };
          if (galleryCreatorSummaries.length > 0 && !galleryCreatorSummaryIsLoading) {
            const pending = pendingCreatorTrackingSessionRestore;
            pendingCreatorTrackingSessionRestore = null;
            restoreCreatorTrackingSession(pending.state, pending.activate);
          }
          else if (!galleryCreatorSummaryIsLoading) {
            loadGalleryCreatorSummaries();
          }
        }
        const restoredExplorerCardColumns = Number(event.data.state?.explorerCardColumns);
        if ([4, 5, 6, 7].includes(restoredExplorerCardColumns)) {
          explorerCardColumns = restoredExplorerCardColumns;
        }
        loadGalleryWorks();
      }

        if (event.data?.type === 'explorer.list.result') {
          if (event.data.pane === 'split-right' && explorerSplit) {
          const rightTabId = explorerSplit.rightTabId;
            explorerSplit = {
            ...explorerSplit,
            rightPath: event.data.path ?? '',
            rightParentPath: event.data.parentPath ?? null,
            rightEntries: event.data.entries ?? [],
            rightSelectedPaths: [],
            rightIsLoading: false,
              rightIsTruncated: event.data.isTruncated ?? false
            };
            if (event.data.message) {
              showExplorerToast(event.data.message, 'success');
            }
          syncExplorerTabById(rightTabId, event.data.path ?? '');
          rememberExplorerHistory(event.data.path ?? '', 'right');
            prefetchExplorerThumbnails(explorerSplit.rightEntries, 'right');
            continueRenameAfterRefresh('right', explorerSplit.rightEntries);
            finishExplorerPaste('right', event.data.path ?? '');
            finishPendingMoveRefresh('right', event.data.path ?? '');
            if (!rarToZipBatchInProgress) finishWinRarProgress();
            focusPastedExplorerEntries(event.data.focusPaths ?? [], 'right');
            restoreExplorerSplitScroll();
            return;
          }

        explorerPath = event.data.path ?? '';
        explorerPathDraft = explorerPath;
        explorerParentPath = event.data.parentPath ?? null;
        explorerRoots = event.data.roots ?? [];
        explorerEntries = event.data.entries ?? [];
        explorerIsTruncated = event.data.isTruncated ?? false;
        if (event.data.message) {
          showExplorerToast(event.data.message, 'success');
        }
        explorerIsLoading = false;
        selectedPaths = [];
        prefetchExplorerThumbnails(explorerEntries, 'left');
        syncExplorerTab(explorerPath);
        rememberExplorerHistory(explorerPath, 'left');
        continueRenameAfterRefresh('left', explorerEntries);
          finishExplorerPaste('left', explorerPath);
          finishPendingMoveRefresh('left', explorerPath);
          restoreExplorerTabScroll();
          restoreExplorerSplitScroll();
          if (!rarToZipBatchInProgress) finishWinRarProgress();
          focusPastedExplorerEntries(event.data.focusPaths ?? [], 'left');
        }

      if (event.data?.type === 'explorer.thumbnail.result') {
        requestedExplorerThumbnailPaths.delete(event.data.path);
        if (event.data.thumbnailUri) {
          unavailableExplorerThumbnailPaths.delete(event.data.path);
          explorerThumbnails = { ...explorerThumbnails, [event.data.path]: event.data.thumbnailUri };
        }
        else {
          unavailableExplorerThumbnailPaths.add(event.data.path);
        }
      }

      if (event.data?.type === 'explorer.thumbnail.action.result') {
        showExplorerToast(event.data.message ?? '', 'success');
        refreshExplorerThumbnail(event.data.thumbnailPath ?? '');
      }

      if (event.data?.type === 'explorer.bookmarks.result') {
        explorerBookmarks = event.data.bookmarks ?? [];
      }

      if (event.data?.type === 'view.bookmarks.result') {
        viewBookmarks = (event.data.bookmarks ?? []).filter((bookmark: ViewBookmark) => isBookmarkableView(bookmark.viewType));
        if (bookmarkMutationMessage) {
          showExplorerToast(bookmarkMutationMessage, 'success');
          bookmarkMutationMessage = '';
        }
      }

      if (event.data?.type === 'stickyNotes.result') {
        stickyNotes = (event.data.notes ?? [])
          .map((note: Partial<StickyNoteItem>) => normalizeStickyNote(note))
          .filter((note: StickyNoteItem | null): note is StickyNoteItem => note !== null);
        requestAnimationFrame(clampStickyNotesToViewport);
      }

      if (event.data?.type === 'stickyNotes.created') {
        const note = normalizeStickyNote(event.data.note ?? {});
        if (note) {
          stickyNotes = [...stickyNotes.filter((candidate) => candidate.id !== note.id), note];
          requestAnimationFrame(() => {
            document.querySelector<HTMLTextAreaElement>(`[data-sticky-note-id="${note.id}"] textarea`)?.focus();
          });
        }
      }

      if (event.data?.type === 'creator.tracking.storage.result' &&
          event.data.requestId === pendingGalleryCreatorStorageRequest?.requestId) {
        const pending = pendingGalleryCreatorStorageRequest;
        pendingGalleryCreatorStorageRequest = null;
        const locations = normalizeCreatorTrackingStorageLocations(
          Array.isArray(event.data.storageLocations) ? event.data.storageLocations : [],
          '',
          '',
          '');
        const paths = locations
          .filter(location => location.usage === 'Gallery' || location.usage === 'Stockroom')
          .map(location => location.path.trim())
          .filter(Boolean);
        requestExplorerPathsOpen(
          paths,
          false,
          true,
          `${pending.creator}のGalleryまたはStockroomフォルダを開けませんでした。`);
      }

      if (event.data?.type === 'creator.tracking.storage.error' &&
          event.data.requestId === pendingGalleryCreatorStorageRequest?.requestId) {
        pendingGalleryCreatorStorageRequest = null;
        showExplorerToast(event.data.message ?? 'Creator Trackingのストレージ情報を取得できませんでした。', 'error');
      }

      if (event.data?.type === 'stickyNotes.error') {
        showExplorerToast(event.data.message ?? '付箋を保存できませんでした。', 'error');
      }

      if (event.data?.type === 'stickyNotes.board.result') {
        stickyNoteBoardItems = (event.data.items ?? [])
          .map((item: { note?: Partial<StickyNoteItem>; hasLiveNote?: boolean; bookmarkCount?: number }) => {
            const note = normalizeStickyNote(item.note ?? {});
            return note
              ? { note, hasLiveNote: Boolean(item.hasLiveNote), bookmarkCount: Number(item.bookmarkCount ?? 0) }
              : null;
          })
          .filter((item: StickyNoteBoardItem | null): item is StickyNoteBoardItem => item !== null);
        stickyNoteBoardIsLoading = false;
        stickyNoteBoardError = '';
      }

      if (event.data?.type === 'stickyNotes.board.error') {
        stickyNoteBoardIsLoading = false;
        stickyNoteBoardError = event.data.message ?? 'Boardの付箋を更新できませんでした。';
        showExplorerToast(stickyNoteBoardError, 'error');
      }

      if (event.data?.type === 'view.bookmarks.error') {
        bookmarkMutationMessage = '';
        showExplorerToast(event.data.message ?? 'Bookmarkを保存できませんでした。', 'error');
      }

      if (event.data?.type === 'view.bookmarks.paths.result' && event.data.requestId === pendingExplorerBookmarkRestore?.requestId) {
        const pending = pendingExplorerBookmarkRestore;
        pendingExplorerBookmarkRestore = null;
        restoreExplorerBookmark(
          pending.bookmark,
          pending.state,
          event.data.validPaths ?? [],
          event.data.invalidPaths ?? []);
      }

      if (event.data?.type === 'explorer.tabs.result') {
        const restoredTabs = (event.data.tabs ?? []).map((tab: { path: string; label: string; isActive: boolean }) => ({
          id: `explorer-tab-${nextExplorerTabId++}`,
          path: tab.path,
          label: tab.label
        }));
        if (restoredTabs.length > 0) {
          explorerTabs = restoredTabs;
          activeExplorerTabId = restoredTabs.find((_, index) => event.data.tabs[index]?.isActive)?.id ?? restoredTabs[0].id;
        }
        else if (explorerPath) {
          const currentTab = { id: `explorer-tab-${nextExplorerTabId++}`, path: explorerPath, label: explorerPath };
          explorerTabs = [currentTab];
          activeExplorerTabId = currentTab.id;
        }
        else {
          explorerTabs = [];
          activeExplorerTabId = '';
        }
        explorerTabsRestored = true;

        if (activeView === 'explorer' && !explorerPath && activeExplorerTabId) {
          const activeTab = explorerTabs.find((tab) => tab.id === activeExplorerTabId);
          if (activeTab) loadExplorer(activeTab.path);
        }
        else if (explorerTabs.length > 0 && restoredTabs.length === 0) {
          persistExplorerTabs();
        }
      }

      if (event.data?.type === 'explorer.operation.result') {
        showExplorerToast(event.data.message ?? '', 'success');
      }

      if (event.data?.type === 'explorer.paths.validate.result' &&
          event.data.requestId === pendingExplorerPathsOpen?.requestId) {
        const pending = pendingExplorerPathsOpen;
        pendingExplorerPathsOpen = null;
        const validSet = new Set((event.data.validPaths ?? []).map((path: unknown) => normalizeWindowsPath(String(path))));
        const validPaths = pending.paths.filter(path => validSet.has(normalizeWindowsPath(path)));
        const invalidPaths = pending.paths.filter(path => !validSet.has(normalizeWindowsPath(path)));
        if (validPaths.length === 0 || (pending.requireAll && invalidPaths.length > 0)) {
          const details = invalidPaths.length > 0 ? `\n見つからないフォルダ: ${invalidPaths.join(' / ')}` : '';
          showExplorerToast(`${pending.failureMessage}${details}`, 'error');
        }
        else {
          openValidatedExplorerPaths(validPaths, pending.splitWhenTwo);
          if (invalidPaths.length > 0) {
            showExplorerToast(`存在するフォルダのみExplorerで開きました。\n見つからないフォルダ: ${invalidPaths.join(' / ')}`, 'success');
          }
        }
      }

      if (event.data?.type === 'explorer.transfer.progress') {
        const transferredBytes = Number(event.data.bytesTransferred ?? 0);
        const totalBytes = Number(event.data.totalBytes ?? 0);
        const completedFiles = Number(event.data.completedFiles ?? 0);
        const totalFiles = Number(event.data.totalFiles ?? 0);
        const percent = totalBytes > 0 ? Math.min(100, Math.round((transferredBytes / totalBytes) * 100)) : 100;
        const currentPath = String(event.data.currentPath ?? '');
        const currentName = currentPath.replace(/^.*[\\/]/, '') || '準備中...';
        const operation = String(event.data.operation ?? 'コピー');
        showExplorerToast(
          `${operation}中 ${formatSize(transferredBytes)} / ${formatSize(totalBytes)} (${percent}%)\n${completedFiles}/${totalFiles} ファイル: ${currentName}`,
          'progress',
          null
        );
      }

      if (event.data?.type === 'explorer.winrar.progress') {
        updateWinRarProgress(event.data.message ?? 'WinRAR: 処理中');
      }

      if (event.data?.type === 'explorer.winrar.convertRarToZip.result') {
        rarToZipBatchInProgress = false;
        finishWinRarProgress();
        const directory = String(event.data.directory ?? '');
        const pane = event.data.pane === 'split-right' ? 'right' : 'left';
        if (directory) {
          if (pane === 'right' && explorerSplit &&
              normalizeWindowsPath(explorerSplit.rightPath) === normalizeWindowsPath(directory)) {
            loadSplitExplorer(directory);
          }
          else if (pane === 'left' &&
                   normalizeWindowsPath(explorerPath) === normalizeWindowsPath(directory)) {
            loadExplorer(directory);
          }
        }
        showExplorerToast(
          event.data.message ?? 'RARからZIPへの変換を完了しました。',
          Number(event.data.successCount ?? 0) === 0 || event.data.hasWarnings === true ? 'error' : 'success'
        );
      }

      if (event.data?.type === 'explorer.gid.assign.result') {
        gidAssignmentInProgress = false;
        gidAssignmentConfirmation = null;
        showExplorerToast(
          event.data.message ?? 'gid発行とDB同期を完了しました。',
          event.data.hasWarnings === true ? 'error' : 'success'
        );
      }

      if (event.data?.type === 'explorer.creatorFolder.convert.result') {
        creatorFolderConversionInProgress = false;
        creatorFolderConversionConfirmation = null;
        showExplorerToast(
          event.data.message ?? '作者フォルダ化を完了しました。',
          event.data.hasWarnings === true ? 'error' : 'success'
        );
      }

      if (event.data?.type === 'explorer.creator.reassign.result') {
        creatorReassignmentInProgress = false;
        creatorReassignmentConfirmation = null;
        showExplorerToast(
          event.data.message ?? '作者情報を付け替えました。',
          event.data.hasWarnings === true ? 'error' : 'success'
        );
        loadGalleryWorks(false, galleryRatingFilters, true);
      }

      if (event.data?.type === 'explorer.dbManagement.progress') {
        showExplorerToast(event.data.message ?? 'DB管理機能を実行しています。', 'progress', null);
      }

      if (event.data?.type === 'explorer.operation.error') {
        gidAssignmentInProgress = false;
        gidAssignmentConfirmation = null;
        creatorFolderConversionInProgress = false;
        creatorFolderConversionConfirmation = null;
        creatorReassignmentInProgress = false;
        creatorReassignmentConfirmation = null;
        if (event.data.operation === 'explorer.winrar.convertRarToZip') {
          rarToZipBatchInProgress = false;
          finishWinRarProgress();
        }
        else if (!rarToZipBatchInProgress) {
          finishWinRarProgress();
        }
        showExplorerToast(event.data.message ?? '操作を完了できませんでした。', 'error');
        explorerIsLoading = false;
        if (explorerSplit) {
          explorerSplit = { ...explorerSplit, rightIsLoading: false };
        }
        pendingMoveRefresh = null;
        pendingRenameNavigation = null;
        explorerPasteInProgress = null;
      }
    });

    postHostMessage({ type: 'app.ready' });
    loadGalleryPins(gallerySection);
    loadGalleryWorks();
    postHostMessage({ type: 'settings.externalApps.list' });
    postHostMessage({ type: 'settings.gid.list' });
    postHostMessage({ type: 'filters.editor.list' });
    postHostMessage({ type: 'tags.manager.list' });
    postHostMessage({ type: 'settings.winrar.list' });
    postHostMessage({ type: 'settings.ffmpeg.list' });
    postHostMessage({ type: 'settings.searchEngine.list' });
    postHostMessage({ type: 'settings.thumbnailCache.list' });
    postHostMessage({ type: 'settings.sqliteDatabase.list' });
    postHostMessage({ type: 'settings.pcloud.list' });
    postHostMessage({ type: 'settings.galleryTargets.list' });
    postHostMessage({ type: 'settings.thumbnailAdjustments.list' });
    postHostMessage({ type: 'settings.newTabCandidates.list' });
    postHostMessage({ type: 'settings.creatorTracking.list' });
    postHostMessage({ type: 'settings.theme.load' });
    postHostMessage({ type: 'settings.language.load' });
    postHostMessage({ type: 'settings.calendar.load' });
    postHostMessage({ type: 'ui.navigation.load' });
    postHostMessage({ type: 'view.bookmarks.list' });
    postHostMessage({ type: 'stickyNotes.list' });
    postHostMessage({ type: 'explorer.bookmarks.list' });
    postHostMessage({ type: 'explorer.tabs.list' });

    const onKeydown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && gidAssignmentConfirmation && !gidAssignmentInProgress) {
        event.preventDefault();
        gidAssignmentConfirmation = null;
        return;
      }
      if (event.key === 'Escape' && creatorFolderConversionConfirmation && !creatorFolderConversionInProgress) {
        event.preventDefault();
        creatorFolderConversionConfirmation = null;
        return;
      }
      if (event.key === 'Escape' && galleryDeleteConfirmation) {
        event.preventDefault();
        closeGalleryDeleteConfirmation();
        return;
      }

      if (event.key === 'Escape' && galleryTagAssignment && activeView === 'library') {
        event.preventDefault();
        closeGalleryTagAssignment();
        return;
      }

      if (event.key === 'Escape' && galleryTitleAssignment) {
        event.preventDefault();
        closeGalleryTitleAssignment();
        return;
      }

      if (event.key === 'Escape' && galleryCharacterAssignment) {
        event.preventDefault();
        closeGalleryCharacterAssignment();
        return;
      }

      if (event.key === 'Escape' && galleryContextMenu) {
        event.preventDefault();
        closeGalleryContextMenu();
        return;
      }

      if (event.key === 'Escape' && globalSearchOpen) {
        event.preventDefault();
        globalSearchOpen = false;
        return;
      }

      if (handleGlobalShortcut(event)) return;
      handleCreatorTrackingShortcut(event);
      handleExplorerShortcut(event);
    };
    const onGalleryWheel = (event: WheelEvent) => {
      if ((activeView !== 'library' && activeView !== 'creators') || !event.ctrlKey) {
        return;
      }

      event.preventDefault();
      const now = Date.now();
      if (now - lastGalleryCardWheelChangeAt < 150) {
        return;
      }

      const activeSection = activeView === 'creators' ? galleryCreatorSummarySection : gallerySection;
      const currentColumns = galleryCardColumnModes[activeSection] ?? 7;
      const nextColumns = Math.min(9, Math.max(5, currentColumns + (event.deltaY > 0 ? 1 : -1)));
      if (nextColumns !== currentColumns) {
        setGalleryCardColumns(nextColumns, activeSection);
        lastGalleryCardWheelChangeAt = now;
      }
    };
    const dismissExplorerContextMenu = (event: PointerEvent) => {
      const target = event.target as Element | null;
      const isInsideExplorerContextMenu = Boolean(target?.closest('.explorer-context-menu'));
      const isInsideExplorerColumnsMenu = Boolean(target?.closest('.explorer-columns-menu'));
      if (!isInsideExplorerContextMenu && !isInsideExplorerColumnsMenu) {
        explorerContextMenu = null;
        explorerBlankContextMenu = null;
        explorerTabContextMenu = null;
        explorerColumnMenu = null;
        explorerFolderCreateSubmenuOpen = false;
        explorerCompressionSubmenuOpen = false;
        explorerThumbnailSubmenuOpen = false;
        explorerDbManagementSubmenuOpen = false;
        explorerGidSubmenuOpen = false;
      }
      if (!target?.closest('.explorer-tab-add-menu')) {
        explorerNewTabMenuOpen = false;
      }
      if (!target?.closest('.explorer-drive-selector')) {
        explorerDriveMenuOpen = false;
      }
      if (!target?.closest('.sticky-note-palette, .sticky-note-palette-toggle')) {
        stickyNotePaletteOpenId = null;
      }
      if (!target?.closest('.filter-editor-category-filter')) {
        filterEditorCategoryFilterMenuOpen = false;
      }
    };
    const updateGalleryFilterVisibility = () => {
      scheduleGalleryFilterVisibilityCheck();
      clampStickyNotesToViewport();
    };
    window.addEventListener('keydown', onKeydown);
    window.addEventListener('keydown', reportUserActivity);
    window.addEventListener('pointerdown', dismissExplorerContextMenu, true);
    window.addEventListener('pointerdown', reportUserActivity, true);
    window.addEventListener('pointermove', updateStickyNoteInteraction);
    window.addEventListener('pointermove', reportUserActivity, { passive: true });
    window.addEventListener('pointerup', finishStickyNoteInteraction);
    window.addEventListener('pointercancel', finishStickyNoteInteraction);
    window.addEventListener('wheel', onGalleryWheel, { passive: false });
    window.addEventListener('wheel', reportUserActivity, { passive: true });
    window.addEventListener('focus', reportUserActivity);
    window.addEventListener('resize', updateGalleryFilterVisibility);
    requestAnimationFrame(() => {
      get(allocationVirtualizer).setOptions({ count: allocationVirtualItems.length });
    });
    return () => {
      finishGidMigrationProgressTimer();
      stopSystemLocalization();
      delete window.galleryBrowserFlushCreatorTracking;
      window.removeEventListener('keydown', onKeydown);
      window.removeEventListener('keydown', reportUserActivity);
      window.removeEventListener('pointerdown', dismissExplorerContextMenu, true);
      window.removeEventListener('pointerdown', reportUserActivity, true);
      window.removeEventListener('pointermove', updateStickyNoteInteraction);
      window.removeEventListener('pointermove', reportUserActivity);
      window.removeEventListener('pointerup', finishStickyNoteInteraction);
      window.removeEventListener('pointercancel', finishStickyNoteInteraction);
      window.removeEventListener('wheel', onGalleryWheel);
      window.removeEventListener('wheel', reportUserActivity);
      window.removeEventListener('focus', reportUserActivity);
      window.removeEventListener('resize', updateGalleryFilterVisibility);
    };
  });

  function filterItems(sourceItems: GalleryItem[], text: string) {
    const normalized = text.trim().toLowerCase();
    if (!normalized) {
      return sourceItems;
    }

    return sourceItems.filter((item) => {
      const tags = item.tags.join(' ');
      return `${item.title} ${item.path} ${tags}`.toLowerCase().includes(normalized);
    });
  }

  function filterExplorerEntries(sourceEntries: ExplorerEntry[], text: string) {
    const normalized = text.trim().toLowerCase();
    return normalized
      ? sourceEntries.filter((entry) =>
        entry.name.toLowerCase().includes(normalized) ||
        (entry.romanizedName ?? '').toLowerCase().includes(normalized))
      : sourceEntries;
  }

  function getExplorerBreadcrumbs(path: string): Array<{ label: string; path: string }> {
    const normalized = path.replace(/\//g, '\\').replace(/\\+$/, '');
    if (!normalized) {
      return [];
    }

    if (/^[a-z]:/i.test(normalized)) {
      const drive = normalized.slice(0, 2).toUpperCase();
      const parts = normalized.slice(2).split('\\').filter(Boolean);
      let currentPath = `${drive}\\`;
      const breadcrumbs = [{ label: drive, path: currentPath }];
      for (const part of parts) {
        currentPath = `${currentPath}${part}\\`;
        breadcrumbs.push({ label: part, path: currentPath });
      }
      return breadcrumbs;
    }

    if (normalized.startsWith('\\\\')) {
      const parts = normalized.slice(2).split('\\').filter(Boolean);
      if (parts.length === 0) {
        return [{ label: '\\\\', path: normalized }];
      }

      const server = parts.shift()!;
      const share = parts.shift();
      let currentPath = `\\\\${server}`;
      const breadcrumbs = [{ label: `\\\\${server}`, path: currentPath }];
      if (share) {
        currentPath += `\\${share}`;
        breadcrumbs.push({ label: share, path: currentPath });
      }
      for (const part of parts) {
        currentPath += `\\${part}`;
        breadcrumbs.push({ label: part, path: currentPath });
      }
      return breadcrumbs;
    }

    return [{ label: normalized, path: normalized }];
  }

  function getEntryNameParts(entry: ExplorerEntry): EntryNameParts {
    const match = /\{gid=([^{}]+)\}/i.exec(entry.name);
    if (!match || match.index === undefined) {
      const extension = getEntryExtension(entry.name, entry.isDirectory);
      return {
        displayName: extension ? entry.name.slice(0, -extension.length) : entry.name,
        identifier: '',
        extension,
        tagBeforeExtension: false
      };
    }

    const nameWithoutTag = `${entry.name.slice(0, match.index)}${entry.name.slice(match.index + match[0].length)}`.trim();
    const extension = getEntryExtension(nameWithoutTag, entry.isDirectory);
    const displayName = extension ? nameWithoutTag.slice(0, -extension.length) : nameWithoutTag;
    const extensionIndex = entry.name.lastIndexOf('.');
    return {
      displayName,
      identifier: match[1],
      extension,
      tagBeforeExtension: !entry.isDirectory && extensionIndex > match.index
    };
  }

  function getEntryExtension(name: string, isDirectory: boolean) {
    if (isDirectory) {
      return '';
    }

    const extensionIndex = name.lastIndexOf('.');
    return extensionIndex > 0 ? name.slice(extensionIndex) : '';
  }

  function getEntryDisplayName(entry: ExplorerEntry) {
    return getEntryNameParts(entry).displayName;
  }

  function getEntryIdentifier(entry: ExplorerEntry) {
    return getEntryNameParts(entry).identifier;
  }

  function buildEntryName(
    baseName: string,
    identifier: string,
    extension: string,
    tagBeforeExtension: boolean,
    isDirectory: boolean
  ) {
    const normalizedBaseName = baseName.trim();
    if (!identifier) {
      return `${normalizedBaseName}${extension}`;
    }

    const tag = `{gid=${identifier}}`;
    return tagBeforeExtension && !isDirectory
      ? `${normalizedBaseName}${tag}${extension}`
      : `${normalizedBaseName}${extension}${tag}`;
  }

  function sortExplorerEntries(sourceEntries: ExplorerEntry[], sort: string, direction: 'asc' | 'desc') {
    return [...sourceEntries].sort((left, right) => {
      if (left.isDirectory !== right.isDirectory) {
        return left.isDirectory ? -1 : 1;
      }

      let comparison: number;
      if (sort === 'modified') {
        comparison = new Date(left.modifiedAt).getTime() - new Date(right.modifiedAt).getTime();
      }
      else if (sort === 'size') {
        comparison = (left.size ?? -1) - (right.size ?? -1);
      }
      else if (sort === 'type') {
        comparison = (left.extension || 'folder').localeCompare(right.extension || 'folder', 'ja');
      }
      else {
        comparison = left.name.localeCompare(right.name, 'ja', { numeric: true, sensitivity: 'base' });
      }

      return direction === 'asc' ? comparison : -comparison;
    });
  }

  function requestThumbnail(item: GalleryItem) {
    if (item.thumbnailUri || requestedThumbnailIds.has(item.id)) {
      return;
    }

    requestedThumbnailIds.add(item.id);
    postHostMessage({ type: 'thumbnail.request', id: item.id });
  }

  function observeThumbnail(node: HTMLElement, item: GalleryItem) {
    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting) {
          requestThumbnail(item);
          observer.disconnect();
        }
      },
      { rootMargin: '700px' }
    );

    observer.observe(node);

    return {
      destroy() {
        observer.disconnect();
      }
    };
  }

  function requestExplorerThumbnail(entry: ExplorerEntry, pane: 'left' | 'right' = 'left') {
    if (explorerThumbnails[entry.path] || requestedExplorerThumbnailPaths.has(entry.path) || unavailableExplorerThumbnailPaths.has(entry.path)) {
      return;
    }

    requestedExplorerThumbnailPaths.add(entry.path);
    const priority = explorerThumbnailPriority + (explorerSplit && splitFocusedPane === pane ? 1 : 0);
    postHostMessage({ type: 'explorer.thumbnail', path: entry.path, priority });
  }

  function prefetchExplorerThumbnails(entries: ExplorerEntry[], pane: 'left' | 'right') {
    entries.slice(0, 14).forEach((entry) => requestExplorerThumbnail(entry, pane));
  }

  function observeExplorerThumbnail(node: HTMLElement, request: { entry: ExplorerEntry; pane: 'left' | 'right' }) {
    const observer = new IntersectionObserver(
      ([entryState]) => {
        if (entryState.isIntersecting) {
          requestExplorerThumbnail(request.entry, request.pane);
          observer.disconnect();
        }
      },
      { root: node.closest('.explorer-grid-pane, .split-grid-pane'), rootMargin: '220px' }
    );

    observer.observe(node);

    return {
      destroy() {
        observer.disconnect();
      }
    };
  }

  function getGallerySnapshotFilterParts(): GalleryFilterPart[] {
    const parts: GalleryFilterPart[] = [];
    if (isGalleryFilterEnabled('rating')) parts.push('ratings');
    if (isGalleryFilterEnabled('tag')) parts.push('tags');
    if (isGalleryFilterEnabled('creator')) parts.push('creators');
    if (isGalleryFilterEnabled('title')) parts.push('titles');
    if (isGalleryFilterEnabled('character') && isGalleryFilterEnabled('title') && galleryTitleFilters.length === 1) {
      parts.push('characters');
    }
    return parts;
  }

  function loadGalleryWorks(
    append = false,
    ratingFilters = galleryRatingFilters,
    refreshFilterSnapshot = false,
    dependentFilterParts: GalleryFilterPart[] = [],
    pageSize = 96
  ) {
    if (append && galleryIsLoading) {
      return;
    }

    galleryIsLoading = true;
    const offset = append ? galleryWorks.length : 0;
    galleryRequestId = `gallery-${nextGalleryRequestId++}`;
    const request = {
      requestId: galleryRequestId,
      category: gallerySection,
      ratings: isGalleryFilterEnabled('rating') ? ratingFilters : [],
      tags: isGalleryFilterEnabled('tag') ? galleryTagFilters : [],
      creators: isGalleryFilterEnabled('creator') ? galleryCreatorFilters : [],
      titles: isGalleryFilterEnabled('title') ? galleryTitleFilters : [],
      characters: isGalleryFilterEnabled('character') && isGalleryFilterEnabled('title') ? galleryCharacterFilters : [],
      filterSort: galleryFilterSorts.map((criterion) => `${criterion.key}:${criterion.direction}`).join(','),
      sort: galleryThumbnailSorts.map((criterion) => `${criterion.key}:${criterion.direction}`).join(','),
      offset,
      pageSize
    };
    postHostMessage({ type: 'gallery.works.list', ...request });
    if (!append) {
      const isSnapshot = refreshFilterSnapshot || !galleryFilterSnapshotSections.has(gallerySection);
      const filterParts = isSnapshot ? getGallerySnapshotFilterParts() : dependentFilterParts;
      if (filterParts.length > 0) {
        postHostMessage({ type: 'gallery.works.filters', ...request, filterParts, isSnapshot });
      }
      else if (isSnapshot) {
        galleryFilterSnapshotSections = new Set([...galleryFilterSnapshotSections, gallerySection]);
      }
    }
  }

  function commitGalleryFilterConfiguration() {
    if (galleryFilterCommitInProgress) {
      return;
    }

    galleryFilterCommitInProgress = true;
    galleryFilterSnapshotSections = new Set();
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function applyGallerySection(section: string) {
    if (activeView === 'library' && section !== gallerySection) recordNavigationHistory();
    gallerySection = section;
    loadGalleryPins(section);
    galleryRatingFilters = [];
    galleryTagFilters = [];
    galleryCreatorFilters = [];
    galleryTitleFilters = [];
    galleryCharacterFilters = [];
    galleryQuery = '';
    galleryCreatorFiltersExpanded = false;
    galleryTitleFiltersExpanded = false;
    galleryCharacterFiltersExpanded = false;
    galleryTagFiltersExpanded = false;
    galleryPromotedCreators = [];
    galleryPromotedTitles = [];
    galleryPromotedCharacters = [];
    galleryPromotedTags = [];
    selectedGalleryWorkIds = new Set();
    galleryWorks = [];
    galleryThumbnails = {};
    requestedGalleryThumbnailIds.clear();
    unavailableGalleryThumbnailIds.clear();
    activateView('library');
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function selectGallerySection(section: string) {
    galleryNavigationExpanded = true;
    requestViewChange({ view: 'library', gallerySection: section });
  }

  function toggleGalleryNavigation() {
    if (activeView !== 'library') {
      galleryNavigationExpanded = true;
      setView('library');
      return;
    }
    galleryNavigationExpanded = !galleryNavigationExpanded;
  }

  function toggleCreatorsNavigation() {
    if (activeView !== 'creatorTracking') {
      creatorsNavigationExpanded = true;
      if (creatorTrackingTabs.length === 0) {
        activeCreatorTrackingTabId = '';
        creatorTrackingSummary = null;
        creatorTracking = null;
        creatorTrackingDashboardContext = null;
        creatorTrackingDashboard = null;
        creatorTrackingIsLoading = false;
        creatorTrackingIsSaving = false;
        creatorTrackingDirty = false;
        creatorTrackingError = '';
      }
      setView('creatorTracking');
      return;
    }
    creatorsNavigationExpanded = !creatorsNavigationExpanded;
  }

  function loadGalleryCreatorSummaries(forceRefresh = false) {
    if (galleryCreatorSummaryIsLoading) {
      return;
    }

    galleryCreatorSummaryIsLoading = true;
    galleryCreatorSummaryError = '';
    galleryCreatorSummaryRequestId = `creator-summary-${nextGalleryCreatorSummaryRequestId++}`;
    postHostMessage({ type: 'gallery.creatorSummary.list', requestId: galleryCreatorSummaryRequestId, forceRefresh });
  }

  function loadUserMetrics(category = userMetricsCategory, forceRefresh = false) {
    userMetricsCategory = category;
    userMetricsIsLoading = true;
    userMetricsError = '';
    userMetricsRequestId = `user-metrics-${nextUserMetricsRequestId++}`;
    postHostMessage({ type: 'user.metrics.get', requestId: userMetricsRequestId, category, forceRefresh });
  }

  function loadCalendarSubscriptions() {
    calendarIsLoading = true;
    calendarError = '';
    calendarRequestId = `calendar-${nextCalendarRequestId++}`;
    postHostMessage({ type: 'calendar.subscriptions.list', requestId: calendarRequestId });
  }

  function exportCalendarIcs() {
    calendarIcsRequestId = `calendar-ics-${nextCalendarRequestId++}`;
    postHostMessage({ type: 'calendar.ics.export', requestId: calendarIcsRequestId });
  }

  function saveCalendarSettings() {
    calendarWeekStartDraft = normalizeWeekStartDay(calendarWeekStartDraft);
    postHostMessage(getGoogleCalendarSettingsMessage('settings.calendar.save', {
      weekStartDay: calendarWeekStartDraft
    }));
  }

  function getGoogleCalendarSettingsMessage(type: string, extra: Record<string, unknown> = {}) {
    return {
      type,
      autoSyncEnabled: googleCalendarAutoSyncEnabled,
      clientId: googleCalendarClientId.trim(),
      clientSecret: googleCalendarClientSecretDraft.trim(),
      calendarId: googleCalendarId.trim() || 'primary',
      ...extra
    };
  }

  function connectGoogleCalendar() {
    googleCalendarBusy = true;
    googleCalendarStatus = 'ブラウザでGoogle Calendarへのアクセスを許可してください...';
    postHostMessage(getGoogleCalendarSettingsMessage('settings.calendar.google.connect'));
  }

  function testGoogleCalendarConnection() {
    googleCalendarBusy = true;
    googleCalendarStatus = 'Google Calendarとの接続を確認しています...';
    postHostMessage(getGoogleCalendarSettingsMessage('settings.calendar.google.test'));
  }

  function syncGoogleCalendar() {
    googleCalendarBusy = true;
    googleCalendarStatus = 'Google Calendarへサブスク予定を同期しています...';
    postHostMessage({ type: 'calendar.google.sync' });
  }

  function disconnectGoogleCalendar() {
    googleCalendarBusy = true;
    googleCalendarStatus = 'Google Calendar連携を解除しています...';
    postHostMessage({ type: 'settings.calendar.google.disconnect' });
  }

  function applyGoogleCalendarSettings(value: unknown) {
    const settings = value as Partial<GoogleCalendarSyncSettings> | null | undefined;
    if (!settings) return;
    googleCalendarAutoSyncEnabled = settings.autoSyncEnabled === true;
    googleCalendarClientId = String(settings.clientId ?? '');
    googleCalendarHasClientSecret = settings.hasClientSecret === true;
    googleCalendarHasRefreshToken = settings.hasRefreshToken === true;
    googleCalendarId = String(settings.calendarId ?? 'primary') || 'primary';
    googleCalendarRedirectUri = String(settings.redirectUri ?? '');
    googleCalendarLastSyncedAt = String(settings.lastSyncedAt ?? '');
    googleCalendarLastSyncError = String(settings.lastSyncError ?? '');
  }

  function normalizeWeekStartDay(value: unknown) {
    const parsed = Math.round(Number(value));
    return Number.isFinite(parsed) && parsed >= 0 && parsed <= 6 ? parsed : 0;
  }

  function parseCalendarSettings(value: unknown): CalendarSettings {
    const settings = value as Partial<CalendarSettings> | null | undefined;
    return { weekStartDay: normalizeWeekStartDay(settings?.weekStartDay) };
  }

  function normalizeCalendarEvent(value: Partial<CalendarSubscriptionEvent> | null | undefined): CalendarSubscriptionEvent | null {
    if (!value?.creator || !value.renewalOn) return null;
    return {
      id: String(value.id ?? `${value.creator}-${value.renewalOn}`),
      creator: String(value.creator ?? '').trim(),
      displayName: String(value.displayName ?? value.creator ?? '').trim(),
      platform: String(value.platform ?? '').trim(),
      plan: String(value.plan ?? '').trim(),
      currency: String(value.currency ?? '').trim(),
      amount: Number(value.amount ?? 0),
      renewalOn: String(value.renewalOn ?? '').trim(),
      endingPlanned: value.endingPlanned === true,
      reminder: value.reminder === true
    };
  }

  function toLocalDateInputValue(date: Date) {
    const year = date.getFullYear();
    const month = `${date.getMonth() + 1}`.padStart(2, '0');
    const day = `${date.getDate()}`.padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  function parseCalendarDate(value: string) {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    if (!match) return null;
    return new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  }

  function addCalendarDays(date: Date, days: number) {
    return new Date(date.getFullYear(), date.getMonth(), date.getDate() + days);
  }

  function getCalendarWeekStart(date: Date, weekStartDay = calendarSettings.weekStartDay) {
    const normalizedWeekStart = normalizeWeekStartDay(weekStartDay);
    const diff = (date.getDay() - normalizedWeekStart + 7) % 7;
    return addCalendarDays(date, -diff);
  }

  function getCalendarMonthCells(monthValue = calendarMonthCursor) {
    const [yearText, monthText] = monthValue.split('-');
    const first = new Date(Number(yearText), Number(monthText) - 1, 1);
    if (Number.isNaN(first.getTime())) return [];
    const start = getCalendarWeekStart(first);
    const last = new Date(first.getFullYear(), first.getMonth() + 1, 0);
    const end = addCalendarDays(getCalendarWeekStart(last), 6);
    const cells: Array<{ date: string; day: number; inMonth: boolean; isToday: boolean; events: CalendarSubscriptionEvent[] }> = [];
    const today = toLocalDateInputValue(new Date());
    for (let cursor = start; cursor <= end; cursor = addCalendarDays(cursor, 1)) {
      const date = toLocalDateInputValue(cursor);
      cells.push({
        date,
        day: cursor.getDate(),
        inMonth: cursor.getMonth() === first.getMonth(),
        isToday: date === today,
        events: calendarEventsForDate(date)
      });
    }
    return cells;
  }

  function getCalendarFocusCells() {
    const todayDate = new Date();
    const start = getCalendarWeekStart(todayDate);
    const today = toLocalDateInputValue(todayDate);
    return Array.from({ length: 14 }, (_, index) => {
      const cursor = addCalendarDays(start, index);
      const date = toLocalDateInputValue(cursor);
      return {
        date,
        day: cursor.getDate(),
        inMonth: true,
        isToday: date === today,
        events: calendarEventsForDate(date)
      };
    });
  }

  function calendarEventsForDate(date: string) {
    return calendarEvents
      .filter((event) => event.renewalOn === date)
      .sort((left, right) =>
        left.displayName.localeCompare(right.displayName, 'ja-JP') ||
        left.platform.localeCompare(right.platform, 'ja-JP'));
  }

  function getCalendarWeekdayLabels() {
    const labels = ['日', '月', '火', '水', '木', '金', '土'];
    const start = normalizeWeekStartDay(calendarSettings.weekStartDay);
    return Array.from({ length: 7 }, (_, index) => labels[(start + index) % 7]);
  }

  function shiftCalendarMonth(offset: number) {
    const [yearText, monthText] = calendarMonthCursor.split('-');
    const next = new Date(Number(yearText), Number(monthText) - 1 + offset, 1);
    calendarMonthCursor = `${next.getFullYear()}-${`${next.getMonth() + 1}`.padStart(2, '0')}`;
  }

  function resetCalendarMonthToToday() {
    calendarMonthCursor = toLocalDateInputValue(new Date()).slice(0, 7);
  }

  function getCalendarMonthTitle() {
    const [yearText, monthText] = calendarMonthCursor.split('-');
    return `${yearText}年${Number(monthText)}月`;
  }

  function formatCalendarEventAmount(event: CalendarSubscriptionEvent) {
    return event.amount > 0 ? `${event.amount.toLocaleString(undefined, { maximumFractionDigits: 2 })} ${event.currency}` : '';
  }

  function openCalendarEventCreator(event: CalendarSubscriptionEvent) {
    const summary = galleryCreatorSummaries.find((item) =>
      item.creator.trim().localeCompare(event.creator.trim(), 'ja-JP', { sensitivity: 'base' }) === 0);
    if (summary) {
      openCreatorTrackingForSummary(summary);
      return;
    }

    openCreatorTrackingForSummary(createCreatorTrackingTemplateSummary(event.creator, galleryCreatorSummarySection || gallerySection || defaultGallerySectionId, ''));
  }

  function selectUserMetricsCategory(category: string) {
    if (category === userMetricsCategory && userMetricsDashboard && !userMetricsIsLoading) {
      return;
    }
    loadUserMetrics(category);
  }

  function selectGalleryCreatorSummarySection(section: string) {
    if (activeView === 'creators' && section !== galleryCreatorSummarySection) recordNavigationHistory();
    creatorsNavigationExpanded = true;
    galleryCreatorSummarySection = section;
    galleryCreatorSummaryCoreTitles = [];
    galleryCreatorSummaryCoreTags = [];
    galleryCreatorSummarySites = [];
    galleryCreatorSummaryOverallRatings = [];
    galleryCreatorSummaryMetricScores = Object.fromEntries(
      creatorTrackingMetricDefinitions.map((metric) => [metric.key, [] as number[]])) as Record<CreatorTrackingMetricKey, number[]>;
    galleryCreatorSummaryCoreTitlesExpanded = false;
    galleryCreatorSummaryCoreTagsExpanded = false;
    galleryCreatorSummaryMoreExpanded = false;
    if (activeView !== 'creators') {
      setView('creators');
    }
  }

  function setGalleryCreatorSummaryRatingFilter(ratingBucket: string, event: MouseEvent) {
    galleryCreatorSummaryRatings = toggleGalleryFilter(galleryCreatorSummaryRatings, ratingBucket, event);
  }

  function setGalleryCreatorSummaryCoreTitleFilter(title: string, event: MouseEvent) {
    galleryCreatorSummaryCoreTitles = toggleGalleryFilter(galleryCreatorSummaryCoreTitles, title, event);
  }

  function setGalleryCreatorSummaryCoreTagFilter(tag: string, event: MouseEvent) {
    galleryCreatorSummaryCoreTags = toggleGalleryFilter(galleryCreatorSummaryCoreTags, tag, event);
  }

  function setGalleryCreatorSummarySiteFilter(site: string, event: MouseEvent) {
    galleryCreatorSummarySites = toggleGalleryFilter(galleryCreatorSummarySites, site, event);
  }

  function setGalleryCreatorSummaryOverallRatingFilter(score: number, event: MouseEvent) {
    galleryCreatorSummaryOverallRatings = toggleGalleryFilter(galleryCreatorSummaryOverallRatings, score, event);
  }

  function setGalleryCreatorSummaryMetricScoreFilter(key: CreatorTrackingMetricKey, score: number, event: MouseEvent) {
    galleryCreatorSummaryMetricScores = {
      ...galleryCreatorSummaryMetricScores,
      [key]: toggleGalleryFilter(galleryCreatorSummaryMetricScores[key], score, event)
    };
  }

  function getGalleryCreatorSummaryDefaultSortDirection(sort: GalleryCreatorSummarySortKey): GalleryCreatorSummarySortDirection {
    return sort === 'name' ? 'asc' : 'desc';
  }

  function toggleGalleryCreatorSummarySort(sort: GalleryCreatorSummarySortKey) {
    const index = galleryCreatorSummarySorts.findIndex((criterion) => criterion.key === sort);
    if (index < 0) {
      if (galleryCreatorSummarySorts.length >= 3) {
        showExplorerToast('Creatorsのソート条件は3つまで選択できます。', 'error');
        return;
      }
      galleryCreatorSummarySorts = [
        ...galleryCreatorSummarySorts,
        { key: sort, direction: getGalleryCreatorSummaryDefaultSortDirection(sort) }
      ];
    }
    else if (sort !== 'rating') {
      galleryCreatorSummarySorts = galleryCreatorSummarySorts.map((criterion, criterionIndex) =>
        criterionIndex === index
          ? { ...criterion, direction: criterion.direction === 'desc' ? 'asc' : 'desc' }
          : criterion);
    }
  }

  function removeGalleryCreatorSummarySort(event: MouseEvent, sort: GalleryCreatorSummarySortKey) {
    event.preventDefault();
    event.stopPropagation();
    galleryCreatorSummarySorts = galleryCreatorSummarySorts.filter((criterion) => criterion.key !== sort);
  }

  function galleryCreatorSummarySortArrow(
    sorts: GalleryCreatorSummarySortCriterion[],
    sort: GalleryCreatorSummarySortKey
  ) {
    const direction = sorts.find((criterion) => criterion.key === sort)?.direction
      ?? getGalleryCreatorSummaryDefaultSortDirection(sort);
    return direction === 'desc' ? '⤵' : '⤴';
  }

  function galleryCreatorSummarySortPriority(
    sorts: GalleryCreatorSummarySortCriterion[],
    sort: GalleryCreatorSummarySortKey
  ) {
    return sorts.findIndex((criterion) => criterion.key === sort) + 1;
  }

  function clearGalleryCreatorSummaryFilters() {
    galleryCreatorSummaryRatings = [];
    galleryCreatorSummaryCoreTitles = [];
    galleryCreatorSummaryCoreTags = [];
    galleryCreatorSummarySites = [];
    galleryCreatorSummaryOverallRatings = [];
    galleryCreatorSummaryMetricScores = Object.fromEntries(
      creatorTrackingMetricDefinitions.map((metric) => [metric.key, [] as number[]])) as Record<CreatorTrackingMetricKey, number[]>;
    galleryCreatorSummaryReminderFilter = '';
    galleryCreatorSummaryQuery = '';
    galleryCreatorSummaryCoreTitlesExpanded = false;
    galleryCreatorSummaryCoreTagsExpanded = false;
    galleryCreatorSummaryMoreExpanded = false;
  }

  function getGalleryCreatorRatingBucketCount(ratingBucket: string) {
    return galleryCreatorSummaries.filter((item) =>
      item.category === galleryCreatorSummarySection && item.ratingBucket === ratingBucket).length;
  }

  function getGalleryCreatorSummarySiteCount(site: string) {
    return galleryCreatorSummaries.filter((item) =>
      item.category === galleryCreatorSummarySection &&
      item.trackingSites.some((candidate) => candidate.localeCompare(site, 'ja-JP', { sensitivity: 'base' }) === 0)).length;
  }

  function getGalleryCreatorSummaryOverallRatingCount(score: number) {
    return galleryCreatorSummaries.filter((item) =>
      item.category === galleryCreatorSummarySection &&
      item.hasCreatorTracking &&
      Math.floor(item.personalRating) === score).length;
  }

  function getGalleryCreatorSummaryMetricScoreCount(key: CreatorTrackingMetricKey, score: number) {
    return galleryCreatorSummaries.filter((item) =>
      item.category === galleryCreatorSummarySection && Number(item.evaluationMetrics?.[key] ?? 0) === score).length;
  }

  function toggleGalleryCreatorSummaryReminderFilter(filter: Exclude<GalleryCreatorSummaryReminderFilter, ''>) {
    galleryCreatorSummaryReminderFilter = galleryCreatorSummaryReminderFilter === filter ? '' : filter;
  }

  function getVisibleGalleryCreatorSummaries(
    summaries: GalleryCreatorSummary[],
    section: string,
    ratings: string[],
    coreTitles: string[],
    coreTags: string[],
    sites: string[],
    overallRatings: number[],
    metricScores: Record<CreatorTrackingMetricKey, number[]>,
    reminderFilter: GalleryCreatorSummaryReminderFilter,
    query: string,
    sorts: GalleryCreatorSummarySortCriterion[]) {
    const normalizedQuery = query.trim().toLocaleLowerCase('ja-JP');
    const selectedRatings = new Set(ratings);
    const selectedCoreTitles = new Set(coreTitles);
    const selectedCoreTags = new Set(coreTags);
    const selectedSites = new Set(sites.map((site) => site.toLocaleLowerCase('ja-JP')));
    const selectedOverallRatings = new Set(overallRatings);
    const items = summaries.filter((item) => {
      if (item.category !== section) return false;
      if (selectedRatings.size > 0 && !selectedRatings.has(item.ratingBucket)) return false;
      if (selectedCoreTitles.size > 0 && !item.coreTitles.some((title) => selectedCoreTitles.has(title))) return false;
      if (selectedCoreTags.size > 0 && !item.coreTags.some((tag) => selectedCoreTags.has(tag))) return false;
      if (selectedSites.size > 0 && !item.trackingSites.some((site) => selectedSites.has(site.toLocaleLowerCase('ja-JP')))) return false;
      if (selectedOverallRatings.size > 0 && !selectedOverallRatings.has(Math.floor(item.personalRating))) return false;
      if (reminderFilter === 'warning' && !item.followWarnFlg) return false;
      if (reminderFilter === 'alert' && !item.followAlertFlg) return false;
      for (const metric of creatorTrackingMetricDefinitions) {
        const selectedScores = metricScores[metric.key];
        if (selectedScores.length > 0 && !selectedScores.includes(Number(item.evaluationMetrics?.[metric.key] ?? 0))) return false;
      }
      return !normalizedQuery || item.searchText.includes(normalizedQuery);
    });

    return items.sort((left, right) => {
      for (const sort of sorts) {
        const leftMissing = (sort.key === 'updated' && !left.trackingLastActivityOn)
          || ((sort.key === 'sinceCheck' || sort.key === 'trackingDays') && !left.hasCreatorTracking);
        const rightMissing = (sort.key === 'updated' && !right.trackingLastActivityOn)
          || ((sort.key === 'sinceCheck' || sort.key === 'trackingDays') && !right.hasCreatorTracking);
        if (leftMissing !== rightMissing) return leftMissing ? 1 : -1;

        let comparison = 0;
        if (sort.key === 'name') {
          comparison = left.creator.localeCompare(right.creator, 'ja-JP');
        }
        else if (sort.key === 'updated') {
          comparison = left.trackingLastActivityOn.localeCompare(right.trackingLastActivityOn, 'ja-JP');
        }
        else {
          const leftValue = sort.key === 'files'
            ? left.fileCount
            : sort.key === 'average'
              ? left.averageImageCount
              : sort.key === 'sinceCheck'
                ? left.sinceLastCheckDays
                : sort.key === 'trackingDays'
                  ? left.trackingDays
                  : sort.key === 'spend'
                    ? left.totalSpend
                    : left.totalRating;
          const rightValue = sort.key === 'files'
            ? right.fileCount
            : sort.key === 'average'
              ? right.averageImageCount
              : sort.key === 'sinceCheck'
                ? right.sinceLastCheckDays
                : sort.key === 'trackingDays'
                  ? right.trackingDays
                  : sort.key === 'spend'
                    ? right.totalSpend
                    : right.totalRating;
          comparison = leftValue - rightValue;
        }

        if (comparison !== 0) {
          return sort.direction === 'asc' ? comparison : -comparison;
        }
      }
      return left.creator.localeCompare(right.creator, 'ja-JP');
    });
  }

  function getGalleryCreatorCoreFilterOptions(
    summaries: GalleryCreatorSummary[],
    section: string,
    property: 'coreTitles' | 'coreTags'): GalleryCreatorCoreFilterOption[] {
    const creatorsByValue = new Map<string, Set<string>>();
    for (const item of summaries) {
      if (item.category !== section) continue;
      for (const value of new Set(item[property])) {
        const creators = creatorsByValue.get(value) ?? new Set<string>();
        creators.add(item.creator);
        creatorsByValue.set(value, creators);
      }
    }

    return [...creatorsByValue.entries()]
      .map(([value, creators]) => ({ value, count: creators.size }))
      .sort((left, right) => right.count - left.count || left.value.localeCompare(right.value, 'ja-JP'));
  }

  function flushGalleryThumbnailRequests() {
    galleryThumbnailFlushFrame = 0;
    const requests = [...pendingGalleryThumbnailRequests.values()];
    pendingGalleryThumbnailRequests.clear();
    for (let offset = 0; offset < requests.length; offset += galleryThumbnailRequestBatchSize) {
      postHostMessage({
        type: 'gallery.work.thumbnail.batch',
        items: requests.slice(offset, offset + galleryThumbnailRequestBatchSize)
      });
    }
  }

  function applyCachedGalleryThumbnailUris(value: unknown) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      return;
    }

    const thumbnailUris = Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .filter((entry): entry is [string, string] => typeof entry[1] === 'string' && entry[1].length > 0));
    const ids = Object.keys(thumbnailUris);
    if (ids.length === 0) {
      return;
    }

    for (const id of ids) {
      requestedGalleryThumbnailIds.delete(id);
      unavailableGalleryThumbnailIds.delete(id);
    }
    galleryThumbnails = { ...galleryThumbnails, ...thumbnailUris };
  }

  function queueGalleryThumbnailRequest(id: string, path: string, category: string) {
    if (galleryThumbnails[id] || requestedGalleryThumbnailIds.has(id) || unavailableGalleryThumbnailIds.has(id)) {
      return;
    }

    requestedGalleryThumbnailIds.add(id);
    pendingGalleryThumbnailRequests.set(id, { id, path, category });
    if (!galleryThumbnailFlushFrame) {
      galleryThumbnailFlushFrame = window.requestAnimationFrame(flushGalleryThumbnailRequests);
    }
  }

  function getGalleryThumbnailObserver() {
    if (!galleryThumbnailObserver) {
      galleryThumbnailObserver = new IntersectionObserver((entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          galleryThumbnailObserverCallbacks.get(entry.target)?.();
          galleryThumbnailObserver?.unobserve(entry.target);
          galleryThumbnailObserverCallbacks.delete(entry.target);
        }
      }, { rootMargin: '700px' });
    }
    return galleryThumbnailObserver;
  }

  function observeGalleryThumbnailSource(
    node: HTMLElement,
    getRequest: () => { id: string; path: string; category: string }) {
    galleryThumbnailObserverCallbacks.set(node, () => {
      const request = getRequest();
      queueGalleryThumbnailRequest(request.id, request.path, request.category);
    });
    getGalleryThumbnailObserver().observe(node);
    return {
      update() {
        galleryThumbnailObserverCallbacks.set(node, () => {
          const request = getRequest();
          queueGalleryThumbnailRequest(request.id, request.path, request.category);
        });
      },
      destroy() {
        galleryThumbnailObserver?.unobserve(node);
        galleryThumbnailObserverCallbacks.delete(node);
      }
    };
  }

  function observeGalleryCreatorSummaryThumbnail(node: HTMLElement, item: GalleryCreatorSummary) {
    return observeGalleryThumbnailSource(node, () => ({
      id: item.id,
      path: item.creatorFolder,
      category: item.category
    }));
  }

  function openGalleryCreatorSummary(item: GalleryCreatorSummary) {
    gallerySection = item.category;
    loadGalleryPins(item.category);
    galleryRatingFilters = [];
    galleryTagFilters = [];
    galleryCreatorFilters = [item.creator];
    galleryTitleFilters = [];
    galleryCharacterFilters = [];
    galleryQuery = '';
    galleryCreatorFiltersExpanded = false;
    galleryTitleFiltersExpanded = false;
    galleryCharacterFiltersExpanded = false;
    galleryTagFiltersExpanded = false;
    galleryPromotedCreators = [item.creator];
    galleryPromotedTitles = [];
    galleryPromotedCharacters = [];
    galleryPromotedTags = [];
    selectedGalleryWorkIds = new Set();
    galleryWorks = [];
    galleryTotal = 0;
    galleryThumbnails = {};
    requestedGalleryThumbnailIds.clear();
    unavailableGalleryThumbnailIds.clear();
    activateView('library');
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function captureActiveCreatorTrackingTab() {
    if (!activeCreatorTrackingTabId) return;
    creatorTrackingTabs = creatorTrackingTabs.map(tab => tab.id === activeCreatorTrackingTabId
      ? {
          ...tab,
          summary: creatorTrackingSummary ?? tab.summary,
          tracking: creatorTracking,
          dashboardContext: creatorTrackingDashboardContext,
          dashboard: creatorTrackingDashboard,
          isLoading: creatorTrackingIsLoading,
          isSaving: creatorTrackingIsSaving,
          dirty: creatorTrackingDirty,
          error: creatorTrackingError,
          billingView: creatorTrackingBillingView,
          archiveScale: creatorTrackingArchiveScale,
          requestId: creatorTrackingRequestId
        }
      : tab);
  }

  function captureCreatorTrackingTabSessionState() {
    captureActiveCreatorTrackingTab();
    const activeIndex = Math.max(0, creatorTrackingTabs.findIndex((tab) => tab.id === activeCreatorTrackingTabId));
    const state: CreatorTrackingBookmarkState = {
      version: 4,
      tabs: creatorTrackingTabs.map((tab) => ({
        creator: tab.creator,
        category: tab.summary.category,
        creatorFolder: tab.summary.creatorFolder,
        billingView: tab.id === activeCreatorTrackingTabId ? creatorTrackingBillingView : tab.billingView,
        archiveScale: tab.id === activeCreatorTrackingTabId ? creatorTrackingArchiveScale : tab.archiveScale
      })),
      activeIndex,
      stickyNotes: []
    };
    return JSON.stringify(state);
  }

  function applyCreatorTrackingTab(tab: CreatorTrackingTab) {
    activeCreatorTrackingTabId = tab.id;
    creatorTrackingSummary = tab.summary;
    creatorTracking = tab.tracking;
    creatorTrackingDashboardContext = tab.dashboardContext;
    creatorTrackingDashboard = tab.dashboard;
    creatorTrackingIsLoading = tab.isLoading;
    creatorTrackingIsSaving = tab.isSaving;
    creatorTrackingDirty = tab.dirty;
    creatorTrackingError = tab.error;
    creatorTrackingBillingView = tab.billingView;
    creatorTrackingArchiveScale = tab.archiveScale;
    creatorTrackingRequestId = tab.requestId;
  }

  function activateCreatorTrackingTab(tabId: string) {
    if (tabId === activeCreatorTrackingTabId && activeView === 'creatorTracking') return;
    if (activeView === 'creatorTracking') recordNavigationHistory();
    saveCreatorTracking();
    captureActiveCreatorTrackingTab();
    const tab = creatorTrackingTabs.find(candidate => candidate.id === tabId);
    if (!tab) return;
    applyCreatorTrackingTab(tab);
    activateView('creatorTracking');
    persistNavigationState();
    queueMicrotask(scrollCreatorTrackingArchiveToEnd);
  }

  function selectRelativeCreatorTrackingTab(offset: number) {
    if (creatorTrackingTabs.length < 2) return;
    const currentIndex = creatorTrackingTabs.findIndex(tab => tab.id === activeCreatorTrackingTabId);
    const nextIndex = (Math.max(0, currentIndex) + offset + creatorTrackingTabs.length) % creatorTrackingTabs.length;
    activateCreatorTrackingTab(creatorTrackingTabs[nextIndex].id);
  }

  function handleCreatorTrackingShortcut(event: KeyboardEvent) {
    if (activeView !== 'creatorTracking') return;
    const target = event.target as HTMLElement | null;
    if (target?.matches('input, select, textarea, [contenteditable="true"]')) return;
    if (matchesKeyboardShortcut(event, 'selectPreviousTab')) {
      event.preventDefault();
      selectRelativeCreatorTrackingTab(-1);
    }
    else if (matchesKeyboardShortcut(event, 'selectNextTab')) {
      event.preventDefault();
      selectRelativeCreatorTrackingTab(1);
    }
  }

  async function openGlobalSearchDialog() {
    globalSearchMode = activeView === 'creators' ? 'creator' : 'normal';
    globalSearchQuery = activeView === 'explorer'
      ? activeExplorerQuery
      : activeView === 'creators'
        ? galleryCreatorSummaryQuery
        : activeView === 'library'
          ? galleryQuery
          : '';
    globalSearchOpen = true;
    await tick();
    globalSearchInputElement?.focus();
    globalSearchInputElement?.select();
  }

  function executeGlobalSearch() {
    const search = globalSearchQuery.trim();
    recordNavigationHistory();
    globalSearchOpen = false;

    if (globalSearchMode === 'creator') {
      const sourceSection = activeView === 'library'
        ? gallerySection
        : activeView === 'creatorTracking'
          ? creatorTrackingSummary?.category
          : activeView === 'userMetrics'
            ? userMetricsCategory
            : galleryCreatorSummarySection;
      if (sourceSection && gallerySections.some(section => section.id === sourceSection)) {
        galleryCreatorSummarySection = sourceSection;
      }
      galleryCreatorSummaryQuery = search;
      if (activeView !== 'creators') activateView('creators');
      if (galleryCreatorSummaries.length === 0) loadGalleryCreatorSummaries();
      return;
    }

    if (activeView === 'explorer') {
      if (explorerSplit && splitFocusedPane === 'right') splitExplorerQuery = search;
      else explorerQuery = search;
      return;
    }
    if (activeView === 'creators') {
      galleryCreatorSummaryQuery = search;
      return;
    }

    galleryQuery = search;
    if (activeView !== 'library') activateView('library');
    if (galleryWorks.length === 0) loadGalleryWorks();
  }

  function handleGlobalShortcut(event: KeyboardEvent) {
    if (matchesKeyboardShortcut(event, 'openGlobalSearch')) {
      event.preventDefault();
      void openGlobalSearchDialog();
      return true;
    }

    const target = event.target as HTMLElement | null;
    const isEditable = Boolean(target?.matches('input, select, textarea, [contenteditable="true"]'));
    if (!isEditable && matchesKeyboardShortcut(event, 'navigateBack')) {
      event.preventDefault();
      navigateAppHistory(-1);
      return true;
    }
    return false;
  }

  function syncActiveCreatorTrackingTab() {
    captureActiveCreatorTrackingTab();
  }

  function getCreatorTrackingTabLabel(tab: CreatorTrackingTab) {
    const tracking = tab.id === activeCreatorTrackingTabId ? creatorTracking : tab.tracking;
    return tracking?.displayName?.trim() || tab.creator;
  }

  function splitCreatorTrackingTabLabel(label: string) {
    const segments: { text: string; isFullWidth: boolean }[] = [];
    for (const character of Array.from(label)) {
      const isFullWidth = /[\u1100-\u115f\u2329\u232a\u2e80-\u303e\u3040-\ua4cf\uac00-\ud7a3\uf900-\ufaff\ufe10-\ufe19\ufe30-\ufe6f\uff01-\uff60\uffe0-\uffe6\u{1b000}-\u{1b16f}\u{1f200}-\u{1f2ff}\u{20000}-\u{3fffd}]/u.test(character);
      const previous = segments.at(-1);
      if (previous?.isFullWidth === isFullWidth) {
        previous.text += character;
      }
      else {
        segments.push({ text: character, isFullWidth });
      }
    }
    return segments;
  }

  function closeCreatorTrackingTab(event: MouseEvent, tabId: string) {
    event.stopPropagation();
    captureActiveCreatorTrackingTab();
    saveCreatorTrackingTab(tabId);
    const closingIndex = creatorTrackingTabs.findIndex(tab => tab.id === tabId);
    if (closingIndex < 0) return;
    const wasActive = tabId === activeCreatorTrackingTabId;
    creatorTrackingTabs = creatorTrackingTabs.filter(tab => tab.id !== tabId);
    if (!wasActive) return;
    const nextTab = creatorTrackingTabs[Math.min(closingIndex, creatorTrackingTabs.length - 1)];
    if (nextTab) {
      applyCreatorTrackingTab(nextTab);
      persistNavigationState();
      return;
    }
    activeCreatorTrackingTabId = '';
    creatorTrackingSummary = null;
    creatorTracking = null;
    creatorTrackingDashboard = null;
    creatorTrackingDashboardContext = null;
    activateView('creators');
    persistNavigationState();
  }

  function removeDeletedCreatorTrackingTabs(creator: string) {
    const normalizedCreator = creator.trim().toLocaleLowerCase('ja-JP');
    const activeIndex = creatorTrackingTabs.findIndex((tab) => tab.id === activeCreatorTrackingTabId);
    creatorTrackingTabs = creatorTrackingTabs.filter((tab) =>
      tab.creator.trim().toLocaleLowerCase('ja-JP') !== normalizedCreator);

    if (creatorTrackingTabs.length === 0) {
      activeCreatorTrackingTabId = '';
      creatorTrackingSummary = null;
      creatorTracking = null;
      creatorTrackingDashboard = null;
      creatorTrackingDashboardContext = null;
      creatorTrackingIsLoading = false;
      creatorTrackingIsSaving = false;
      creatorTrackingDirty = false;
      creatorTrackingError = '';
      activateView('creators');
      persistNavigationState();
      return;
    }

    if (!creatorTrackingTabs.some((tab) => tab.id === activeCreatorTrackingTabId)) {
      const nextTab = creatorTrackingTabs[Math.min(Math.max(0, activeIndex), creatorTrackingTabs.length - 1)];
      applyCreatorTrackingTab(nextTab);
    }
    persistNavigationState();
  }

  function requestCreatorTrackingDelete() {
    const creator = creatorTracking?.creator?.trim()
      || creatorTrackingSummary?.creator?.trim()
      || creatorTrackingTabs.find((tab) => tab.id === activeCreatorTrackingTabId)?.creator?.trim()
      || '';
    if (!creator) {
      showExplorerToast('削除するCreatorを特定できませんでした。', 'error');
      return;
    }

    creatorTrackingDeleteTarget = {
      creator,
      label: creatorTracking?.displayName?.trim() || creatorTrackingSummary?.creator || creator
    };
    creatorTrackingDeleteStep = 1;
    creatorTrackingDeleteInProgress = false;
  }

  function cancelCreatorTrackingDelete() {
    if (creatorTrackingDeleteInProgress) return;
    creatorTrackingDeleteStep = 0;
    creatorTrackingDeleteTarget = null;
  }

  function advanceCreatorTrackingDeleteConfirmation() {
    if (!creatorTrackingDeleteTarget || creatorTrackingDeleteInProgress) return;
    creatorTrackingDeleteStep = 2;
  }

  function executeCreatorTrackingDelete() {
    if (!creatorTrackingDeleteTarget || creatorTrackingDeleteInProgress) return;
    creatorTrackingDeleteInProgress = true;
    creatorTrackingDeleteRequestId = `creator-delete-${nextCreatorTrackingRequestId++}`;
    postHostMessage({
      type: 'creator.tracking.delete',
      requestId: creatorTrackingDeleteRequestId,
      creator: creatorTrackingDeleteTarget.creator
    });
  }

  function openCreatorTrackingPicker() {
    saveCreatorTracking();
    captureActiveCreatorTrackingTab();
    activateView('creators');
  }

  function openCreatorTrackingNewDialog() {
    saveCreatorTracking();
    captureActiveCreatorTrackingTab();
    creatorTrackingNewCreator = '';
    creatorTrackingNewCategory = galleryCreatorSummarySection || gallerySection || gallerySections[0]?.id || defaultGallerySectionId;
    creatorTrackingNewDialogOpen = true;
  }

  function createNewCreatorTracking() {
    const creator = creatorTrackingNewCreator.trim();
    if (!creator) {
      showExplorerToast('Creator名を入力してください。', 'error');
      return;
    }

    creatorTrackingNewDialogOpen = false;
    const category = gallerySections.some((section) => section.id === creatorTrackingNewCategory)
      ? creatorTrackingNewCategory
      : gallerySections[0]?.id ?? defaultGallerySectionId;
    openCreatorTrackingForSummary(createCreatorTrackingTemplateSummary(creator, category, ''));
  }

  function openCreatorTracking(event: MouseEvent, item: GalleryCreatorSummary) {
    event.preventDefault();
    event.stopPropagation();
    openCreatorTrackingForSummary(item);
  }

  function openCreatorTrackingForSummary(item: GalleryCreatorSummary) {
    const existingTab = creatorTrackingTabs.find(tab =>
      tab.summary.category === item.category &&
      tab.creator.trim().localeCompare(item.creator.trim(), 'ja-JP', { sensitivity: 'base' }) === 0);
    if (existingTab) {
      activateCreatorTrackingTab(existingTab.id);
      return;
    }

    captureActiveCreatorTrackingTab();
    const tab = createCreatorTrackingTab(item);
    creatorTrackingTabs = [...creatorTrackingTabs, tab];
    applyCreatorTrackingTab(tab);
    activateView('creatorTracking');
    persistNavigationState();
  }

  function openSelectedGalleryCreatorTracking() {
    if (galleryCreatorFilters.length !== 1) return;
    const creator = galleryCreatorFilters[0];
    const summary = galleryCreatorSummaries.find((item) =>
      item.category === gallerySection &&
      item.creator.trim().localeCompare(creator.trim(), 'ja-JP', { sensitivity: 'base' }) === 0);
    if (summary) {
      openCreatorTrackingForSummary(summary);
      return;
    }

    pendingGalleryCreatorTracking = { creator, category: gallerySection };
    loadGalleryCreatorSummaries();
    showExplorerToast(`Creator「${creator}」の情報を読み込んでいます。`, 'progress');
  }

  function openSelectedGalleryCreatorStorage() {
    if (galleryCreatorFilters.length !== 1 || pendingGalleryCreatorStorageRequest) return;
    const creator = galleryCreatorFilters[0];
    const requestId = `gallery-creator-storage-${nextExplorerPathsRequestId++}`;
    pendingGalleryCreatorStorageRequest = { requestId, creator };
    postHostMessage({ type: 'creator.tracking.storage.get', requestId, creator });
  }

  function normalizeWindowsPath(path: string) {
    const normalized = path.trim().replace(/\//g, '\\');
    return (/^[a-z]:\\$/i.test(normalized) ? normalized : normalized.replace(/\\+$/, '')).toLocaleLowerCase('ja-JP');
  }

  function getExplorerPathLabel(path: string) {
    const normalized = path.trim().replace(/\//g, '\\').replace(/\\+$/, '');
    return normalized.split('\\').filter(Boolean).at(-1) ?? normalized;
  }

  function requestExplorerPathsOpen(
    paths: string[],
    requireAll: boolean,
    splitWhenTwo: boolean,
    failureMessage: string) {
    const distinctPaths = [...new Map(
      paths.map(path => path.trim()).filter(Boolean).map(path => [normalizeWindowsPath(path), path])
    ).values()];
    if (distinctPaths.length === 0) {
      showExplorerToast(failureMessage, 'error');
      return;
    }

    const requestId = `explorer-paths-${nextExplorerPathsRequestId++}`;
    pendingExplorerPathsOpen = { requestId, paths: distinctPaths, requireAll, splitWhenTwo, failureMessage };
    postHostMessage({ type: 'explorer.paths.validate', requestId, paths: distinctPaths });
  }

  function openValidatedExplorerPaths(paths: string[], splitWhenTwo: boolean) {
    if (paths.length === 0) return;
    if (activeView === 'creatorTracking') {
      saveCreatorTracking();
      captureActiveCreatorTrackingTab();
    }
    saveExplorerTabScroll();
    const nextTabs = [...explorerTabs];
    const tabs = paths.map(path => {
      const existing = nextTabs.find(tab => normalizeWindowsPath(tab.path) === normalizeWindowsPath(path));
      if (existing) return existing;
      const tab = { id: `explorer-tab-${nextExplorerTabId++}`, path, label: getExplorerPathLabel(path) };
      nextTabs.push(tab);
      return tab;
    });
    explorerTabs = nextTabs;
    activeExplorerTabId = tabs[0].id;
    explorerPath = tabs[0].path;
    explorerPathDraft = tabs[0].path;
    explorerEntries = [];
    selectedPaths = [];
    activeView = 'explorer';
    explorerBookmarksExpanded = true;
    persistNavigationState();
    persistExplorerTabs();

    if (splitWhenTwo && tabs.length >= 2) {
      explorerSplit = {
        leftTabId: tabs[0].id,
        rightTabId: tabs[1].id,
        rightPath: tabs[1].path,
        rightParentPath: null,
        rightEntries: [],
        rightSelectedPaths: [],
        rightIsLoading: true,
        rightIsTruncated: false
      };
      splitFocusedPane = 'left';
      loadExplorer(tabs[0].path);
      loadSplitExplorer(tabs[1].path);
    }
    else {
      exitExplorerSplit();
      loadExplorer(tabs[0].path);
    }
  }

  function createCreatorTrackingTab(
    item: GalleryCreatorSummary,
    billingView: 'subscriptions' | 'purchases' = 'subscriptions',
    archiveScale: 'week' | 'month' | 'year' = creatorTrackingSettingsDraft.archiveScale
  ): CreatorTrackingTab {
    const dashboardContext = buildCreatorTrackingDashboardContext(item);
    const requestId = `creator-tracking-${nextCreatorTrackingRequestId++}`;
    const tab: CreatorTrackingTab = {
      id: `creator-tracking-tab-${nextCreatorTrackingRequestId++}`,
      creator: item.creator,
      summary: item,
      tracking: null,
      dashboardContext,
      dashboard: null,
      isLoading: true,
      isSaving: false,
      dirty: false,
      error: '',
      billingView,
      archiveScale,
      requestId
    };
    postHostMessage({
      type: 'creator.tracking.get',
      requestId,
      creator: item.creator,
      dashboardContext,
      templateContext: {
        displayName: item.creator,
        mainStoragePath: item.creatorFolder
      }
    });
    return tab;
  }

  function createCreatorTrackingTemplateSummary(
    creator: string,
    category: string,
    creatorFolder: string): GalleryCreatorSummary {
    return {
      id: `creator-template:${category}:${creator}`,
      category,
      creator,
      creatorFolder,
      coreTitles: [],
      coreTags: [],
      titleComposition: [],
      tagComposition: [],
      totalRating: 0,
      fileCount: 0,
      zipFileCount: 0,
      totalImageCount: 0,
      averageImageCount: 0,
      ratedFileCount: 0,
      maxRating: 0,
      lastAccessTime: '',
      lastUpdatedTime: '',
      trackingLastActivityOn: '',
      hasCreatorTracking: false,
      sinceLastCheckDays: -1,
      followWarnFlg: false,
      followAlertFlg: false,
      trackingDays: -1,
      totalSpend: 0,
      spendCurrency: 'JPY',
      trackingSites: [],
      evaluationMetrics: {} as Record<CreatorTrackingMetricKey, number>,
      personalRating: 0,
      ratingBucket: '0',
      searchText: creator.toLocaleLowerCase('ja-JP')
    };
  }

  function buildCreatorTrackingDashboardContext(item: GalleryCreatorSummary): CreatorTrackingDashboardContext {
    const aggregates = new Map<string, { creator: string; fileCount: number; totalImageCount: number; totalRating: number }>();
    for (const summary of galleryCreatorSummaries) {
      if (summary.category !== item.category) continue;
      const key = summary.creator.trim().toLocaleLowerCase('ja-JP');
      const aggregate = aggregates.get(key) ?? { creator: summary.creator, fileCount: 0, totalImageCount: 0, totalRating: 0 };
      aggregate.fileCount += Math.max(0, summary.fileCount);
      aggregate.totalImageCount += Math.max(0, summary.totalImageCount);
      aggregate.totalRating += Math.max(0, summary.totalRating);
      aggregates.set(key, aggregate);
    }

    const currentKey = item.creator.trim().toLocaleLowerCase('ja-JP');
    if (!aggregates.has(currentKey)) {
      aggregates.set(currentKey, {
        creator: item.creator,
        fileCount: Math.max(0, item.fileCount),
        totalImageCount: Math.max(0, item.totalImageCount),
        totalRating: Math.max(0, item.totalRating)
      });
    }
    const current = aggregates.get(currentKey)!;
    const values = [...aggregates.values()];
    return {
      category: item.category,
      creators: values.map(value => value.creator),
      creatorCount: values.length,
      fileCount: current.fileCount,
      fileRank: 1 + values.filter(value => value.fileCount > current.fileCount).length,
      totalImageCount: current.totalImageCount,
      totalImageCountRank: 1 + values.filter(value => value.totalImageCount > current.totalImageCount).length,
      totalRating: current.totalRating,
      totalRatingRank: 1 + values.filter(value => value.totalRating > current.totalRating).length
    };
  }

  function closeCreatorTracking() {
    saveCreatorTracking();
    captureActiveCreatorTrackingTab();
    creatorTrackingIsLoading = false;
    creatorTrackingError = '';
    activateView('creators');
  }

  function markCreatorTrackingDirty() {
    creatorTrackingDirty = true;
    syncActiveCreatorTrackingTab();
  }

  function saveCreatorTrackingTab(tabId: string, forceRefresh = false) {
    captureActiveCreatorTrackingTab();
    const tab = creatorTrackingTabs.find(candidate => candidate.id === tabId);
    if (!tab?.tracking || tab.isSaving || !tab.dirty) return '';

    const requestId = `creator-tracking-${nextCreatorTrackingRequestId++}`;
    creatorTrackingTabs = creatorTrackingTabs.map(candidate => candidate.id === tabId
      ? { ...candidate, isSaving: true, dirty: false, error: '', requestId }
      : candidate);
    if (tabId === activeCreatorTrackingTabId) {
      creatorTrackingIsSaving = true;
      creatorTrackingDirty = false;
      creatorTrackingError = '';
      creatorTrackingRequestId = requestId;
    }
    postHostMessage({
      type: 'creator.tracking.save',
      requestId,
      tracking: tab.tracking,
      dashboardContext: tab.dashboardContext,
      forceRefresh
    });
    return requestId;
  }

  function saveCreatorTracking() {
    if (!activeCreatorTrackingTabId) return;
    saveCreatorTrackingTab(activeCreatorTrackingTabId);
  }

  function refreshCreatorTracking() {
    captureActiveCreatorTrackingTab();
    const tab = creatorTrackingTabs.find(candidate => candidate.id === activeCreatorTrackingTabId);
    if (!tab?.tracking || tab.isLoading || tab.isSaving) {
      return;
    }

    if (tab.dirty) {
      const requestId = saveCreatorTrackingTab(tab.id, true);
      if (requestId) {
        creatorTrackingRefreshRequestIds = new Set([...creatorTrackingRefreshRequestIds, requestId]);
      }
      return;
    }

    const requestId = `creator-tracking-${nextCreatorTrackingRequestId++}`;
    creatorTrackingRefreshRequestIds = new Set([...creatorTrackingRefreshRequestIds, requestId]);
    creatorTrackingTabs = creatorTrackingTabs.map(candidate => candidate.id === tab.id
      ? { ...candidate, requestId, isLoading: true, error: '' }
      : candidate);
    creatorTrackingRequestId = requestId;
    creatorTrackingIsLoading = true;
    creatorTrackingError = '';
    postHostMessage({
      type: 'creator.tracking.get',
      requestId,
      creator: tab.creator,
      dashboardContext: tab.dashboardContext,
      templateContext: {
        displayName: tab.summary.creator,
        mainStoragePath: tab.summary.creatorFolder
      },
      forceRefresh: true
    });
  }

  function flushCreatorTrackingBeforeExit() {
    captureActiveCreatorTrackingTab();
    postHostMessage({
      type: 'creator.tracking.flush',
      entries: creatorTrackingTabs
        .filter(tab => tab.tracking !== null && (tab.dirty || tab.isSaving))
        .map(tab => ({ tracking: tab.tracking }))
    });
  }

  function addCreatorTrackingActivityLink() {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      activityLinks: [
        ...creatorTracking.activityLinks,
        {
          label: creatorTrackingSettingsDraft.activityPlaces[0]?.label ?? '',
          url: '',
          status: '',
          note: '',
          followUpEnabled: false,
          followUpDays: 30
        }
      ]
    };
    markCreatorTrackingDirty();
  }

  function updateCreatorTrackingActivityLink(
    index: number,
    patch: Partial<CreatorTrackingActivityLink>) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      activityLinks: creatorTracking.activityLinks.map((link, candidateIndex) =>
        candidateIndex === index ? { ...link, ...patch } : link)
    };
    markCreatorTrackingDirty();
  }

  function openCreatorTrackingActivityLink(url: string) {
    if (!/^https?:\/\//i.test(url.trim())) {
      showExplorerToast('http または https のURLを入力してください。', 'error');
      return;
    }
    postHostMessage({ type: 'creator.tracking.url.open', url: url.trim() });
  }

  function getCreatorTrackingActivityPlaceholder(label: string) {
    return getCreatorTrackingActivityPlaceSetting(label)?.placeholder
      ?? 'https://... またはチャンネル名';
  }

  function getCreatorTrackingActivityPlaceSetting(label: string) {
    return creatorTrackingSettingsDraft.activityPlaces.find(place =>
      place.label.localeCompare(label, 'ja-JP', { sensitivity: 'base' }) === 0);
  }

  function getCreatorTrackingActivityIcon(label: string) {
    return getCreatorTrackingActivityPlaceSetting(label)?.iconDataUri ?? '';
  }

  function startCreatorTrackingActivityLinkDrag(event: DragEvent, index: number) {
    draggedCreatorTrackingActivityLinkIndex = index;
    event.dataTransfer?.setData('text/plain', String(index));
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  function reorderCreatorTrackingActivityLink(event: DragEvent, targetIndex: number) {
    event.preventDefault();
    if (!creatorTracking) return;
    const sourceIndex = draggedCreatorTrackingActivityLinkIndex;
    draggedCreatorTrackingActivityLinkIndex = null;
    if (sourceIndex === null || sourceIndex === targetIndex || sourceIndex < 0 || sourceIndex >= creatorTracking.activityLinks.length) return;
    const activityLinks = [...creatorTracking.activityLinks];
    const [movedLink] = activityLinks.splice(sourceIndex, 1);
    activityLinks.splice(targetIndex, 0, movedLink);
    creatorTracking = { ...creatorTracking, activityLinks };
    markCreatorTrackingDirty();
  }

  function removeCreatorTrackingActivityLink(index: number) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      activityLinks: creatorTracking.activityLinks.filter((_, candidateIndex) => candidateIndex !== index)
    };
    markCreatorTrackingDirty();
  }

  function startCreatorTrackingBillingRowDrag(
    event: DragEvent,
    view: 'subscriptions' | 'purchases',
    index: number) {
    draggedCreatorTrackingBillingRow = { view, index };
    event.dataTransfer?.setData('text/plain', `${view}:${index}`);
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  function reorderCreatorTrackingBillingRow(
    event: DragEvent,
    view: 'subscriptions' | 'purchases',
    targetIndex: number) {
    event.preventDefault();
    if (!creatorTracking || draggedCreatorTrackingBillingRow?.view !== view) return;
    const sourceIndex = draggedCreatorTrackingBillingRow.index;
    draggedCreatorTrackingBillingRow = null;
    if (sourceIndex === targetIndex || sourceIndex < 0) return;

    if (view === 'subscriptions') {
      if (sourceIndex >= creatorTracking.subscriptionHistory.length) return;
      const subscriptionHistory = [...creatorTracking.subscriptionHistory];
      const [movedRow] = subscriptionHistory.splice(sourceIndex, 1);
      subscriptionHistory.splice(targetIndex, 0, movedRow);
      creatorTracking = { ...creatorTracking, subscriptionHistory };
    }
    else {
      if (sourceIndex >= creatorTracking.purchaseHistory.length) return;
      const purchaseHistory = [...creatorTracking.purchaseHistory];
      const [movedRow] = purchaseHistory.splice(sourceIndex, 1);
      purchaseHistory.splice(targetIndex, 0, movedRow);
      creatorTracking = { ...creatorTracking, purchaseHistory };
    }
    markCreatorTrackingDirty();
  }

  function finishCreatorTrackingBillingRowDrag() {
    draggedCreatorTrackingBillingRow = null;
  }

  function createCreatorTrackingHistoryId(prefix: string) {
    return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 9)}`;
  }

  function addCreatorTrackingSubscription() {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      subscriptionHistory: [
        ...creatorTracking.subscriptionHistory,
        {
          id: createCreatorTrackingHistoryId('subscription'),
          platform: '',
          plan: '',
          currency: 'JPY',
          amount: 0,
          billingFrequency: 'monthly',
          startedOn: '',
          renewalOn: '',
          endingPlanned: false,
          reminder: false,
          wishlist: false,
          isActive: true
        }
      ]
    };
    markCreatorTrackingDirty();
  }

  function updateCreatorTrackingSubscription(index: number, patch: Partial<CreatorTrackingSubscription>) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      subscriptionHistory: creatorTracking.subscriptionHistory.map((subscription, candidateIndex) =>
        candidateIndex === index ? { ...subscription, ...patch } : subscription)
    };
    markCreatorTrackingDirty();
  }

  function toggleCreatorTrackingSubscriptionWishlist(index: number) {
    if (!creatorTracking) return;
    const subscription = creatorTracking.subscriptionHistory[index];
    if (!subscription) return;
    const wishlist = !subscription.wishlist;
    updateCreatorTrackingSubscription(index, wishlist
      ? { wishlist: true, isActive: false, endingPlanned: false, reminder: false }
      : { wishlist: false });
  }

  function toggleCreatorTrackingSubscriptionActive(index: number) {
    if (!creatorTracking) return;
    const subscription = creatorTracking.subscriptionHistory[index];
    if (!subscription) return;
    const isActive = !subscription.isActive;
    updateCreatorTrackingSubscription(index, isActive
      ? { isActive: true, wishlist: false }
      : { isActive: false });
  }

  function updateCreatorTrackingSubscriptionSchedule(
    index: number,
    field: 'startedOn' | 'billingFrequency',
    value: string) {
    if (!creatorTracking) return;
    const subscription = creatorTracking.subscriptionHistory[index];
    if (!subscription) return;
    const startedOn = field === 'startedOn' ? value : subscription.startedOn;
    const billingFrequency = field === 'billingFrequency' ? value : subscription.billingFrequency;
    updateCreatorTrackingSubscription(index, {
      [field]: value,
      renewalOn: calculateCreatorTrackingRenewalDate(startedOn, billingFrequency)
    });
  }

  function removeCreatorTrackingSubscription(index: number) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      subscriptionHistory: creatorTracking.subscriptionHistory.filter((_, candidateIndex) => candidateIndex !== index)
    };
    markCreatorTrackingDirty();
  }

  function addCreatorTrackingPurchase() {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      purchaseHistory: [
        ...creatorTracking.purchaseHistory,
        {
          id: createCreatorTrackingHistoryId('purchase'),
          platform: '',
          productName: '',
          currency: 'JPY',
          amount: 0,
          purchasedOn: '',
          wishlist: false
        }
      ]
    };
    markCreatorTrackingDirty();
  }

  function updateCreatorTrackingPurchase(index: number, patch: Partial<CreatorTrackingPurchase>) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      purchaseHistory: creatorTracking.purchaseHistory.map((purchase, candidateIndex) =>
        candidateIndex === index ? { ...purchase, ...patch } : purchase)
    };
    markCreatorTrackingDirty();
  }

  function toggleCreatorTrackingPurchaseWishlist(index: number) {
    if (!creatorTracking) return;
    const purchase = creatorTracking.purchaseHistory[index];
    if (!purchase) return;
    updateCreatorTrackingPurchase(index, { wishlist: !purchase.wishlist });
  }

  function removeCreatorTrackingPurchase(index: number) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      purchaseHistory: creatorTracking.purchaseHistory.filter((_, candidateIndex) => candidateIndex !== index)
    };
    markCreatorTrackingDirty();
  }

  function calculateCreatorTrackingRenewalDate(startedOn: string, billingFrequency: string) {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(startedOn);
    if (!match) return '';
    const year = Number(match[1]);
    const month = Number(match[2]);
    const day = Number(match[3]);
    if (month < 1 || month > 12 || day < 1 || day > 31) return '';

    const monthsToAdd = billingFrequency === 'annual'
      ? 12
      : billingFrequency === 'semiannual'
        ? 6
        : billingFrequency === 'quarterly'
          ? 3
          : 1;
    const today = new Date();
    const todayUtc = Date.UTC(today.getFullYear(), today.getMonth(), today.getDate());
    let cycle = 1;
    let absoluteMonth = year * 12 + month - 1 + monthsToAdd;
    let targetYear = Math.floor(absoluteMonth / 12);
    let targetMonthIndex = absoluteMonth % 12;
    let lastDayOfTargetMonth = new Date(Date.UTC(targetYear, targetMonthIndex + 1, 0)).getUTCDate();
    let targetDay = Math.min(day, lastDayOfTargetMonth);
    while (Date.UTC(targetYear, targetMonthIndex, targetDay) <= todayUtc && cycle < 1200) {
      cycle += 1;
      absoluteMonth = year * 12 + month - 1 + monthsToAdd * cycle;
      targetYear = Math.floor(absoluteMonth / 12);
      targetMonthIndex = absoluteMonth % 12;
      lastDayOfTargetMonth = new Date(Date.UTC(targetYear, targetMonthIndex + 1, 0)).getUTCDate();
      targetDay = Math.min(day, lastDayOfTargetMonth);
    }
    return `${targetYear.toString().padStart(4, '0')}-${(targetMonthIndex + 1).toString().padStart(2, '0')}-${targetDay.toString().padStart(2, '0')}`;
  }

  function normalizeCreatorTrackingMetric(value: unknown, fallback = 1) {
    const numericValue = Number(value);
    return Number.isFinite(numericValue) ? Math.max(1, Math.min(4, Math.round(numericValue))) : fallback;
  }

  function normalizeCreatorTrackingStorageLocations(
    locations: Array<Partial<CreatorTrackingStorageLocation>> | undefined,
    mainStoragePath: string,
    workStoragePath: string,
    unsortedStoragePath: string) {
    const source: Array<Partial<CreatorTrackingStorageLocation>> = Array.isArray(locations) && locations.length > 0
      ? locations
      : [
          { id: createCreatorTrackingHistoryId('storage'), usage: 'Gallery', path: mainStoragePath ?? '' },
          ...(workStoragePath?.trim()
            ? [{ id: createCreatorTrackingHistoryId('storage'), usage: 'Stockroom' as const, path: workStoragePath }]
            : []),
          ...(unsortedStoragePath?.trim()
            ? [{ id: createCreatorTrackingHistoryId('storage'), usage: 'Temporary' as const, path: unsortedStoragePath }]
            : [])
        ];
    const result: CreatorTrackingStorageLocation[] = [];
    const usedSingletons = new Set<CreatorTrackingStorageUsage>();
    for (const location of source) {
      const usage = location.usage === 'Gallery' || location.usage === 'Stockroom' || location.usage === 'Temporary'
        ? location.usage
        : null;
      if (!usage || (usage !== 'Temporary' && usedSingletons.has(usage))) continue;
      if (usage !== 'Temporary') usedSingletons.add(usage);
      result.push({
        id: String(location.id ?? '').trim() || createCreatorTrackingHistoryId('storage'),
        usage,
        path: String(location.path ?? '').trim()
      });
    }
    if (!result.some(location => location.usage === 'Gallery')) {
      result.unshift({ id: createCreatorTrackingHistoryId('storage'), usage: 'Gallery', path: mainStoragePath?.trim() ?? '' });
    }
    return result;
  }

  function applyCreatorTrackingStorageLocations(locations: CreatorTrackingStorageLocation[]) {
    if (!creatorTracking) return;
    creatorTracking = {
      ...creatorTracking,
      storageLocations: locations,
      mainStoragePath: locations.find(location => location.usage === 'Gallery')?.path ?? '',
      workStoragePath: locations.find(location => location.usage === 'Stockroom')?.path ?? ''
    };
    markCreatorTrackingDirty();
  }

  function getCreatorTrackingStorageUsageOptions(locationId: string) {
    const used = new Set((creatorTracking?.storageLocations ?? [])
      .filter(location => location.id !== locationId && location.usage !== 'Temporary')
      .map(location => location.usage));
    return (['Gallery', 'Stockroom', 'Temporary'] as CreatorTrackingStorageUsage[])
      .filter(usage => usage === 'Temporary' || !used.has(usage));
  }

  function addCreatorTrackingStorageLocation() {
    if (!creatorTracking) return;
    const options = getCreatorTrackingStorageUsageOptions('');
    const usage = options[0] ?? 'Temporary';
    applyCreatorTrackingStorageLocations([
      ...creatorTracking.storageLocations,
      { id: createCreatorTrackingHistoryId('storage'), usage, path: '' }
    ]);
  }

  function updateCreatorTrackingStorageLocation(id: string, patch: Partial<CreatorTrackingStorageLocation>) {
    if (!creatorTracking) return;
    applyCreatorTrackingStorageLocations(creatorTracking.storageLocations.map(location =>
      location.id === id ? { ...location, ...patch } : location));
  }

  function removeCreatorTrackingStorageLocation(location: CreatorTrackingStorageLocation) {
    if (!creatorTracking || location.usage !== 'Temporary') return;
    applyCreatorTrackingStorageLocations(creatorTracking.storageLocations.filter(candidate => candidate.id !== location.id));
  }

  function openCreatorTrackingStorageLocation(location: CreatorTrackingStorageLocation) {
    requestExplorerPathsOpen(
      [location.path],
      true,
      false,
      `${location.usage}のフォルダをExplorerで開けませんでした。`);
  }

  function canOpenCreatorTrackingStorageSplit() {
    const usages = new Set((creatorTracking?.storageLocations ?? []).map(location => location.usage));
    return usages.has('Gallery') && usages.has('Stockroom');
  }

  function openCreatorTrackingStorageSplit() {
    if (!creatorTracking || !canOpenCreatorTrackingStorageSplit()) return;
    const gallery = creatorTracking.storageLocations.find(location => location.usage === 'Gallery');
    const stockroom = creatorTracking.storageLocations.find(location => location.usage === 'Stockroom');
    if (!gallery?.path.trim() || !stockroom?.path.trim()) {
      showExplorerToast('GalleryとStockroomの両方にフォルダパスを入力してください。', 'error');
      return;
    }
    requestExplorerPathsOpen(
      [gallery.path, stockroom.path],
      true,
      true,
      'GalleryとStockroomの両方のフォルダを確認できないため、分割表示を開けませんでした。');
  }

  function normalizeCreatorTracking(tracking: CreatorTracking): CreatorTracking {
    const legacyFallback = tracking.personalRating > 0
      ? Math.max(1, Math.min(4, Math.round(tracking.personalRating / 1.25)))
      : 1;
    const sourceMetrics = tracking.evaluationMetrics ?? {} as Record<CreatorTrackingMetricKey, number>;
    const evaluationMetrics = {} as Record<CreatorTrackingMetricKey, number>;
    for (const metric of creatorTrackingMetricDefinitions) {
      evaluationMetrics[metric.key] = normalizeCreatorTrackingMetric(sourceMetrics[metric.key], legacyFallback);
    }

    const storageLocations = normalizeCreatorTrackingStorageLocations(
      tracking.storageLocations,
      tracking.mainStoragePath ?? '',
      tracking.workStoragePath ?? '',
      tracking.unsortedStoragePath ?? '');

    return {
      ...tracking,
      alternateName: tracking.alternateName ?? '',
      activityLinks: Array.isArray(tracking.activityLinks)
        ? tracking.activityLinks.map(link => ({
            ...link,
            followUpEnabled: link.followUpEnabled === true,
            followUpDays: Math.max(1, Math.min(99, Math.round(Number(link.followUpDays) || 30)))
          }))
        : [],
      subscriptionHistory: Array.isArray(tracking.subscriptionHistory)
        ? tracking.subscriptionHistory.map(subscription => ({ ...subscription, plan: subscription.plan ?? '' }))
        : [],
      purchaseHistory: Array.isArray(tracking.purchaseHistory) ? tracking.purchaseHistory : [],
      storageLocations,
      mainStoragePath: storageLocations.find(location => location.usage === 'Gallery')?.path ?? '',
      workStoragePath: storageLocations.find(location => location.usage === 'Stockroom')?.path ?? '',
      evaluationMetrics,
      personalRating: calculateCreatorTrackingRating(evaluationMetrics)
    };
  }

  function formatCreatorTrackingHeading(tracking: CreatorTracking) {
    const displayName = tracking.displayName.trim() || tracking.creator;
    const alternateName = tracking.alternateName?.trim();
    return alternateName ? `${displayName}（${alternateName}）` : displayName;
  }

  function formatCreatorTrackingRank(rank: number, creatorCount: number) {
    return `${Math.max(1, rank).toLocaleString('ja-JP')}/${Math.max(1, creatorCount).toLocaleString('ja-JP')}位`;
  }

  function getCreatorTrackingRankTier(rank: number, creatorCount: number): 'gold' | 'silver' | 'bronze' | '' {
    const ratio = Math.max(1, rank) / Math.max(1, creatorCount);
    if (ratio <= 0.05) return 'gold';
    if (ratio <= 0.10) return 'silver';
    if (ratio <= 0.15) return 'bronze';
    return '';
  }

  function formatCreatorTrackingSpendAmount(amount: number, currency: string) {
    const digits = currency === 'JPY' || currency === 'KRW' ? 0 : 2;
    return amount.toLocaleString('ja-JP', {
      minimumFractionDigits: 0,
      maximumFractionDigits: digits
    });
  }

  function getCreatorTrackingCheckAge(lastCheckedOn: string, activityLinks: CreatorTrackingActivityLink[]) {
    const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(lastCheckedOn);
    if (!match) return { days: null as number | null, level: '' as '' | 'warning' | 'danger', followWarnFlg: false, followAlertFlg: false };
    const checked = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
    if (Number.isNaN(checked.getTime())) return { days: null as number | null, level: '' as '' | 'warning' | 'danger', followWarnFlg: false, followAlertFlg: false };
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    checked.setHours(0, 0, 0, 0);
    const days = Math.max(0, Math.floor((today.getTime() - checked.getTime()) / 86_400_000));
    const followUpDays = activityLinks
      .filter(link => link.followUpEnabled)
      .map(link => Math.max(1, Math.min(99, Math.round(Number(link.followUpDays) || 30))))
      .reduce((minimum, value) => Math.min(minimum, value), Number.POSITIVE_INFINITY);
    const followWarnFlg = Number.isFinite(followUpDays) && days >= followUpDays;
    const followAlertFlg = Number.isFinite(followUpDays) && days >= Math.ceil(followUpDays * 1.5);
    const level = followAlertFlg ? 'danger' : followWarnFlg ? 'warning' : '';
    return { days, level, followWarnFlg, followAlertFlg } as const;
  }

  function formatCreatorTrackingInputDate(date: Date) {
    const year = date.getFullYear();
    const month = String(date.getMonth() + 1).padStart(2, '0');
    const day = String(date.getDate()).padStart(2, '0');
    return `${year}-${month}-${day}`;
  }

  function setCreatorTrackingLastCheckToToday() {
    if (!creatorTracking) return;
    creatorTracking = { ...creatorTracking, lastActivityOn: formatCreatorTrackingInputDate(new Date()) };
    markCreatorTrackingDirty();
  }

  function synchronizeCreatorSummaryReminderState(summary: GalleryCreatorSummary, tracking: CreatorTracking) {
    const checkAge = getCreatorTrackingCheckAge(tracking.lastActivityOn, tracking.activityLinks);
    const sites = [...new Set(tracking.activityLinks.map(link => link.label.trim()).filter(Boolean))];
    const update = (item: GalleryCreatorSummary): GalleryCreatorSummary => ({
      ...item,
      trackingLastActivityOn: tracking.lastActivityOn,
      hasCreatorTracking: true,
      sinceLastCheckDays: checkAge.days ?? -1,
      followWarnFlg: checkAge.followWarnFlg,
      followAlertFlg: checkAge.followAlertFlg,
      trackingSites: sites
    });
    const matches = (item: GalleryCreatorSummary) => item.category === summary.category
      && item.creator.localeCompare(summary.creator, 'ja-JP', { sensitivity: 'base' }) === 0;
    galleryCreatorSummaries = galleryCreatorSummaries.map(item => matches(item) ? update(item) : item);
    if (creatorTrackingSummary && matches(creatorTrackingSummary)) {
      creatorTrackingSummary = update(creatorTrackingSummary);
    }
  }

  function getCreatorTrackingCompositionTotal(slices: GalleryCreatorCompositionSlice[] | undefined) {
    return (slices ?? []).reduce((total, slice) => total + Math.max(0, Number(slice.count) || 0), 0);
  }

  function getCreatorTrackingCompositionPercentage(
    slice: GalleryCreatorCompositionSlice,
    slices: GalleryCreatorCompositionSlice[] | undefined) {
    const total = getCreatorTrackingCompositionTotal(slices);
    return total > 0 ? Math.max(0, slice.count) / total * 100 : 0;
  }

  function formatCreatorTrackingCompositionPercentage(
    slice: GalleryCreatorCompositionSlice,
    slices: GalleryCreatorCompositionSlice[] | undefined) {
    const percentage = Math.round(getCreatorTrackingCompositionPercentage(slice, slices) * 10) / 10;
    return `${percentage.toLocaleString('ja-JP', { maximumFractionDigits: 1 })}%`;
  }

  function getCreatorTrackingCompositionSegments(slices: GalleryCreatorCompositionSlice[] | undefined) {
    const visibleSlices = (slices ?? []).filter(slice => slice.count > 0);
    const total = getCreatorTrackingCompositionTotal(visibleSlices);
    if (total <= 0) return [];
    let cursor = 0;
    return visibleSlices.map((slice, index) => {
      const startPercent = cursor / total * 100;
      cursor += slice.count;
      const lengthPercent = slice.count / total * 100;
      return {
        slice,
        index,
        startPercent,
        lengthPercent
      };
    });
  }

  function getCreatorTrackingCompositionColor(slice: GalleryCreatorCompositionSlice, index: number) {
    return slice.label === 'その他'
      ? '#4f5d6d'
      : creatorTrackingCompositionColors[index % creatorTrackingCompositionColors.length];
  }

  function getCreatorTrackingCompositionLabelPosition(
    slices: GalleryCreatorCompositionSlice[] | undefined,
    index: number): { x: number; y: number; overlapsArc: boolean } {
    const visibleSlices = (slices ?? [])
      .map((slice, originalIndex) => ({ slice, originalIndex }))
      .filter(entry => entry.slice.count > 0);
    const total = getCreatorTrackingCompositionTotal(visibleSlices.map(entry => entry.slice));
    const visibleIndex = visibleSlices.findIndex(entry => entry.originalIndex === index);
    if (total <= 0 || visibleIndex < 0) return { x: 50, y: 50, overlapsArc: false };

    const labelSlots = [
      { angle: -Math.PI / 2, x: 50, y: 8, overlapsArc: false },
      { angle: -Math.PI * 3 / 8, x: 68, y: 8, overlapsArc: false },
      { angle: -Math.PI / 4, x: 82.5, y: 13, overlapsArc: false },
      { angle: -Math.PI / 8, x: 82.5, y: 31.5, overlapsArc: true },
      { angle: 0, x: 82.5, y: 50, overlapsArc: true },
      { angle: Math.PI / 8, x: 82.5, y: 68.5, overlapsArc: true },
      { angle: Math.PI / 4, x: 82.5, y: 87, overlapsArc: false },
      { angle: Math.PI * 3 / 8, x: 68, y: 92, overlapsArc: false },
      { angle: Math.PI / 2, x: 50, y: 92, overlapsArc: false },
      { angle: Math.PI * 5 / 8, x: 32, y: 92, overlapsArc: false },
      { angle: Math.PI * 3 / 4, x: 17.5, y: 87, overlapsArc: false },
      { angle: Math.PI * 7 / 8, x: 17.5, y: 68.5, overlapsArc: true },
      { angle: Math.PI, x: 17.5, y: 50, overlapsArc: true },
      { angle: -Math.PI * 7 / 8, x: 17.5, y: 31.5, overlapsArc: true },
      { angle: -Math.PI * 3 / 4, x: 17.5, y: 13, overlapsArc: false },
      { angle: -Math.PI * 5 / 8, x: 32, y: 8, overlapsArc: false }
    ];
    const fullTurn = Math.PI * 2;
    let cursor = 0;
    const sliceArcs = visibleSlices.map(entry => {
      const startAngle = cursor / total * fullTurn - Math.PI / 2;
      const length = entry.slice.count / total * fullTurn;
      cursor += entry.slice.count;
      return { startAngle, length, middleAngle: startAngle + length / 2 };
    });
    const sliceAngles = sliceArcs.map(arc => arc.middleAngle);
    if (sliceAngles.length > labelSlots.length) {
      const angle = sliceAngles[visibleIndex];
      return {
        x: Math.max(17.5, Math.min(82.5, 50 + Math.cos(angle) * 43)),
        y: Math.max(8, Math.min(92, 50 + Math.sin(angle) * 43)),
        overlapsArc: false
      };
    }

    const assignedSlotIndexes = Array<number>(sliceAngles.length).fill(-1);
    const usedSlotIndexes = new Set<number>();
    const assignmentOrder = visibleSlices
      .map((entry, sliceIndex) => ({ sliceIndex, count: entry.slice.count }))
      .sort((left, right) => left.count - right.count || left.sliceIndex - right.sliceIndex);
    assignmentOrder.forEach(({ sliceIndex }) => {
      const angle = sliceAngles[sliceIndex];
      let nearestSlotIndex = -1;
      let nearestDistance = Number.POSITIVE_INFINITY;
      let nearestSignedDistance = Number.NEGATIVE_INFINITY;
      const unusedSlotIndexes = labelSlots
        .map((_, slotIndex) => slotIndex)
        .filter(slotIndex => !usedSlotIndexes.has(slotIndex));
      const separatedSlotIndexes = unusedSlotIndexes
        .filter(slotIndex => {
          const previousSlotIndex = (slotIndex - 1 + labelSlots.length) % labelSlots.length;
          const nextSlotIndex = (slotIndex + 1) % labelSlots.length;
          return !usedSlotIndexes.has(previousSlotIndex)
            && !usedSlotIndexes.has(nextSlotIndex);
        });
      const arc = sliceArcs[sliceIndex];
      const getClockwiseOffset = (slotIndex: number) =>
        ((labelSlots[slotIndex].angle - arc.startAngle) % fullTurn + fullTurn) % fullTurn;
      const slotsWithinArc = unusedSlotIndexes.filter(slotIndex => getClockwiseOffset(slotIndex) <= arc.length + 1e-10);

      if (slotsWithinArc.length > 0) {
        if (arc.length / fullTurn >= 0.2 - 1e-10) {
          const getSignedCenterDistance = (slotIndex: number) =>
            Math.atan2(Math.sin(labelSlots[slotIndex].angle - arc.middleAngle), Math.cos(labelSlots[slotIndex].angle - arc.middleAngle));
          nearestSlotIndex = slotsWithinArc.reduce((centralSlotIndex, slotIndex) => {
            const centralSignedDistance = getSignedCenterDistance(centralSlotIndex);
            const signedDistance = getSignedCenterDistance(slotIndex);
            const centralDistance = Math.abs(centralSignedDistance);
            const distance = Math.abs(signedDistance);
            const isEquidistant = Math.abs(distance - centralDistance) <= 1e-10;
            return distance < centralDistance - 1e-10 || (isEquidistant && signedDistance > centralSignedDistance)
              ? slotIndex
              : centralSlotIndex;
          });
        }
        else {
          const separatedSlotsWithinArc = slotsWithinArc.filter(slotIndex => separatedSlotIndexes.includes(slotIndex));
          const candidateSlotIndexes = separatedSlotsWithinArc.length > 0 ? separatedSlotsWithinArc : slotsWithinArc;
          // Clockwise is left when facing the center, so the greatest offset is the arc's leftmost candidate.
          nearestSlotIndex = candidateSlotIndexes.reduce((leftmostSlotIndex, slotIndex) =>
            getClockwiseOffset(slotIndex) > getClockwiseOffset(leftmostSlotIndex) ? slotIndex : leftmostSlotIndex);
        }
      }
      else {
        const candidateSlotIndexes = separatedSlotIndexes.length > 0 ? separatedSlotIndexes : unusedSlotIndexes;
        candidateSlotIndexes.forEach(slotIndex => {
          const slot = labelSlots[slotIndex];
          const signedDistance = Math.atan2(Math.sin(slot.angle - angle), Math.cos(slot.angle - angle));
          const distance = Math.abs(signedDistance);
          const isEquidistant = Math.abs(distance - nearestDistance) <= 1e-10;
          if (distance < nearestDistance - 1e-10 || (isEquidistant && signedDistance > nearestSignedDistance)) {
            nearestDistance = distance;
            nearestSignedDistance = signedDistance;
            nearestSlotIndex = slotIndex;
          }
        });
      }
      if (nearestSlotIndex >= 0) {
        assignedSlotIndexes[sliceIndex] = nearestSlotIndex;
        usedSlotIndexes.add(nearestSlotIndex);
      }
    });
    const slotIndex = assignedSlotIndexes[visibleIndex];
    return labelSlots[slotIndex] ?? { x: 50, y: 50, overlapsArc: false };
  }

  function getCreatorTrackingCompositionAriaLabel(
    heading: string,
    slices: GalleryCreatorCompositionSlice[] | undefined) {
    const details = (slices ?? [])
      .map(slice => `${slice.label} ${formatCreatorTrackingCompositionPercentage(slice, slices)}`)
      .join('、');
    return details ? `${heading}: ${details}` : `${heading}: データなし`;
  }

  function getCreatorTrackingArchiveSeries(
    dashboard: CreatorTrackingDashboard,
    scale: 'week' | 'month' | 'year') {
    const source = Array.isArray(dashboard.archiveSnapshots) && dashboard.archiveSnapshots.length > 0
      ? dashboard.archiveSnapshots
      : dashboard.archiveMonths.map(point => ({
          date: `${point.month}-01`,
          fileCount: point.fileCount,
          imageCount: point.imageCount
        }));
    const grouped = new Map<string, { month: string; fileCount: number; imageCount: number; label: string }>();
    for (const point of source) {
      const key = scale === 'year'
        ? point.date.slice(0, 4)
        : scale === 'month'
          ? point.date.slice(0, 7)
          : getCreatorTrackingArchiveWeekStart(point.date);
      const label = scale === 'year'
        ? key
        : scale === 'month'
          ? formatCreatorTrackingArchiveMonth(key)
          : formatCreatorTrackingArchiveWeek(key);
      grouped.set(key, { month: key, fileCount: point.fileCount, imageCount: point.imageCount, label });
    }
    return [...grouped.values()];
  }

  function getCreatorTrackingArchiveWeekStart(dateValue: string) {
    const date = new Date(`${dateValue}T00:00:00Z`);
    if (Number.isNaN(date.getTime())) return dateValue;
    const daysSinceMonday = (date.getUTCDay() + 6) % 7;
    date.setUTCDate(date.getUTCDate() - daysSinceMonday);
    return date.toISOString().slice(0, 10);
  }

  function formatCreatorTrackingArchiveWeek(dateValue: string) {
    const [year, month, day] = dateValue.split('-');
    return `${year.slice(-2)}/${Number(month)}/${Number(day)}`;
  }

  async function setCreatorTrackingArchiveScale(value: 'week' | 'month' | 'year') {
    creatorTrackingArchiveScale = value;
    syncActiveCreatorTrackingTab();
    await tick();
    scrollCreatorTrackingArchiveToEnd();
  }

  function scrollCreatorTrackingArchiveToEnd() {
    requestAnimationFrame(() => {
      if (creatorTrackingArchiveScrollElement) {
        creatorTrackingArchiveScrollElement.scrollLeft = creatorTrackingArchiveScrollElement.scrollWidth;
      }
    });
  }

  function formatCreatorTrackingArchiveMonth(month: string) {
    const [year, monthValue] = month.split('-');
    return `${year}/${monthValue}`;
  }

  function getCreatorTrackingArchiveFileMaximum(dashboard: CreatorTrackingDashboard, scale: 'week' | 'month' | 'year') {
    return Math.max(1, ...getCreatorTrackingArchiveSeries(dashboard, scale).map(point => point.fileCount));
  }

  function getCreatorTrackingArchiveImageMaximum(dashboard: CreatorTrackingDashboard, scale: 'week' | 'month' | 'year') {
    return Math.max(1, ...getCreatorTrackingArchiveSeries(dashboard, scale).map(point => point.imageCount));
  }

  function getCreatorTrackingArchiveBarHeight(fileCount: number, dashboard: CreatorTrackingDashboard, scale: 'week' | 'month' | 'year') {
    return Math.max(0, fileCount) / getCreatorTrackingArchiveFileMaximum(dashboard, scale) * 100;
  }

  function getCreatorTrackingArchiveLinePoints(dashboard: CreatorTrackingDashboard, scale: 'week' | 'month' | 'year') {
    const points = getCreatorTrackingArchiveSeries(dashboard, scale);
    const maximum = getCreatorTrackingArchiveImageMaximum(dashboard, scale);
    return points.map((point, index) => {
      const x = (index + 0.5) / Math.max(1, points.length) * 100;
      const y = 100 - Math.max(0, point.imageCount) / maximum * 100;
      return `${x},${y}`;
    }).join(' ');
  }

  function calculateCreatorTrackingRating(metrics: Record<CreatorTrackingMetricKey, number>) {
    const weightedRating = creatorTrackingSettingsDraft.metrics.reduce(
      (total, metric) => total + normalizeCreatorTrackingMetric(metrics[metric.key]) * Number(metric.weightPercent ?? 0) / 100,
      0);
    return Math.floor((weightedRating * 1.25 + 1e-9) * 10) / 10;
  }

  function setCreatorTrackingMetric(key: CreatorTrackingMetricKey, value: number) {
    if (!creatorTracking) return;
    const evaluationMetrics = {
      ...creatorTracking.evaluationMetrics,
      [key]: normalizeCreatorTrackingMetric(value)
    };
    creatorTracking = {
      ...creatorTracking,
      evaluationMetrics,
      personalRating: calculateCreatorTrackingRating(evaluationMetrics)
    };
    markCreatorTrackingDirty();
  }

  function getCreatorTrackingStarFill(starIndex: number) {
    if (!creatorTracking) return 0;
    return Math.max(0, Math.min(1, creatorTracking.personalRating - starIndex)) * 100;
  }

  function getCreatorTrackingRadarPoint(index: number, value: number, radius = 84) {
    const angle = (index * 60 - 90) * Math.PI / 180;
    const scaledRadius = radius * Math.max(0, Math.min(4, value)) / 4;
    return {
      x: 180 + Math.cos(angle) * scaledRadius,
      y: 137 + Math.sin(angle) * scaledRadius
    };
  }

  function getCreatorTrackingRadarPolygon(value: number) {
    return creatorTrackingMetricDefinitions
      .map((_, index) => getCreatorTrackingRadarPoint(index, value))
      .map((point) => `${point.x},${point.y}`)
      .join(' ');
  }

  function getCreatorTrackingRadarValuePolygon(metrics: Record<CreatorTrackingMetricKey, number>) {
    return creatorTrackingMetricDefinitions
      .map((metric, index) => getCreatorTrackingRadarPoint(index, metrics[metric.key]))
      .map((point) => `${point.x},${point.y}`)
      .join(' ');
  }

  function getCreatorTrackingRadarLabelAnchor(x: number) {
    if (x < 170) return 'end';
    if (x > 190) return 'start';
    return 'middle';
  }

  function toggleGalleryFilter<T>(selected: T[], value: T, event: MouseEvent): T[] {
    const isSelected = selected.includes(value);
    if (event.ctrlKey) {
      return isSelected ? selected.filter((item) => item !== value) : [...selected, value];
    }

    return selected.length === 1 && isSelected ? [] : [value];
  }

  function setGalleryRatingFilter(rating: number, event: MouseEvent) {
    const nextRatingFilters = toggleGalleryFilter(galleryRatingFilters, rating, event);
    galleryRatingFilters = nextRatingFilters;
    loadGalleryWorks(false, nextRatingFilters, true);
  }

  function setGalleryTagFilter(tag: string, event: MouseEvent) {
    const wasHiddenWhenCollapsed = isHiddenWhenCollapsed(event);
    const nextTags = toggleGalleryFilter(galleryTagFilters, tag, event);
    galleryTagFilters = nextTags;
    galleryPromotedTags = updateGalleryPromotedFilters(
      galleryPromotedTags,
      nextTags,
      tag,
      wasHiddenWhenCollapsed && !galleryCollapsePins.tag);
    if (shouldAutoCollapseGalleryFilter(galleryTagFiltersExpanded, galleryCollapsePins.tag, nextTags, tag, wasHiddenWhenCollapsed)) {
      galleryTagFiltersExpanded = false;
    }
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function openGalleryTagAssignment(sourceWork: GalleryWork | null = galleryContextTargetWork ?? galleryContextMenu?.work ?? null) {
    try {
      closeGalleryContextMenu();
      let works = galleryWorks.filter((work) => selectedGalleryWorkIds.has(work.id));
      if (works.length === 0 && sourceWork) {
        works = [sourceWork];
      }

      if (works.length === 0) {
        showExplorerToast('Tagを登録する作品を選択してください。', 'error');
        return;
      }

      const targetWork = sourceWork ?? works[0];
      if (!targetWork) {
        showExplorerToast('Tagを登録する作品を特定できませんでした。', 'error');
        return;
      }

      const assignment: GalleryTagAssignment = {
        requestId: '',
        sourcePath: targetWork.path,
        works,
        category: targetWork.category,
        creator: (targetWork.creator ?? '').trim(),
        title: (targetWork.title ?? '').trim(),
        creatorTitleTags: [],
        availableTags: [],
        commonAssignedTags: [],
        creatorQuery: '',
        query: '',
        selectedTagId: null,
        selectedAssignedTagIds: [],
        assignedTagsPanelOpen: false,
        isLoading: true,
        isSaving: false
      };
      flushSync(() => {
        galleryTitleAssignment = null;
        galleryCharacterAssignment = null;
        galleryTagAssignment = assignment;
      });
      requestGalleryTagAssignmentOptions(assignment);
    }
    catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      showExplorerToast(`Tag画面を開けませんでした: ${message}`, 'error');
    }
  }

  function requestGalleryTagAssignmentOptions(assignment = galleryTagAssignment) {
    if (!assignment) {
      return;
    }

    const requestId = `gallery-tag-assignment-${nextGalleryTagAssignmentRequestId++}`;
    galleryTagAssignment = {
      ...assignment,
      requestId,
      isLoading: true,
      isSaving: false
    };
    postHostMessage({
      type: 'gallery.tagAssignment.options.request',
      requestId,
      category: assignment.category,
      sourcePath: assignment.sourcePath,
      paths: assignment.works.map((work) => work.path)
    });
  }

  function getGalleryTagAssignmentCreatorTitleTags(assignment: GalleryTagAssignment) {
    const query = assignment.creatorQuery.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryTagAssignmentCreatorFilterCache;
    if (cached?.source === assignment.creatorTitleTags && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.creatorTitleTags.filter((option) => option.searchText.includes(query))
      : assignment.creatorTitleTags;
    galleryTagAssignmentCreatorFilterCache = { source: assignment.creatorTitleTags, query, result };
    return result;
  }

  function getGalleryTagAssignmentAvailableTags(assignment: GalleryTagAssignment) {
    const query = assignment.query.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryTagAssignmentAvailableFilterCache;
    if (cached?.source === assignment.availableTags && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.availableTags.filter((option) => option.searchText.includes(query))
      : assignment.availableTags;
    galleryTagAssignmentAvailableFilterCache = { source: assignment.availableTags, query, result };
    return result;
  }

  function getGalleryTagAssignmentScopeLabel(assignment: GalleryTagAssignment) {
    return `${assignment.creator || 'Creator未設定'} / ${assignment.title || 'Title未設定'}`;
  }

  function selectGalleryTagAssignment(tagId: number) {
    if (!galleryTagAssignment || galleryTagAssignment.isSaving) {
      return;
    }
    galleryTagAssignment = { ...galleryTagAssignment, selectedTagId: tagId };
  }

  function toggleGalleryAssignedTagSelection(tagId: number) {
    if (!galleryTagAssignment || galleryTagAssignment.isSaving) {
      return;
    }

    const selected = galleryTagAssignment.selectedAssignedTagIds.includes(tagId)
      ? galleryTagAssignment.selectedAssignedTagIds.filter((id) => id !== tagId)
      : [...galleryTagAssignment.selectedAssignedTagIds, tagId];
    galleryTagAssignment = { ...galleryTagAssignment, selectedAssignedTagIds: selected };
  }

  function removeSelectedGalleryTags() {
    if (!galleryTagAssignment || galleryTagAssignment.selectedAssignedTagIds.length === 0 || galleryTagAssignment.isSaving) {
      return;
    }

    galleryTagAssignment = { ...galleryTagAssignment, isSaving: true };
    postHostMessage({
      type: 'gallery.tagAssignment.remove',
      paths: galleryTagAssignment.works.map((work) => work.path),
      tagIds: galleryTagAssignment.selectedAssignedTagIds
    });
  }

  function openGalleryTagAssignmentNewTag() {
    if (!galleryTagAssignment) {
      return;
    }

    galleryTagAssignmentReturnPending = true;
    createTagManagementDefinition();
    activateView('tags');
  }

  function returnToGalleryTagAssignment() {
    if (!galleryTagAssignment || !galleryTagAssignmentReturnPending) {
      return;
    }

    galleryTagAssignmentReturnPending = false;
    activeView = 'library';
    persistNavigationState();
    requestGalleryTagAssignmentOptions(galleryTagAssignment);
  }

  function applyGalleryTagAssignment() {
    if (!galleryTagAssignment || galleryTagAssignment.selectedTagId === null || galleryTagAssignment.isSaving) {
      return;
    }

    galleryTagAssignment = { ...galleryTagAssignment, isSaving: true };
    postHostMessage({
      type: 'gallery.tagAssignment.apply',
      paths: galleryTagAssignment.works.map((work) => work.path),
      tagId: galleryTagAssignment.selectedTagId,
      removeTagIds: galleryTagAssignment.selectedAssignedTagIds
    });
  }

  function closeGalleryTagAssignment() {
    if (galleryTagAssignment?.isSaving) {
      return;
    }

    galleryTagAssignment = null;
    galleryTagAssignmentReturnPending = false;
  }

  function openGalleryTitleAssignment(sourceWork: GalleryWork | null = galleryContextTargetWork ?? galleryContextMenu?.work ?? null) {
    try {
      closeGalleryContextMenu();
      let works = galleryWorks.filter((work) => selectedGalleryWorkIds.has(work.id));
      if (works.length === 0 && sourceWork) {
        works = [sourceWork];
      }

      if (works.length === 0) {
        showExplorerToast('Title属性を登録する作品を選択してください。', 'error');
        return;
      }

      const targetWork = sourceWork ?? works[0];
      if (!targetWork) {
        showExplorerToast('Title属性を登録する作品を特定できませんでした。', 'error');
        return;
      }

      const creator = (targetWork.creator ?? '').trim();
      const assignment: GalleryTitleAssignment = {
        requestId: '',
        characterRequestId: '',
        works,
        category: targetWork.category,
        creators: creator ? [creator] : [],
        creatorTitles: [],
        availableTitles: [],
        commonAssignedTitles: [],
        creatorTitleCharacters: [],
        availableCharacters: [],
        creatorQuery: '',
        query: '',
        characterCreatorQuery: '',
        characterQuery: '',
        categoryFilter: '',
        selectedTitleId: null,
        selectedCharacterId: null,
        selectedAssignedTitleIds: [],
        assignedTitlesPanelOpen: false,
        characterPanelOpen: false,
        isLoading: true,
        isCharacterLoading: false,
        isSaving: false
      };
      flushSync(() => {
        galleryTagAssignment = null;
        galleryTagAssignmentReturnPending = false;
        galleryCharacterAssignment = null;
        galleryTitleAssignment = assignment;
      });
      requestGalleryTitleAssignmentOptions(assignment);
    }
    catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      showExplorerToast(`Title属性画面を開けませんでした: ${message}`, 'error');
    }
  }

  function requestGalleryTitleAssignmentOptions(assignment = galleryTitleAssignment) {
    if (!assignment) {
      return;
    }

    const requestId = `gallery-title-assignment-${nextGalleryTitleAssignmentRequestId++}`;
    galleryTitleAssignment = {
      ...assignment,
      requestId,
      isLoading: true,
      isSaving: false
    };
    postHostMessage({
      type: 'gallery.titleAssignment.options.request',
      requestId,
      category: assignment.category,
      creators: assignment.creators,
      paths: assignment.works.map((work) => work.path)
    });
  }

  function getGalleryTitleAssignmentAvailableTitles(assignment: GalleryTitleAssignment) {
    const query = assignment.query.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryTitleAssignmentAvailableFilterCache;
    if (
      cached?.source === assignment.availableTitles
      && cached.query === query
      && cached.categoryFilter === assignment.categoryFilter
    ) {
      return cached.result;
    }

    const result = assignment.availableTitles.filter((option) =>
      (!assignment.categoryFilter || option.categoryName === assignment.categoryFilter) &&
      (!query || option.searchText.includes(query)));
    galleryTitleAssignmentAvailableFilterCache = {
      source: assignment.availableTitles,
      query,
      categoryFilter: assignment.categoryFilter,
      result
    };
    return result;
  }

  function getGalleryTitleAssignmentCreatorTitles(assignment: GalleryTitleAssignment) {
    const query = assignment.creatorQuery.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryTitleAssignmentCreatorFilterCache;
    if (cached?.source === assignment.creatorTitles && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.creatorTitles.filter((option) => option.searchText.includes(query))
      : assignment.creatorTitles;
    galleryTitleAssignmentCreatorFilterCache = { source: assignment.creatorTitles, query, result };
    return result;
  }

  function getGalleryTitleAssignmentCategories(assignment: GalleryTitleAssignment) {
    const cached = galleryTitleAssignmentCategoryCache.get(assignment.availableTitles);
    if (cached) {
      return cached;
    }

    const result = [...new Set(assignment.availableTitles.map((option) => option.categoryName))]
      .sort((left, right) => left.localeCompare(right, 'ja-JP'));
    galleryTitleAssignmentCategoryCache.set(assignment.availableTitles, result);
    return result;
  }

  function selectGalleryTitleAssignment(titleId: number) {
    if (!galleryTitleAssignment || galleryTitleAssignment.isSaving) {
      return;
    }
    galleryTitleAssignment = {
      ...galleryTitleAssignment,
      selectedTitleId: titleId,
      selectedCharacterId: null,
      creatorTitleCharacters: [],
      availableCharacters: [],
      characterPanelOpen: true,
      isCharacterLoading: true
    };
    requestGalleryTitleAssignmentCharacters(galleryTitleAssignment, titleId);
  }

  function requestGalleryTitleAssignmentCharacters(assignment = galleryTitleAssignment, titleId = assignment?.selectedTitleId ?? null) {
    if (!assignment || titleId === null) {
      return;
    }

    const requestId = `gallery-title-assignment-characters-${nextGalleryTitleAssignmentCharacterRequestId++}`;
    galleryTitleAssignment = {
      ...assignment,
      characterRequestId: requestId,
      isCharacterLoading: true
    };
    postHostMessage({
      type: 'gallery.titleAssignment.characters.request',
      requestId,
      category: assignment.category,
      creators: assignment.creators,
      titleIds: [titleId],
      paths: assignment.works.map((work) => work.path)
    });
  }

  function getGalleryTitleAssignmentCreatorTitleCharacters(assignment: GalleryTitleAssignment) {
    const query = assignment.characterCreatorQuery.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryTitleAssignmentCharacterCreatorFilterCache;
    if (cached?.source === assignment.creatorTitleCharacters && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.creatorTitleCharacters.filter((option) => option.searchText.includes(query))
      : assignment.creatorTitleCharacters;
    galleryTitleAssignmentCharacterCreatorFilterCache = { source: assignment.creatorTitleCharacters, query, result };
    return result;
  }

  function getGalleryTitleAssignmentAvailableCharacters(assignment: GalleryTitleAssignment) {
    const query = assignment.characterQuery.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryTitleAssignmentCharacterAvailableFilterCache;
    if (cached?.source === assignment.availableCharacters && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.availableCharacters.filter((option) => option.searchText.includes(query))
      : assignment.availableCharacters;
    galleryTitleAssignmentCharacterAvailableFilterCache = { source: assignment.availableCharacters, query, result };
    return result;
  }

  function selectGalleryTitleAssignmentCharacter(characterId: number) {
    if (!galleryTitleAssignment || galleryTitleAssignment.isSaving) {
      return;
    }
    galleryTitleAssignment = { ...galleryTitleAssignment, selectedCharacterId: characterId };
  }

  function toggleGalleryAssignedTitleSelection(titleId: number) {
    if (!galleryTitleAssignment || galleryTitleAssignment.isSaving) {
      return;
    }

    const selected = galleryTitleAssignment.selectedAssignedTitleIds.includes(titleId)
      ? galleryTitleAssignment.selectedAssignedTitleIds.filter((id) => id !== titleId)
      : [...galleryTitleAssignment.selectedAssignedTitleIds, titleId];
    galleryTitleAssignment = { ...galleryTitleAssignment, selectedAssignedTitleIds: selected };
  }

  function removeSelectedGalleryTitles() {
    if (!galleryTitleAssignment || galleryTitleAssignment.selectedAssignedTitleIds.length === 0 || galleryTitleAssignment.isSaving) {
      return;
    }

    galleryTitleAssignment = { ...galleryTitleAssignment, isSaving: true };
    postHostMessage({
      type: 'gallery.titleAssignment.remove',
      paths: galleryTitleAssignment.works.map((work) => work.path),
      titleIds: galleryTitleAssignment.selectedAssignedTitleIds
    });
  }

  function openGalleryTitleAssignmentNewTitle() {
    if (!galleryTitleAssignment) {
      return;
    }

    galleryTitleAssignmentReturnPending = true;
    pendingGalleryTitleAssignmentCategory = galleryTitleAssignment.category;
    createFilterEditorDefinition('title');
    activateView('filters');
  }

  function returnToGalleryTitleAssignment() {
    if (!galleryTitleAssignment || !galleryTitleAssignmentReturnPending) {
      return;
    }

    galleryTitleAssignmentReturnPending = false;
    pendingGalleryTitleAssignmentCategory = '';
    activeView = 'library';
    persistNavigationState();
    requestGalleryTitleAssignmentOptions(galleryTitleAssignment);
  }

  function applyGalleryTitleAssignment() {
    if (!galleryTitleAssignment || galleryTitleAssignment.selectedTitleId === null || galleryTitleAssignment.isSaving) {
      return;
    }

    galleryTitleAssignment = { ...galleryTitleAssignment, isSaving: true };
    postHostMessage({
      type: 'gallery.titleAssignment.apply',
      paths: galleryTitleAssignment.works.map((work) => work.path),
      titleId: galleryTitleAssignment.selectedTitleId,
      characterId: galleryTitleAssignment.selectedCharacterId,
      removeTitleIds: galleryTitleAssignment.selectedAssignedTitleIds
    });
  }

  function closeGalleryTitleAssignment() {
    if (galleryTitleAssignment?.isSaving) {
      return;
    }

    galleryTitleAssignment = null;
    galleryTitleAssignmentReturnPending = false;
    pendingGalleryTitleAssignmentCategory = '';
    pendingGalleryTitleAssignmentSelectedTitleId = null;
    pendingGalleryTitleAssignmentSelectedCharacterId = null;
  }

  function openGalleryCharacterAssignment(sourceWork: GalleryWork | null = galleryContextTargetWork ?? galleryContextMenu?.work ?? null) {
    try {
      closeGalleryContextMenu();
      let works = galleryWorks.filter((work) => selectedGalleryWorkIds.has(work.id));
      if (works.length === 0 && sourceWork) {
        works = [sourceWork];
      }

      if (works.length === 0) {
        showExplorerToast('Character属性を登録する作品を選択してください。', 'error');
        return;
      }

      const targetWork = sourceWork ?? works[0];
      if (!targetWork) {
        showExplorerToast('Character属性を登録する作品を特定できませんでした。', 'error');
        return;
      }

      const creator = (targetWork.creator ?? '').trim();
      const title = (targetWork.title ?? '').trim();
      const assignment: GalleryCharacterAssignment = {
        requestId: '',
        sourcePath: targetWork.path,
        works,
        category: targetWork.category,
        creators: creator ? [creator] : [],
        titles: title ? [title] : [],
        creatorTitleCharacters: [],
        availableCharacters: [],
        commonAssignedCharacters: [],
        creatorQuery: '',
        query: '',
        selectedCharacterId: null,
        selectedAssignedCharacterIds: [],
        assignedCharactersPanelOpen: false,
        isLoading: true,
        isSaving: false
      };
      flushSync(() => {
        galleryTagAssignment = null;
        galleryTagAssignmentReturnPending = false;
        galleryTitleAssignment = null;
        galleryCharacterAssignment = assignment;
      });
      requestGalleryCharacterAssignmentOptions(assignment);
    }
    catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      showExplorerToast(`Character属性画面を開けませんでした: ${message}`, 'error');
    }
  }

  function requestGalleryCharacterAssignmentOptions(assignment = galleryCharacterAssignment) {
    if (!assignment) {
      return;
    }

    const requestId = `gallery-character-assignment-${nextGalleryCharacterAssignmentRequestId++}`;
    galleryCharacterAssignment = {
      ...assignment,
      requestId,
      isLoading: true,
      isSaving: false
    };
    postHostMessage({
      type: 'gallery.characterAssignment.options.request',
      requestId,
      category: assignment.category,
      creators: assignment.creators,
      sourcePath: assignment.sourcePath,
      paths: assignment.works.map((work) => work.path)
    });
  }

  function getGalleryCharacterAssignmentCreatorTitleCharacters(assignment: GalleryCharacterAssignment) {
    const query = assignment.creatorQuery.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryCharacterAssignmentCreatorFilterCache;
    if (cached?.source === assignment.creatorTitleCharacters && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.creatorTitleCharacters.filter((option) => option.searchText.includes(query))
      : assignment.creatorTitleCharacters;
    galleryCharacterAssignmentCreatorFilterCache = { source: assignment.creatorTitleCharacters, query, result };
    return result;
  }

  function getGalleryCharacterAssignmentAvailableCharacters(assignment: GalleryCharacterAssignment) {
    const query = assignment.query.trim().toLocaleLowerCase('ja-JP');
    const cached = galleryCharacterAssignmentAvailableFilterCache;
    if (cached?.source === assignment.availableCharacters && cached.query === query) {
      return cached.result;
    }

    const result = query
      ? assignment.availableCharacters.filter((option) => option.searchText.includes(query))
      : assignment.availableCharacters;
    galleryCharacterAssignmentAvailableFilterCache = { source: assignment.availableCharacters, query, result };
    return result;
  }

  function getGalleryCharacterAssignmentScopeLabel(assignment: GalleryCharacterAssignment) {
    const creator = assignment.creators[0] || 'Creator未設定';
    const titles = assignment.titles.length > 0 ? assignment.titles.join('・') : 'Title未設定';
    return `${creator} / ${titles}`;
  }

  function selectGalleryCharacterAssignment(characterId: number) {
    if (!galleryCharacterAssignment || galleryCharacterAssignment.isSaving) {
      return;
    }
    galleryCharacterAssignment = { ...galleryCharacterAssignment, selectedCharacterId: characterId };
  }

  function toggleGalleryAssignedCharacterSelection(characterId: number) {
    if (!galleryCharacterAssignment || galleryCharacterAssignment.isSaving) {
      return;
    }

    const selected = galleryCharacterAssignment.selectedAssignedCharacterIds.includes(characterId)
      ? galleryCharacterAssignment.selectedAssignedCharacterIds.filter((id) => id !== characterId)
      : [...galleryCharacterAssignment.selectedAssignedCharacterIds, characterId];
    galleryCharacterAssignment = { ...galleryCharacterAssignment, selectedAssignedCharacterIds: selected };
  }

  function removeSelectedGalleryCharacters() {
    if (!galleryCharacterAssignment || galleryCharacterAssignment.selectedAssignedCharacterIds.length === 0 || galleryCharacterAssignment.isSaving) {
      return;
    }

    galleryCharacterAssignment = { ...galleryCharacterAssignment, isSaving: true };
    postHostMessage({
      type: 'gallery.characterAssignment.remove',
      paths: galleryCharacterAssignment.works.map((work) => work.path),
      characterIds: galleryCharacterAssignment.selectedAssignedCharacterIds
    });
  }

  function applyGalleryCharacterAssignment() {
    if (!galleryCharacterAssignment || galleryCharacterAssignment.selectedCharacterId === null || galleryCharacterAssignment.isSaving) {
      return;
    }

    galleryCharacterAssignment = { ...galleryCharacterAssignment, isSaving: true };
    postHostMessage({
      type: 'gallery.characterAssignment.apply',
      paths: galleryCharacterAssignment.works.map((work) => work.path),
      characterId: galleryCharacterAssignment.selectedCharacterId,
      removeCharacterIds: galleryCharacterAssignment.selectedAssignedCharacterIds
    });
  }

  function openGalleryCharacterAssignmentNewCharacter() {
    if (!galleryCharacterAssignment) {
      return;
    }

    galleryCharacterAssignmentReturnPending = true;
    pendingGalleryCharacterAssignmentCategory = galleryCharacterAssignment.category;
    createFilterEditorDefinition('character');
    if (galleryCharacterAssignment.titles.length === 1) {
      const parent = filterEditorTitles.find(definition =>
        definition.canonicalName.localeCompare(galleryCharacterAssignment?.titles[0] ?? '', 'ja-JP', { sensitivity: 'base' }) === 0);
      if (parent) {
        filterEditorParentTitleId = parent.id;
        filterEditorParentTitle = parent.canonicalName;
        filterEditorCategoryName = parent.categoryName === '未分類' ? '' : parent.categoryName;
      }
    }
    activateView('filters');
  }

  function returnToGalleryCharacterAssignment() {
    if (!galleryCharacterAssignment || !galleryCharacterAssignmentReturnPending) {
      return;
    }

    galleryCharacterAssignmentReturnPending = false;
    pendingGalleryCharacterAssignmentCategory = '';
    activeView = 'library';
    persistNavigationState();
    requestGalleryCharacterAssignmentOptions(galleryCharacterAssignment);
  }

  function closeGalleryCharacterAssignment() {
    if (galleryCharacterAssignment?.isSaving) {
      return;
    }

    galleryCharacterAssignment = null;
    galleryCharacterAssignmentReturnPending = false;
    pendingGalleryCharacterAssignmentCategory = '';
    pendingGalleryCharacterAssignmentSelectedId = null;
  }

  function setGalleryCreatorFilter(creator: string, event: MouseEvent) {
    const wasHiddenWhenCollapsed = isHiddenWhenCollapsed(event);
    const nextCreators = toggleGalleryFilter(galleryCreatorFilters, creator, event);
    galleryCreatorFilters = nextCreators;
    galleryPromotedCreators = updateGalleryPromotedFilters(
      galleryPromotedCreators,
      nextCreators,
      creator,
      wasHiddenWhenCollapsed && !galleryCollapsePins.creator);
    if (shouldAutoCollapseGalleryFilter(galleryCreatorFiltersExpanded, galleryCollapsePins.creator, nextCreators, creator, wasHiddenWhenCollapsed)) {
      galleryCreatorFiltersExpanded = false;
    }
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function setGalleryTitleFilter(title: string, event: MouseEvent) {
    const wasHiddenWhenCollapsed = isHiddenWhenCollapsed(event);
    const nextTitles = toggleGalleryFilter(galleryTitleFilters, title, event);
    galleryTitleFilters = nextTitles;
    galleryPromotedTitles = updateGalleryPromotedFilters(
      galleryPromotedTitles,
      nextTitles,
      title,
      wasHiddenWhenCollapsed && !galleryCollapsePins.title);
    if (shouldAutoCollapseGalleryFilter(galleryTitleFiltersExpanded, galleryCollapsePins.title, nextTitles, title, wasHiddenWhenCollapsed)) {
      galleryTitleFiltersExpanded = false;
    }
    galleryCharacterFilters = [];
    galleryCharacterFiltersExpanded = false;
    galleryPromotedCharacters = [];
    if (!isGalleryFilterEnabled('character') || nextTitles.length !== 1) {
      galleryCharacters = [];
    }
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function loadGalleryPins(section: string) {
    galleryPinnedCreators = readGalleryPins(`zpiPinnedCreators:${section}`);
    galleryPinnedTitles = readGalleryPins(`zpiPinnedTitles:${section}`);
  }

  function readGalleryPins(key: string) {
    try {
      const value = JSON.parse(window.localStorage.getItem(key) ?? '[]');
      return Array.isArray(value)
        ? [...new Set(value.filter((item): item is string => typeof item === 'string'))]
        : [];
    }
    catch {
      return [];
    }
  }

  function saveGalleryPins() {
    try {
      window.localStorage.setItem(`zpiPinnedCreators:${gallerySection}`, JSON.stringify(galleryPinnedCreators));
      window.localStorage.setItem(`zpiPinnedTitles:${gallerySection}`, JSON.stringify(galleryPinnedTitles));
    }
    catch {
      // Pin state remains available for the current application session.
    }
  }

  function toggleGalleryFilterPin(event: MouseEvent, kind: 'creator' | 'title', value: string) {
    event.preventDefault();
    event.stopPropagation();
    const pins = kind === 'creator' ? galleryPinnedCreators : galleryPinnedTitles;
    const wasPinned = pins.includes(value);
    let removedSelectedFilter = false;
    if (wasPinned) {
      const nextPins = pins.filter((pin) => pin !== value);
      if (kind === 'creator') {
        removedSelectedFilter = galleryCreatorFilters.includes(value);
        galleryCreatorFilters = galleryCreatorFilters.filter((filter) => filter !== value);
        galleryPinnedCreators = nextPins;
      }
      else {
        removedSelectedFilter = galleryTitleFilters.includes(value);
        galleryTitleFilters = galleryTitleFilters.filter((filter) => filter !== value);
        if (removedSelectedFilter) {
          galleryCharacterFilters = [];
          galleryCharacterFiltersExpanded = false;
        }
        galleryPinnedTitles = nextPins;
      }
    }
    else {
      if (kind === 'creator') {
        galleryPinnedCreators = [value, ...pins.filter((pin) => pin !== value)];
        galleryCreatorFiltersExpanded = false;
      }
      else {
        galleryPinnedTitles = [value, ...pins.filter((pin) => pin !== value)];
        galleryTitleFiltersExpanded = false;
      }
    }

    saveGalleryPins();
    if (removedSelectedFilter) loadGalleryWorks();
  }

  function setGalleryCharacterFilter(character: string, event: MouseEvent) {
    const wasHiddenWhenCollapsed = isHiddenWhenCollapsed(event);
    const nextCharacters = toggleGalleryFilter(galleryCharacterFilters, character, event);
    galleryCharacterFilters = nextCharacters;
    galleryPromotedCharacters = updateGalleryPromotedFilters(
      galleryPromotedCharacters,
      nextCharacters,
      character,
      wasHiddenWhenCollapsed && !galleryCollapsePins.character);
    if (shouldAutoCollapseGalleryFilter(galleryCharacterFiltersExpanded, galleryCollapsePins.character, nextCharacters, character, wasHiddenWhenCollapsed)) {
      galleryCharacterFiltersExpanded = false;
    }
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function clearGalleryFilters() {
    galleryRatingFilters = [];
    galleryTagFilters = [];
    galleryCreatorFilters = [];
    galleryTitleFilters = [];
    galleryCharacterFilters = [];
    galleryQuery = '';
    galleryCreatorFiltersExpanded = false;
    galleryTitleFiltersExpanded = false;
    galleryCharacterFiltersExpanded = false;
    galleryTagFiltersExpanded = false;
    galleryPromotedCreators = [];
    galleryPromotedTitles = [];
    galleryPromotedCharacters = [];
    galleryPromotedTags = [];
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function setGalleryThumbnailSort(key: GalleryThumbnailSortKey) {
    const index = galleryThumbnailSorts.findIndex((criterion) => criterion.key === key);
    if (index < 0) {
      galleryThumbnailSorts = [
        ...galleryThumbnailSorts,
        { key, direction: key === 'path' ? 'asc' : 'desc' }
      ];
    }
    else if (key !== 'rating') {
      galleryThumbnailSorts = galleryThumbnailSorts.map((criterion, criterionIndex) =>
        criterionIndex === index
          ? { ...criterion, direction: criterion.direction === 'desc' ? 'asc' : 'desc' }
          : criterion);
    }
    else {
      return;
    }

    persistNavigationState();
    loadGalleryWorks();
  }

  function getGalleryThumbnailSortPriority(
    sorts: GalleryThumbnailSortCriterion[],
    key: GalleryThumbnailSortKey
  ) {
    return sorts.findIndex((criterion) => criterion.key === key) + 1;
  }

  function getGalleryThumbnailSortDirection(
    sorts: GalleryThumbnailSortCriterion[],
    key: GalleryThumbnailSortKey
  ): GalleryThumbnailSortDirection {
    return sorts.find((criterion) => criterion.key === key)?.direction
      ?? (key === 'path' ? 'asc' : 'desc');
  }

  function removeGalleryThumbnailSort(event: MouseEvent, key: GalleryThumbnailSortKey) {
    event.preventDefault();
    event.stopPropagation();
    if (!galleryThumbnailSorts.some((criterion) => criterion.key === key)) {
      return;
    }

    galleryThumbnailSorts = galleryThumbnailSorts.filter((criterion) => criterion.key !== key);
    persistNavigationState();
    loadGalleryWorks();
  }

  function setGalleryFilterSort(key: GalleryFilterSortKey) {
    const index = galleryFilterSorts.findIndex((criterion) => criterion.key === key);
    if (index < 0) {
      galleryFilterSorts = [
        ...galleryFilterSorts,
        { key, direction: key === 'name' ? 'asc' : 'desc' }
      ];
    }
    else if (key !== 'rating') {
      galleryFilterSorts = galleryFilterSorts.map((criterion, criterionIndex) =>
        criterionIndex === index
          ? { ...criterion, direction: criterion.direction === 'desc' ? 'asc' : 'desc' }
          : criterion);
    }
    else {
      return;
    }

    persistNavigationState();
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function getGalleryFilterSortPriority(
    sorts: GalleryFilterSortCriterion[],
    key: GalleryFilterSortKey
  ) {
    return sorts.findIndex((criterion) => criterion.key === key) + 1;
  }

  function getGalleryFilterSortDirection(
    sorts: GalleryFilterSortCriterion[],
    key: GalleryFilterSortKey
  ): GalleryFilterSortDirection {
    return sorts.find((criterion) => criterion.key === key)?.direction
      ?? (key === 'name' ? 'asc' : 'desc');
  }

  function removeGalleryFilterSort(event: MouseEvent, key: GalleryFilterSortKey) {
    event.preventDefault();
    event.stopPropagation();
    if (!galleryFilterSorts.some((criterion) => criterion.key === key)) {
      return;
    }

    galleryFilterSorts = galleryFilterSorts.filter((criterion) => criterion.key !== key);
    persistNavigationState();
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function scheduleGalleryFilterVisibilityCheck() {
    requestAnimationFrame(() => {
      galleryCreatorHasHiddenFilters = hasCollapsedHiddenFilters(galleryCreatorFilterButtonsElement);
      galleryTitleHasHiddenFilters = hasCollapsedHiddenFilters(galleryTitleFilterButtonsElement);
      galleryCharacterHasHiddenFilters = hasCollapsedHiddenFilters(galleryCharacterFilterButtonsElement);
      galleryTagHasHiddenFilters = hasCollapsedHiddenFilters(galleryTagFilterButtonsElement);
    });
  }

  function scrollGalleryFilterRows(event: WheelEvent) {
    if (event.ctrlKey || event.deltaY === 0) return;
    const container = event.currentTarget as HTMLElement | null;
    if (!container || container.scrollHeight <= container.clientHeight + 1) return;

    const measuredOffsets = [...new Set(
      [...container.querySelectorAll<HTMLElement>(':scope > button')]
        .map(button => button.offsetTop))]
      .sort((left, right) => left - right);
    const firstOffset = measuredOffsets[0] ?? 0;
    const rowOffsets = measuredOffsets.map(offset => offset - firstOffset);
    if (rowOffsets.length < 2) return;

    const currentOffset = container.scrollTop;
    const direction = event.deltaY > 0 ? 1 : -1;
    const targetOffset = direction > 0
      ? rowOffsets.find(offset => offset > currentOffset + 1)
      : [...rowOffsets].reverse().find(offset => offset < currentOffset - 1);
    if (targetOffset === undefined) return;

    const boundedOffset = Math.min(targetOffset, container.scrollHeight - container.clientHeight);
    if (Math.abs(boundedOffset - currentOffset) < 1) return;
    event.preventDefault();
    container.scrollTo({ top: boundedOffset, behavior: 'auto' });
  }

  function hasCollapsedHiddenFilters(container: HTMLElement | null) {
    if (!container) {
      return false;
    }

    const buttons = [...container.querySelectorAll<HTMLElement>('button')];
    const firstButton = buttons[0];
    return Boolean(firstButton && buttons.some((button) => button.offsetTop > firstButton.offsetTop));
  }

  function getGalleryFilterOptions(
    options: GalleryFilterOption[],
    pinnedValues: string[] = [],
    promotedValues: string[] = []
  ) {
    if (pinnedValues.length === 0 && promotedValues.length === 0) return options;

    return [...options].sort((left, right) => {
      const leftPinned = pinnedValues.includes(left.value);
      const rightPinned = pinnedValues.includes(right.value);
      if (leftPinned !== rightPinned) return leftPinned ? -1 : 1;
      const leftPromoted = promotedValues.includes(left.value);
      const rightPromoted = promotedValues.includes(right.value);
      if (leftPromoted !== rightPromoted) return leftPromoted ? -1 : 1;
      return 0;
    });
  }

  function isHiddenWhenCollapsed(event: MouseEvent) {
    const button = event.currentTarget as HTMLElement | null;
    const container = button?.parentElement;
    const firstButton = container?.querySelector<HTMLElement>('button');
    return Boolean(button && firstButton && button.offsetTop > firstButton.offsetTop);
  }

  function shouldAutoCollapseGalleryFilter<T>(
    isExpanded: boolean,
    isCollapsePinned: boolean,
    selectedValues: T[],
    value: T,
    wasHiddenWhenCollapsed: boolean
  ) {
    return isExpanded && !isCollapsePinned && wasHiddenWhenCollapsed && selectedValues.includes(value);
  }

  function toggleGalleryFilterCollapsePin(event: MouseEvent, kind: GalleryExpandableFilterKind) {
    event.preventDefault();
    event.stopPropagation();
    galleryCollapsePins = {
      ...galleryCollapsePins,
      [kind]: !galleryCollapsePins[kind]
    };
  }

  function toggleGalleryFilterExpansion(kind: GalleryExpandableFilterKind) {
    const promoteSelected = (selected: string[], promoted: string[]) => [
      ...selected,
      ...promoted.filter(value => !selected.includes(value))
    ];
    if (kind === 'creator') {
      if (galleryCreatorFiltersExpanded) {
        galleryPromotedCreators = promoteSelected(galleryCreatorFilters, galleryPromotedCreators);
      }
      galleryCreatorFiltersExpanded = !galleryCreatorFiltersExpanded;
      return;
    }
    if (kind === 'title') {
      if (galleryTitleFiltersExpanded) {
        galleryPromotedTitles = promoteSelected(galleryTitleFilters, galleryPromotedTitles);
      }
      galleryTitleFiltersExpanded = !galleryTitleFiltersExpanded;
      return;
    }
    if (kind === 'character') {
      if (galleryCharacterFiltersExpanded) {
        galleryPromotedCharacters = promoteSelected(galleryCharacterFilters, galleryPromotedCharacters);
      }
      galleryCharacterFiltersExpanded = !galleryCharacterFiltersExpanded;
      return;
    }
    if (galleryTagFiltersExpanded) {
      galleryPromotedTags = promoteSelected(galleryTagFilters, galleryPromotedTags);
    }
    galleryTagFiltersExpanded = !galleryTagFiltersExpanded;
  }

  function updateGalleryPromotedFilters(
    promotedValues: string[],
    selectedValues: string[],
    value: string,
    shouldPromote: boolean
  ) {
    const nextSelected = new Set(selectedValues);
    const retained = promotedValues.filter((item) => nextSelected.has(item));
    if (nextSelected.has(value) && shouldPromote) {
      return [value, ...retained.filter((item) => item !== value)];
    }

    return retained;
  }

  function requestGalleryThumbnail(work: GalleryWork) {
    queueGalleryThumbnailRequest(work.id, work.path, work.category);
  }

  function observeGalleryThumbnail(node: HTMLElement, work: GalleryWork) {
    return observeGalleryThumbnailSource(node, () => ({ id: work.id, path: work.path, category: work.category }));
  }

  function getGallerySectionLabel(section: string) {
    return gallerySections.find((item) => item.id === section)?.label ?? section;
  }

  function getGalleryDisplayName(work: GalleryWork) {
    return work.name
      .replace(/\{gid=[^{}]+\}/gi, '')
      .replace(/\{zpi[^{}]*\}/gi, '')
      .replace(/\.zip$/i, '')
      .replace(/\s{2,}/g, ' ')
      .trim();
  }

  function filterGalleryWorks(works: GalleryWork[], query: string) {
    const terms = query.trim().toLocaleLowerCase('ja-JP').split(/\s+/).filter(Boolean);
    if (terms.length === 0) return works;
    return works.filter((work) => {
      const searchableText = [
        getGalleryDisplayName(work),
        work.path,
        work.creator,
        work.title,
        work.character,
        ...work.tags
      ].join('\n').toLocaleLowerCase('ja-JP');
      return terms.every((term) => searchableText.includes(term));
    });
  }

  function formatGalleryRating(rating: number) {
    if (rating === 6) {
      return '★6+';
    }

    return rating > 0 ? '★'.repeat(rating) : 'NR';
  }

  function formatGalleryCardRating(rating: number) {
    return rating > 0 ? `⭐${rating}` : 'NR';
  }

  function formatGalleryDuration(seconds: number | null) {
    if (seconds === null || !Number.isFinite(seconds) || seconds < 0) {
      return '--:--';
    }

    const totalSeconds = Math.floor(seconds);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const remainingSeconds = totalSeconds % 60;
    const twoDigits = (value: number) => String(value).padStart(2, '0');
    return hours > 0
      ? `${hours}:${twoDigits(minutes)}:${twoDigits(remainingSeconds)}`
      : `${minutes}:${twoDigits(remainingSeconds)}`;
  }

  function toggleGalleryWorkSelection(event: MouseEvent, work: GalleryWork) {
    event.stopPropagation();
    closeGalleryContextMenu();
    const next = new Set(selectedGalleryWorkIds);
    if (next.has(work.id)) {
      next.delete(work.id);
    }
    else {
      next.add(work.id);
    }
    selectedGalleryWorkIds = next;
    gallerySelectionAnchorId = work.id;
  }

  function adjustGalleryWorkRating(event: MouseEvent, work: GalleryWork, delta: 1 | -1) {
    event.preventDefault();
    event.stopPropagation();
    closeGalleryContextMenu();
    if (pendingGalleryRatingIds.has(work.id)) {
      return;
    }

    pendingGalleryRatingIds = new Set([...pendingGalleryRatingIds, work.id]);
    pendingGalleryRatingDeltas = { ...pendingGalleryRatingDeltas, [work.id]: delta };
    galleryRatingEffects = { ...galleryRatingEffects, [work.id]: delta > 0 ? 'increase' : 'decrease' };
    window.setTimeout(() => {
      const { [work.id]: _, ...remaining } = galleryRatingEffects;
      galleryRatingEffects = remaining;
    }, 540);
    postHostMessage({ type: 'gallery.work.rating.adjust', id: work.id, path: work.path, delta });
  }

  function formatGalleryDate(value: string) {
    const match = value.match(/(\d{4})[-/](\d{2})[-/](\d{2})/);
    if (!match) {
      return value ? value.slice(0, 10) : '-';
    }

    const [, year, month, day] = match;
    return galleryCardColumns === 9
      ? `${year.slice(2)}-${month}-${day}`
      : `${year}-${month}-${day}`;
  }

  function getGalleryCardDate(work: GalleryWork) {
    return galleryThumbnailSorts.some(criterion => criterion.key === 'accessed')
      ? work.lastAccessTime
      : work.lastWriteTime || work.lastAccessTime;
  }

  function handleGalleryWorkCardClick(event: MouseEvent, work: GalleryWork) {
    event.stopPropagation();
    closeGalleryContextMenu();
    if (!event.ctrlKey && !event.metaKey && !event.shiftKey) {
      if (hasGallerySingleClickLaunchRule(work)) {
        const now = Date.now();
        const lastLaunch = gallerySingleClickLaunches[work.path] ?? 0;
        if (now - lastLaunch >= 700) {
          gallerySingleClickLaunches = { ...gallerySingleClickLaunches, [work.path]: now };
          openGalleryWork(work, 'single');
        }
      }
      else if (selectedGalleryWorkIds.size === 1 && selectedGalleryWorkIds.has(work.id)) {
        clearGalleryWorkSelection();
      }
      else {
        selectedGalleryWorkIds = new Set([work.id]);
        gallerySelectionAnchorId = work.id;
      }
      return;
    }

    event.preventDefault();
    const next = new Set(selectedGalleryWorkIds);
    if (event.shiftKey && gallerySelectionAnchorId) {
      const anchorIndex = galleryWorks.findIndex((item) => item.id === gallerySelectionAnchorId);
      const targetIndex = galleryWorks.findIndex((item) => item.id === work.id);
      if (anchorIndex >= 0 && targetIndex >= 0) {
        const [start, end] = anchorIndex <= targetIndex ? [anchorIndex, targetIndex] : [targetIndex, anchorIndex];
        selectedGalleryWorkIds = new Set(galleryWorks.slice(start, end + 1).map((item) => item.id));
        return;
      }
    }

    if (next.has(work.id)) {
      next.delete(work.id);
    }
    else {
      next.add(work.id);
    }
    selectedGalleryWorkIds = next;
    gallerySelectionAnchorId = work.id;
  }

  function handleGalleryWorkCardDoubleClick(event: MouseEvent, work: GalleryWork) {
    event.preventDefault();
    event.stopPropagation();
    closeGalleryContextMenu();
    if (!hasGallerySingleClickLaunchRule(work) && hasGalleryLaunchRule(work, 'double')) {
      openGalleryWork(work, 'double');
    }
  }

  function openGalleryWork(work: GalleryWork, activation: 'single' | 'double') {
    postHostMessage({ type: 'gallery.work.open', path: work.path, activation });
  }

  function clearGalleryWorkSelection() {
    selectedGalleryWorkIds = new Set();
    gallerySelectionAnchorId = '';
  }

  function openGalleryContextMenu(event: MouseEvent, work: GalleryWork) {
    event.preventDefault();
    event.stopPropagation();
    if (!selectedGalleryWorkIds.has(work.id)) {
      selectedGalleryWorkIds = new Set([work.id]);
      gallerySelectionAnchorId = work.id;
    }

    galleryContextMenu = {
      x: Math.max(8, Math.min(event.clientX, window.innerWidth - 394)),
      y: Math.max(8, Math.min(event.clientY, window.innerHeight - 230)),
      work
    };
    galleryContextTargetWork = work;
  }

  function closeGalleryContextMenu() {
    flushSync(() => {
      galleryContextMenu = null;
    });
    document.querySelector('.gallery-context-menu')?.remove();
    document.querySelector('.gallery-context-menu-backdrop')?.remove();
  }

  function requestGalleryReverseFilters(mode: GalleryReverseFilterMode) {
    const work = galleryContextTargetWork ?? galleryContextMenu?.work ?? null;
    if (!work || galleryReverseFilterRequest) {
      if (!work) showExplorerToast('逆引きする作品を特定できませんでした。', 'error');
      return;
    }

    closeGalleryContextMenu();
    const requestId = `gallery-reverse-filter-${nextGalleryReverseFilterRequestId++}`;
    galleryReverseFilterRequest = { requestId, mode, workName: work.name || work.path };
    postHostMessage({
      type: 'gallery.reverseFilters.get',
      requestId,
      category: work.category,
      path: work.path
    });
  }

  function applyGalleryReverseCreator() {
    const work = galleryContextTargetWork ?? galleryContextMenu?.work ?? null;
    if (!work?.creator?.trim()) {
      closeGalleryContextMenu();
      showExplorerToast('この作品にはCreatorが登録されていません。', 'error');
      return;
    }

    const creator = work.creator.trim();
    closeGalleryContextMenu();
    recordNavigationHistory();
    galleryCreatorFilters = [creator];
    galleryTitleFilters = [];
    galleryCharacterFilters = [];
    galleryTagFilters = [];
    galleryPromotedCreators = galleryCollapsePins.creator ? [] : [creator];
    galleryPromotedTitles = [];
    galleryPromotedCharacters = [];
    galleryPromotedTags = [];
    selectedGalleryWorkIds = new Set();
    loadGalleryWorks(false, galleryRatingFilters, true);
    showExplorerToast(`Creator「${creator}」で絞り込みました。`, 'success');
  }

  function applyGalleryReverseFilters(
    mode: GalleryReverseFilterMode,
    rawTitles: unknown[],
    rawCharacters: unknown[],
    workName: string)
  {
    recordNavigationHistory();
    const titles = [...new Set(rawTitles.map(value => String(value ?? '').trim()).filter(Boolean))];
    const characters = rawCharacters
      .map((value): GalleryReverseCharacterFilter | null => {
        if (!value || typeof value !== 'object') return null;
        const candidate = value as Partial<GalleryReverseCharacterFilter>;
        const filterValue = String(candidate.value ?? '').trim();
        if (!filterValue) return null;
        return {
          value: filterValue,
          label: String(candidate.label ?? '').trim(),
          title: String(candidate.title ?? '').trim()
        };
      })
      .filter((value): value is GalleryReverseCharacterFilter => value !== null);

    galleryCreatorFilters = [];
    galleryTitleFilters = titles;
    galleryCharacterFilters = mode === 'character' && titles.length === 1 && characters.length === 1
      ? [characters[0].value]
      : [];
    galleryTagFilters = [];
    galleryPromotedCreators = [];
    galleryPromotedTitles = galleryCollapsePins.title ? [] : [...titles];
    galleryPromotedCharacters = galleryCollapsePins.character ? [] : [...galleryCharacterFilters];
    galleryPromotedTags = [];

    loadGalleryWorks(false, galleryRatingFilters, true);
    if (titles.length === 0) {
      showExplorerToast(`「${workName}」にはTitle属性が登録されていません。属性フィルターを解除しました。`, 'error');
      return;
    }
    if (mode === 'character' && (titles.length !== 1 || characters.length !== 1)) {
      const reason = characters.length > 1
        ? 'Character属性が複数あるためCharacterは選択していません。'
        : titles.length > 1
          ? 'Title属性が複数あるためCharacterは選択していません。'
          : 'Character属性がないためTitleだけを選択しました。';
      showExplorerToast(`${titles.length}件のTitleで絞り込みました。${reason}`, 'success');
      return;
    }
    showExplorerToast(
      mode === 'character'
        ? `Title「${titles[0]}」とCharacter「${characters[0].label}」で絞り込みました。`
        : `${titles.length}件のTitleで絞り込みました。`,
      'success');
  }

  function requestGalleryDelete() {
    if (selectedGalleryWorkIds.size === 0) {
      return;
    }

    closeGalleryContextMenu();
    galleryDeleteInProgress = false;
    galleryDeleteConfirmation = true;
  }

  function closeGalleryDeleteConfirmation() {
    if (galleryDeleteInProgress) {
      return;
    }

    galleryDeleteConfirmation = false;
  }

  function deleteGallerySelection() {
    if (galleryDeleteInProgress) {
      return;
    }

    const paths = galleryWorks
      .filter((work) => selectedGalleryWorkIds.has(work.id))
      .map((work) => work.path);
    if (paths.length === 0) {
      galleryDeleteConfirmation = false;
      return;
    }

    galleryDeleteInProgress = true;
    postHostMessage({ type: 'gallery.works.delete', paths });
  }

  function clearGallerySelectionOnClick(node: HTMLElement) {
    const handlePointerDown = (event: PointerEvent) => {
      if (
        activeView !== 'library'
        || event.button !== 0
        || !galleryContextMenu
      ) {
        return;
      }

      closeGalleryContextMenu();
    };

    const handleClick = (event: MouseEvent) => {
      if (activeView !== 'library') {
        return;
      }

      closeGalleryContextMenu();
      const target = event.target as HTMLElement | null;
      if (target?.closest('.gallery-work-card, .gallery-filters, .gallery-load-more')) {
        return;
      }

      clearGalleryWorkSelection();
    };

    node.addEventListener('pointerdown', handlePointerDown, true);
    node.addEventListener('click', handleClick);
    return {
      destroy() {
        node.removeEventListener('pointerdown', handlePointerDown, true);
        node.removeEventListener('click', handleClick);
      }
    };
  }

  function openItem(item: GalleryItem) {
    postHostMessage({ type: 'item.open', id: item.id });
  }

  function onTileKeydown(event: KeyboardEvent, item: GalleryItem) {
    if (event.key === 'Enter') {
      openItem(item);
    }
  }

  function isBookmarkableView(view: ActiveView): view is BookmarkableView {
    return view === 'library' || view === 'explorer' || view === 'creators' || view === 'creatorTracking';
  }

  function getBookmarkViewLabel(view: BookmarkableView) {
    if (view === 'library') return 'Gallery';
    if (view === 'explorer') return 'Explorer';
    if (view === 'creators') return 'Creators';
    return 'Creator Tracking';
  }

  function isStickyNoteView(view: ActiveView): view is StickyNoteView {
    return isBookmarkableView(view) || view === 'userMetrics';
  }

  function getGalleryWorkStickyNoteContextKey(section: string, workId: string) {
    return normalizeStickyNoteContextKey('library', `${section}${galleryWorkStickyContextSeparator}${workId}`);
  }

  function isGalleryWorkStickyNoteContext(contextKey: string, section: string) {
    const normalizedSection = normalizeStickyNoteContextKey('library', section);
    return contextKey.startsWith(`${normalizedSection}${galleryWorkStickyContextSeparator}`);
  }

  function normalizeStickyNote(note: Partial<StickyNoteItem>): StickyNoteItem | null {
    const id = Number(note.id ?? 0);
    const viewType = note.viewType;
    if (!Number.isFinite(id) || id <= 0 || !viewType || !isStickyNoteView(viewType as ActiveView)) {
      return null;
    }

    const width = Math.min(1200, Math.max(125, Number(note.width ?? 250)));
    const height = Math.min(900, Math.max(100, Number(note.height ?? 200)));
    const contextKey = normalizeStickyNoteContextKey(viewType as StickyNoteView, String(note.contextKey ?? ''));
    if (!contextKey) return null;
    return {
      id,
      viewType: viewType as StickyNoteView,
      contextKey,
      contextLabel: String(note.contextLabel ?? contextKey).trim() || contextKey,
      content: String(note.content ?? ''),
      x: Math.max(0, Number(note.x ?? 24)),
      y: Math.max(stickyNoteTitlebarHeight, Number(note.y ?? 88)),
      width,
      height,
      colorKey: normalizeStickyNoteColorKey(note.colorKey),
      contentMode: note.contentMode === 'markdown' ? 'markdown' : 'plain',
      createdAt: String(note.createdAt ?? ''),
      updatedAt: String(note.updatedAt ?? '')
    };
  }

  function normalizeStickyNoteColorKey(colorKey: unknown): StickyNoteColorKey {
    const normalized = String(colorKey ?? '').toLocaleLowerCase('en-US');
    return stickyNotePalette.some((color) => color.key === normalized)
      ? normalized as StickyNoteColorKey
      : 'amber';
  }

  function normalizeStickyNoteContextKey(viewType: StickyNoteView, contextKey: string) {
    let normalized = contextKey.trim();
    if (viewType === 'explorer') {
      normalized = normalized.replace(/\//g, '\\').replace(/\\+$/, '');
    }
    return normalized.toLocaleLowerCase('en-US');
  }

  function getStickyNoteContextKey(viewType: ActiveView): string {
    if (viewType === 'library') return normalizeStickyNoteContextKey('library', gallerySection);
    if (viewType === 'creators') return normalizeStickyNoteContextKey('creators', galleryCreatorSummarySection);
    if (viewType === 'explorer') {
      const path = explorerSplit && splitFocusedPane === 'right' ? explorerSplit.rightPath : explorerPath;
      return normalizeStickyNoteContextKey('explorer', path ?? '');
    }
    if (viewType === 'creatorTracking') {
      const creator = creatorTrackingTabs.find((tab) => tab.id === activeCreatorTrackingTabId)?.creator
        ?? creatorTracking?.creator
        ?? '';
      return normalizeStickyNoteContextKey('creatorTracking', creator);
    }
    if (viewType === 'userMetrics') return normalizeStickyNoteContextKey('userMetrics', userMetricsCategory);
    return '';
  }

  function getStickyNoteContextLabel(viewType: StickyNoteView) {
    if (viewType === 'library') return getGallerySectionLabel(gallerySection);
    if (viewType === 'creators') return getGallerySectionLabel(galleryCreatorSummarySection);
    if (viewType === 'userMetrics') return getGallerySectionLabel(userMetricsCategory);
    if (viewType === 'explorer') {
      const path = explorerSplit && splitFocusedPane === 'right' ? explorerSplit.rightPath : explorerPath;
      return getExplorerTabLabel(path) || path;
    }
    return creatorTrackingTabs.find((tab) => tab.id === activeCreatorTrackingTabId)?.creator
      ?? creatorTracking?.creator
      ?? '';
  }

  function getStickyNotePaletteEntry(colorKey: StickyNoteColorKey) {
    return stickyNotePalette.find((color) => color.key === colorKey) ?? stickyNotePalette[0];
  }

  function createStickyNoteFromToolbar(event: MouseEvent) {
    if (!isStickyNoteView(activeView)) return;
    let contextKey = getStickyNoteContextKey(activeView);
    let contextLabel = getStickyNoteContextLabel(activeView);
    if (activeView === 'library' && selectedGalleryWorkIds.size === 1) {
      const selectedId = [...selectedGalleryWorkIds][0];
      const work = visibleGalleryWorks.find(candidate => candidate.id === selectedId);
      if (work) {
        const workName = work.name || work.path.split(/[\\/]/).at(-1) || work.id;
        contextKey = getGalleryWorkStickyNoteContextKey(gallerySection, work.id);
        contextLabel = `${getGallerySectionLabel(gallerySection)} > ${workName}`;
      }
    }
    if (!contextKey) {
      showExplorerToast('付箋を関連付ける対象を特定できませんでした。', 'error');
      return;
    }
    const trigger = event.currentTarget as HTMLElement;
    const bounds = trigger.getBoundingClientRect();
    const width = Math.max(125, Math.min(250, window.innerWidth - 24));
    const height = Math.max(100, Math.min(200, window.innerHeight - 24));
    const centeredX = bounds.left + (bounds.width - width) / 2;
    const x = Math.max(12, Math.min(centeredX, window.innerWidth - width - 12));
    const y = Math.max(stickyNoteTitlebarHeight, Math.min(bounds.bottom + 8, window.innerHeight - height - 12));
    postHostMessage({
      type: 'stickyNotes.create',
      viewType: activeView,
      contextKey,
      contextLabel,
      colorKey: 'amber',
      contentMode: 'plain',
      x,
      y,
      width,
      height
    });
  }

  function bringStickyNoteToFront(id: number) {
    const note = stickyNotes.find((candidate) => candidate.id === id);
    if (!note || stickyNotes.at(-1)?.id === id) return;
    stickyNotes = [...stickyNotes.filter((candidate) => candidate.id !== id), note];
  }

  function updateStickyNote(id: number, patch: Partial<StickyNoteItem>, saveDelay = 350) {
    stickyNotes = stickyNotes.map((note) => note.id === id ? { ...note, ...patch } : note);
    queueStickyNoteSave(id, saveDelay);
  }

  function updateStickyNoteContent(id: number, content: string) {
    updateStickyNote(id, { content }, 350);
  }

  function toggleStickyNotePalette(id: number) {
    stickyNotePaletteOpenId = stickyNotePaletteOpenId === id ? null : id;
  }

  function setStickyNoteColor(id: number, colorKey: StickyNoteColorKey) {
    const note = stickyNotes.find((candidate) => candidate.id === id);
    if (!note) return;
    stickyNotePaletteOpenId = null;
    updateStickyNote(id, { colorKey }, 0);
  }

  function toggleStickyNoteContentMode(id: number) {
    const note = stickyNotes.find((candidate) => candidate.id === id);
    if (!note) return;
    const contentMode = note.contentMode === 'plain' ? 'markdown' : 'plain';
    setMarkdownNoteEditing(id, false);
    updateStickyNote(id, { contentMode }, 0);
  }

  function setMarkdownNoteEditing(id: number, editing: boolean) {
    const next = new Set(markdownEditingNoteIds);
    if (editing) next.add(id);
    else next.delete(id);
    markdownEditingNoteIds = next;
    if (editing) {
      requestAnimationFrame(() => {
        document.querySelector<HTMLTextAreaElement>(`[data-sticky-note-id="${id}"] textarea, [data-board-note-id="${id}"] textarea`)?.focus();
      });
    }
  }

  function escapeStickyNoteHtml(value: string) {
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function renderStickyNoteMarkdownInline(value: string) {
    let html = escapeStickyNoteHtml(value);
    html = html.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2">$1</a>');
    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');
    html = html.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/~~([^~]+)~~/g, '<del>$1</del>');
    html = html.replace(/(^|[^*])\*([^*]+)\*/g, '$1<em>$2</em>');
    return html;
  }

  function renderStickyNoteMarkdown(value: string) {
    const lines = value.replace(/\r\n?/g, '\n').split('\n');
    const output: string[] = [];
    let listOpen = false;
    const closeList = () => {
      if (listOpen) output.push('</ul>');
      listOpen = false;
    };
    for (const line of lines) {
      const list = /^\s*[-*]\s+(.+)$/.exec(line);
      if (list) {
        if (!listOpen) output.push('<ul>');
        listOpen = true;
        output.push(`<li>${renderStickyNoteMarkdownInline(list[1])}</li>`);
        continue;
      }
      closeList();
      const heading = /^(#{1,3})\s+(.+)$/.exec(line);
      if (heading) {
        const level = heading[1].length + 2;
        output.push(`<h${level}>${renderStickyNoteMarkdownInline(heading[2])}</h${level}>`);
      }
      else if (/^>\s?/.test(line)) {
        output.push(`<blockquote>${renderStickyNoteMarkdownInline(line.replace(/^>\s?/, ''))}</blockquote>`);
      }
      else if (line.trim()) {
        output.push(`<p>${renderStickyNoteMarkdownInline(line)}</p>`);
      }
      else {
        output.push('<br>');
      }
    }
    closeList();
    return output.join('');
  }

  function handleStickyNoteMarkdownClick(event: MouseEvent, noteId: number) {
    const link = (event.target as Element | null)?.closest<HTMLAnchorElement>('a[href]');
    if (link) {
      event.preventDefault();
      postHostMessage({ type: 'creator.tracking.url.open', url: link.href });
      return;
    }
    setMarkdownNoteEditing(noteId, true);
  }

  function deleteStickyNote(id: number) {
    const timer = stickyNoteSaveTimers.get(id);
    if (timer) clearTimeout(timer);
    stickyNoteSaveTimers.delete(id);
    if (stickyNoteInteraction?.id === id) stickyNoteInteraction = null;
    if (stickyNotePaletteOpenId === id) stickyNotePaletteOpenId = null;
    stickyNotes = stickyNotes.filter((note) => note.id !== id);
    postHostMessage({ type: 'stickyNotes.delete', id });
  }

  function queueStickyNoteSave(id: number, delay = 350) {
    const current = stickyNoteSaveTimers.get(id);
    if (current) clearTimeout(current);
    if (delay <= 0) {
      stickyNoteSaveTimers.delete(id);
      saveStickyNoteNow(id);
      return;
    }
    stickyNoteSaveTimers.set(id, setTimeout(() => {
      stickyNoteSaveTimers.delete(id);
      saveStickyNoteNow(id);
    }, delay));
  }

  function saveStickyNoteNow(id: number) {
    const note = stickyNotes.find((candidate) => candidate.id === id);
    if (!note) return;
    postHostMessage({
      type: 'stickyNotes.save',
      id: note.id,
      viewType: note.viewType,
      contextKey: note.contextKey,
      contextLabel: note.contextLabel,
      content: note.content,
      x: note.x,
      y: note.y,
      width: note.width,
      height: note.height,
      colorKey: note.colorKey,
      contentMode: note.contentMode
    });
  }

  function flushStickyNoteSaves() {
    const ids = [...stickyNoteSaveTimers.keys()];
    stickyNoteSaveTimers.forEach((timer) => clearTimeout(timer));
    stickyNoteSaveTimers.clear();
    ids.forEach(saveStickyNoteNow);
    const boardIds = [...stickyNoteBoardSaveTimers.keys()];
    stickyNoteBoardSaveTimers.forEach((timer) => clearTimeout(timer));
    stickyNoteBoardSaveTimers.clear();
    boardIds.forEach(saveStickyNoteBoardItemNow);
  }

  function beginStickyNoteMove(event: PointerEvent, note: StickyNoteItem) {
    if (event.button !== 0 || (event.target as Element | null)?.closest('button')) return;
    event.preventDefault();
    event.stopPropagation();
    bringStickyNoteToFront(note.id);
    stickyNoteInteraction = {
      id: note.id,
      mode: 'move',
      pointerX: event.clientX,
      pointerY: event.clientY,
      x: note.x,
      y: note.y,
      width: note.width,
      height: note.height
    };
  }

  function beginStickyNoteResize(event: PointerEvent, note: StickyNoteItem, edge: StickyNoteResizeEdge) {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    bringStickyNoteToFront(note.id);
    stickyNoteInteraction = {
      id: note.id,
      mode: 'resize',
      edge,
      pointerX: event.clientX,
      pointerY: event.clientY,
      x: note.x,
      y: note.y,
      width: note.width,
      height: note.height
    };
  }

  function updateStickyNoteInteraction(event: PointerEvent) {
    const interaction = stickyNoteInteraction;
    if (!interaction) return;
    event.preventDefault();
    const deltaX = event.clientX - interaction.pointerX;
    const deltaY = event.clientY - interaction.pointerY;
    let x = interaction.x;
    let y = interaction.y;
    let width = interaction.width;
    let height = interaction.height;

    if (interaction.mode === 'move') {
      x = Math.max(0, Math.min(interaction.x + deltaX, window.innerWidth - interaction.width));
      y = Math.max(
        stickyNoteTitlebarHeight,
        Math.min(interaction.y + deltaY, Math.max(stickyNoteTitlebarHeight, window.innerHeight - interaction.height)));
    }
    else {
      const edge = interaction.edge ?? 'se';
      if (edge.includes('e')) {
        width = Math.min(1200, Math.max(125, interaction.width + deltaX));
        width = Math.min(width, Math.max(125, window.innerWidth - interaction.x));
      }
      if (edge.includes('s')) {
        height = Math.min(900, Math.max(100, interaction.height + deltaY));
        height = Math.min(height, Math.max(100, window.innerHeight - interaction.y));
      }
      if (edge.includes('w')) {
        width = Math.min(1200, Math.max(125, interaction.width - deltaX));
        x = Math.max(0, interaction.x + interaction.width - width);
        width = interaction.x + interaction.width - x;
      }
      if (edge.includes('n')) {
        height = Math.min(900, Math.max(100, interaction.height - deltaY));
        y = Math.max(stickyNoteTitlebarHeight, interaction.y + interaction.height - height);
        height = interaction.y + interaction.height - y;
      }
    }

    stickyNotes = stickyNotes.map((note) => note.id === interaction.id
      ? { ...note, x: Math.round(x), y: Math.round(y), width: Math.round(width), height: Math.round(height) }
      : note);
  }

  function finishStickyNoteInteraction() {
    const interaction = stickyNoteInteraction;
    if (!interaction) return;
    stickyNoteInteraction = null;
    queueStickyNoteSave(interaction.id, 0);
  }

  function clampStickyNotesToViewport() {
    const changedIds: number[] = [];
    stickyNotes = stickyNotes.map((note) => {
      const width = Math.min(note.width, Math.max(125, window.innerWidth));
      const height = Math.min(note.height, Math.max(100, window.innerHeight));
      const x = Math.max(0, Math.min(note.x, window.innerWidth - width));
      const y = Math.max(
        stickyNoteTitlebarHeight,
        Math.min(note.y, Math.max(stickyNoteTitlebarHeight, window.innerHeight - height)));
      if (x === note.x && y === note.y && width === note.width && height === note.height) return note;
      changedIds.push(note.id);
      return { ...note, x, y, width, height };
    });
    changedIds.forEach((id) => queueStickyNoteSave(id, 250));
  }

  function loadStickyNoteBoard() {
    stickyNoteBoardIsLoading = true;
    stickyNoteBoardError = '';
    postHostMessage({ type: 'stickyNotes.board.list' });
  }

  function compareStickyNoteBoardItems(left: StickyNoteBoardItem, right: StickyNoteBoardItem) {
    const parseCreatedAt = (value: string) => Date.parse(value.includes('T') ? value : `${value.replace(' ', 'T')}Z`);
    const leftTime = parseCreatedAt(String(left.note.createdAt));
    const rightTime = parseCreatedAt(String(right.note.createdAt));
    const dateOrder = Number.isFinite(leftTime) && Number.isFinite(rightTime)
      ? leftTime - rightTime
      : String(left.note.createdAt).localeCompare(String(right.note.createdAt), 'ja-JP');
    return dateOrder || left.note.id - right.note.id;
  }

  function getStickyNoteViewLabel(viewType: StickyNoteView) {
    if (viewType === 'library') return 'Gallery';
    if (viewType === 'explorer') return 'Explorer';
    if (viewType === 'creators') return 'Creators';
    if (viewType === 'creatorTracking') return 'Creator Tracking';
    return 'User Metrics';
  }

  function getStickyNoteBoardContextLabel(note: StickyNoteItem) {
    if (note.viewType === 'library' && note.contextKey.includes(galleryWorkStickyContextSeparator)) {
      const sectionId = note.contextKey.split(galleryWorkStickyContextSeparator, 1)[0];
      const labelParts = (note.contextLabel || '').split(' > ');
      const workLabel = labelParts.length > 1 ? labelParts.slice(1).join(' > ') : note.contextLabel || note.contextKey;
      return `${getGallerySectionLabel(sectionId)} > ${workLabel}`;
    }
    if (note.viewType === 'library' || note.viewType === 'creators' || note.viewType === 'userMetrics') {
      return getGallerySectionLabel(note.contextKey);
    }
    return note.contextLabel || note.contextKey;
  }

  function getStickyNoteBoardGroups(items: StickyNoteBoardItem[]) {
    const order: StickyNoteView[] = ['library', 'explorer', 'creators', 'creatorTracking', 'userMetrics'];
    return order
      .map((viewType) => ({
        viewType,
        label: getStickyNoteViewLabel(viewType),
        items: items.filter((item) => item.note.viewType === viewType)
      }))
      .filter((group) => group.items.length > 0);
  }

  function updateStickyNoteBoardItem(id: number, patch: Partial<StickyNoteItem>, saveDelay = 350) {
    stickyNoteBoardItems = stickyNoteBoardItems.map((item) => item.note.id === id
      ? { ...item, note: { ...item.note, ...patch } }
      : item);
    stickyNotes = stickyNotes.map((note) => note.id === id ? { ...note, ...patch } : note);
    const current = stickyNoteBoardSaveTimers.get(id);
    if (current) clearTimeout(current);
    if (saveDelay <= 0) {
      stickyNoteBoardSaveTimers.delete(id);
      saveStickyNoteBoardItemNow(id);
      return;
    }
    stickyNoteBoardSaveTimers.set(id, setTimeout(() => {
      stickyNoteBoardSaveTimers.delete(id);
      saveStickyNoteBoardItemNow(id);
    }, saveDelay));
  }

  function saveStickyNoteBoardItemNow(id: number) {
    const item = stickyNoteBoardItems.find((candidate) => candidate.note.id === id);
    if (!item) return;
    postHostMessage({ type: 'stickyNotes.board.save', note: item.note });
  }

  function setStickyNoteBoardColor(id: number, colorKey: StickyNoteColorKey) {
    stickyNotePaletteOpenId = null;
    updateStickyNoteBoardItem(id, { colorKey }, 0);
  }

  function toggleStickyNoteBoardContentMode(id: number) {
    const item = stickyNoteBoardItems.find((candidate) => candidate.note.id === id);
    if (!item) return;
    const contentMode = item.note.contentMode === 'plain' ? 'markdown' : 'plain';
    setMarkdownNoteEditing(id, false);
    updateStickyNoteBoardItem(id, { contentMode }, 0);
  }

  function deleteStickyNoteBoardItem(id: number) {
    const timer = stickyNoteBoardSaveTimers.get(id);
    if (timer) clearTimeout(timer);
    stickyNoteBoardSaveTimers.delete(id);
    stickyNoteBoardItems = stickyNoteBoardItems.filter((item) => item.note.id !== id);
    stickyNotes = stickyNotes.filter((note) => note.id !== id);
    setMarkdownNoteEditing(id, false);
    if (stickyNotePaletteOpenId === id) stickyNotePaletteOpenId = null;
    postHostMessage({ type: 'stickyNotes.board.delete', id });
  }

  function captureStickyNotesForCurrentContext() {
    if (!isBookmarkableView(activeView)) return [];
    return visibleStickyNotes.map((note) => ({ ...note }));
  }

  function restoreStickyNotesFromBookmark(
    snapshots: StickyNoteItem[] | undefined,
    viewType: BookmarkableView,
    contextKey: string,
    warnings: string[])
  {
    if (!Array.isArray(snapshots) || snapshots.length === 0) return;
    const normalizedContextKey = normalizeStickyNoteContextKey(viewType, contextKey);
    const normalized = snapshots
      .map((note) => normalizeStickyNote(note))
      .filter((note): note is StickyNoteItem => note !== null);
    const restorable = normalized.filter((note) =>
      note.viewType === viewType && (
        note.contextKey === normalizedContextKey ||
        (viewType === 'library' && isGalleryWorkStickyNoteContext(note.contextKey, normalizedContextKey))));
    if (restorable.length !== snapshots.length) {
      warnings.push('Bookmarkに保存された一部の付箋は、対象画面を復元できないため開けませんでした。');
    }
    if (restorable.length === 0) return;
    const restoredIds = new Set(restorable.map((note) => note.id));
    stickyNotes = [...stickyNotes.filter((note) => !restoredIds.has(note.id)), ...restorable];
    postHostMessage({ type: 'stickyNotes.restore', notes: restorable });
    requestAnimationFrame(clampStickyNotesToViewport);
  }

  function captureCurrentBookmark(): BookmarkCapture | null {
    if (!isBookmarkableView(activeView)) return null;

    if (activeView === 'library') {
      const state: GalleryBookmarkState = {
        version: 4,
        section: gallerySection,
        ratingFilters: [...galleryRatingFilters],
        tagFilters: [...galleryTagFilters],
        creatorFilters: [...galleryCreatorFilters],
        titleFilters: [...galleryTitleFilters],
        characterFilters: [...galleryCharacterFilters],
        filterSorts: galleryFilterSorts.map((criterion) => ({ ...criterion })),
        thumbnailSorts: galleryThumbnailSorts.map((criterion) => ({ ...criterion })),
        expanded: {
          creator: galleryCreatorFiltersExpanded,
          title: galleryTitleFiltersExpanded,
          character: galleryCharacterFiltersExpanded,
          tag: galleryTagFiltersExpanded
        },
        collapsePins: { ...galleryCollapsePins },
        pinned: {
          creators: [...galleryPinnedCreators],
          titles: [...galleryPinnedTitles]
        },
        promoted: {
          creators: [...galleryPromotedCreators],
          titles: [...galleryPromotedTitles],
          characters: [...galleryPromotedCharacters],
          tags: [...galleryPromotedTags]
        },
        cardColumns: galleryCardColumns,
        scrollTop: galleryContentElement?.scrollTop ?? 0,
        loadedCount: galleryWorks.length,
        query: galleryQuery,
        stickyNotes: captureStickyNotesForCurrentContext()
      };
      const sectionLabel = getGallerySectionLabel(gallerySection);
      return {
        viewType: 'library',
        viewLabel: 'Gallery',
        suggestedName: `Gallery - ${sectionLabel}`,
        stateJson: JSON.stringify(state)
      };
    }

    if (activeView === 'explorer') {
      saveExplorerTabScroll();
      const activeIndex = Math.max(0, explorerTabs.findIndex((tab) => tab.id === activeExplorerTabId));
      const leftIndex = explorerSplit ? explorerTabs.findIndex((tab) => tab.id === explorerSplit.leftTabId) : -1;
      const rightIndex = explorerSplit ? explorerTabs.findIndex((tab) => tab.id === explorerSplit.rightTabId) : -1;
      const state: ExplorerBookmarkState = {
        version: 3,
        tabs: explorerTabs.map((tab) => ({ path: tab.path, label: tab.label })),
        activeIndex,
        split: explorerSplit && leftIndex >= 0 && rightIndex >= 0
          ? { leftIndex, rightIndex, focusedPane: splitFocusedPane }
          : null,
        query: explorerQuery,
        splitQuery: splitExplorerQuery,
        sort: explorerSort,
        sortDirection: explorerSortDirection,
        cardColumns: explorerCardColumns,
        tabScrollPositions: explorerTabs.map((tab, index) => ({
          index,
          gridTop: explorerTabScrollPositions[tab.id]?.gridTop ?? 0,
          detailTop: explorerTabScrollPositions[tab.id]?.detailTop ?? 0
        })),
        splitScroll: explorerSplit
          ? {
              leftTop: explorerSplitLeftPaneElement?.scrollTop ?? 0,
              rightTop: explorerSplitRightPaneElement?.scrollTop ?? 0
            }
          : undefined,
        stickyNotes: captureStickyNotesForCurrentContext()
      };
      const activeLabel = explorerTabs[activeIndex]?.label || '現在のタブ';
      return {
        viewType: 'explorer',
        viewLabel: 'Explorer',
        suggestedName: `Explorer - ${activeLabel}`,
        stateJson: JSON.stringify(state)
      };
    }

    if (activeView === 'creators') {
      const state: CreatorSummaryBookmarkState = {
        version: 4,
        section: galleryCreatorSummarySection,
        ratings: [...galleryCreatorSummaryRatings],
        coreTitles: [...galleryCreatorSummaryCoreTitles],
        coreTags: [...galleryCreatorSummaryCoreTags],
        sites: [...galleryCreatorSummarySites],
        overallRatings: [...galleryCreatorSummaryOverallRatings],
        metricScores: Object.fromEntries(
          creatorTrackingMetricDefinitions.map((metric) => [metric.key, [...galleryCreatorSummaryMetricScores[metric.key]]])),
        followReminder: galleryCreatorSummaryReminderFilter,
        query: galleryCreatorSummaryQuery,
        sorts: galleryCreatorSummarySorts.map((criterion) => ({ ...criterion })),
        expanded: {
          coreTitles: galleryCreatorSummaryCoreTitlesExpanded,
          coreTags: galleryCreatorSummaryCoreTagsExpanded,
          more: galleryCreatorSummaryMoreExpanded
        },
        stickyNotes: captureStickyNotesForCurrentContext()
      };
      return {
        viewType: 'creators',
        viewLabel: 'Creators',
        suggestedName: `Creators - ${getGallerySectionLabel(galleryCreatorSummarySection)}`,
        stateJson: JSON.stringify(state)
      };
    }

    captureActiveCreatorTrackingTab();
    const activeIndex = Math.max(0, creatorTrackingTabs.findIndex((tab) => tab.id === activeCreatorTrackingTabId));
    const state: CreatorTrackingBookmarkState = {
      version: 4,
      tabs: creatorTrackingTabs.map((tab) => ({
        creator: tab.creator,
        category: tab.summary.category,
        creatorFolder: tab.summary.creatorFolder,
        billingView: tab.id === activeCreatorTrackingTabId ? creatorTrackingBillingView : tab.billingView,
        archiveScale: tab.id === activeCreatorTrackingTabId ? creatorTrackingArchiveScale : tab.archiveScale
      })),
      activeIndex,
      stickyNotes: captureStickyNotesForCurrentContext()
    };
    const activeCreator = state.tabs[activeIndex]?.creator || '作者';
    return {
      viewType: 'creatorTracking',
      viewLabel: 'Creator Tracking',
      suggestedName: `Creator Tracking - ${activeCreator}`,
      stateJson: JSON.stringify(state)
    };
  }

  function captureNavigationHistoryEntry(): NavigationHistoryEntry {
    return {
      view: activeView,
      capture: captureCurrentBookmark(),
      settingsSection,
      userGuideSection: activeUserGuideSection,
      userMetricsCategory
    };
  }

  function getNavigationHistoryFingerprint(entry: NavigationHistoryEntry) {
    return JSON.stringify({
      view: entry.view,
      stateJson: entry.capture?.stateJson ?? '',
      settingsSection: entry.settingsSection,
      userGuideSection: entry.userGuideSection,
      userMetricsCategory: entry.userMetricsCategory
    });
  }

  function recordNavigationHistory() {
    if (navigationHistoryRestoring) return;
    const entry = captureNavigationHistoryEntry();
    const previous = navigationBackStack.at(-1);
    if (!previous || getNavigationHistoryFingerprint(previous) !== getNavigationHistoryFingerprint(entry)) {
      navigationBackStack = [...navigationBackStack, entry].slice(-50);
    }
    navigationForwardStack = [];
  }

  function restoreNavigationHistoryEntry(entry: NavigationHistoryEntry) {
    navigationHistoryRestoring = true;
    try {
      if (entry.capture) {
        const historyBookmark: ViewBookmark = {
          id: -Date.now(),
          name: '前の画面',
          viewType: entry.capture.viewType,
          stateJson: entry.capture.stateJson,
          thumbnailDataUrl: '',
          windowWidth: 0,
          windowHeight: 0,
          windowIsMaximized: false,
          position: 0,
          createdAt: '',
          updatedAt: ''
        };
        openViewBookmark(historyBookmark, false);
        return;
      }

      settingsSection = entry.settingsSection;
      activeUserGuideSection = entry.userGuideSection;
      userMetricsCategory = entry.userMetricsCategory;
      activateView(entry.view);
      if (entry.view === 'userMetrics') loadUserMetrics(entry.userMetricsCategory);
    }
    finally {
      navigationHistoryRestoring = false;
    }
  }

  function navigateAppHistory(direction: -1 | 1) {
    const source = direction < 0 ? navigationBackStack : navigationForwardStack;
    const target = source.at(-1);
    if (!target) {
      showExplorerToast(direction < 0 ? 'これより前の画面はありません。' : 'これより後の画面はありません。', 'error');
      return;
    }

    const current = captureNavigationHistoryEntry();
    if (direction < 0) {
      navigationBackStack = source.slice(0, -1);
      navigationForwardStack = [...navigationForwardStack, current].slice(-50);
    }
    else {
      navigationForwardStack = source.slice(0, -1);
      navigationBackStack = [...navigationBackStack, current].slice(-50);
    }
    restoreNavigationHistoryEntry(target);
  }

  function openBookmarks() {
    bookmarkCapture = null;
    activateView('bookmarks');
  }

  async function continueGalleryBookmarkScrollRestore() {
    if (pendingGalleryBookmarkScrollTop === null || galleryIsLoading) return;
    const targetCount = Math.min(Math.max(0, pendingGalleryBookmarkLoadedCount), galleryTotal);
    if (galleryWorks.length < targetCount) {
      loadGalleryWorks(
        true,
        galleryRatingFilters,
        false,
        [],
        Math.min(200, targetCount - galleryWorks.length)
      );
      return;
    }

    const scrollTop = pendingGalleryBookmarkScrollTop;
    pendingGalleryBookmarkScrollTop = null;
    pendingGalleryBookmarkLoadedCount = 0;
    await tick();
    requestAnimationFrame(() => {
      if (galleryContentElement) galleryContentElement.scrollTop = scrollTop;
    });
  }

  function captureViewBookmarkFromToolbar() {
    const captured = captureCurrentBookmark();
    if (!captured) return;
    bookmarkCapture = captured;
    bookmarkNameDraft = captured.suggestedName;
    bookmarkSaveDialogOpen = true;
  }

  async function saveViewBookmark() {
    if (!bookmarkCapture || !bookmarkNameDraft.trim()) return;
    const capture = bookmarkCapture;
    const name = bookmarkNameDraft.trim();
    bookmarkSaveDialogOpen = false;
    await tick();
    await new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
    bookmarkMutationMessage = 'Bookmarkを保存しました。';
    postHostMessage({
      type: 'view.bookmarks.save',
      name,
      viewType: capture.viewType,
      stateJson: capture.stateJson
    });
    bookmarkCapture = null;
  }

  function deleteViewBookmark() {
    if (!bookmarkDeleteCandidate) return;
    bookmarkMutationMessage = 'Bookmarkを削除しました。';
    postHostMessage({ type: 'view.bookmarks.delete', id: bookmarkDeleteCandidate.id });
    bookmarkDeleteCandidate = null;
  }

  function parseBookmarkState<T>(bookmark: ViewBookmark): T | null {
    try {
      return JSON.parse(bookmark.stateJson) as T;
    }
    catch {
      bookmarkRestoreWarnings = [`「${bookmark.name}」の保存データを読み取れませんでした。`];
      return null;
    }
  }

  function parseCreatorTrackingSessionState(rawValue: unknown): CreatorTrackingBookmarkState | null {
    if (typeof rawValue !== 'string' || !rawValue.trim()) {
      return null;
    }

    try {
      const state = JSON.parse(rawValue) as CreatorTrackingBookmarkState;
      const tabs = Array.isArray(state.tabs)
        ? state.tabs
            .map((tab) => {
              const billingView: 'subscriptions' | 'purchases' = tab.billingView === 'purchases' ? 'purchases' : 'subscriptions';
              const archiveScale: 'week' | 'month' | 'year' = tab.archiveScale === 'week' || tab.archiveScale === 'year'
                ? tab.archiveScale
                : 'month';
              return {
                creator: String(tab.creator ?? '').trim(),
                category: String(tab.category ?? '').trim(),
                creatorFolder: String(tab.creatorFolder ?? '').trim(),
                billingView,
                archiveScale
              };
            })
            .filter((tab) => tab.creator)
        : [];
      return {
        version: Number(state.version ?? 4),
        tabs,
        activeIndex: Math.max(0, Math.min(tabs.length - 1, Math.round(Number(state.activeIndex ?? 0)))),
        stickyNotes: []
      };
    }
    catch {
      return null;
    }
  }

  function describeViewBookmark(bookmark: ViewBookmark) {
    try {
      const state = JSON.parse(bookmark.stateJson) as Record<string, unknown>;
      if (bookmark.viewType === 'library') {
        const filterCount = ['ratingFilters', 'tagFilters', 'creatorFilters', 'titleFilters', 'characterFilters']
          .reduce((count, key) => count + (Array.isArray(state[key]) ? state[key].length : 0), 0);
        return `${getGallerySectionLabel(String(state.section ?? ''))} / 選択フィルター ${filterCount}`;
      }
      if (bookmark.viewType === 'explorer') {
        return `${Array.isArray(state.tabs) ? state.tabs.length : 0} タブ${state.split ? ' / 分割表示' : ''}`;
      }
      if (bookmark.viewType === 'creators') {
        const metricScores = state.metricScores && typeof state.metricScores === 'object'
          ? Object.values(state.metricScores as Record<string, unknown>)
              .reduce((count, values) => count + (Array.isArray(values) ? values.length : 0), 0)
          : 0;
        const filterCount = ['ratings', 'coreTitles', 'coreTags', 'sites', 'overallRatings']
          .reduce((count, key) => count + (Array.isArray(state[key]) ? state[key].length : 0), 0);
        const reminderFilterCount = state.followReminder === 'warning' || state.followReminder === 'alert' ? 1 : 0;
        return `${getGallerySectionLabel(String(state.section ?? ''))} / 選択フィルター ${filterCount + metricScores + reminderFilterCount}`;
      }
      return `${Array.isArray(state.tabs) ? state.tabs.length : 0} Creator タブ`;
    }
    catch {
      return '保存内容を読み取れません';
    }
  }

  function formatBookmarkDate(value: string) {
    if (!value) return '';
    const normalized = value.includes('T') ? value : `${value.replace(' ', 'T')}Z`;
    const date = new Date(normalized);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString('ja-JP', { dateStyle: 'short', timeStyle: 'short' });
  }

  function openViewBookmark(bookmark: ViewBookmark, restoreWindow = true) {
    bookmarkRestoreWarnings = [];
    if (restoreWindow) {
      postHostMessage({
        type: 'view.bookmarks.window.restore',
        width: Number(bookmark.windowWidth ?? 0),
        height: Number(bookmark.windowHeight ?? 0),
        isMaximized: Boolean(bookmark.windowIsMaximized)
      });
    }
    if (bookmark.viewType === 'library') {
      const state = parseBookmarkState<GalleryBookmarkState>(bookmark);
      if (state) requestGalleryBookmarkRestore(bookmark, state);
      return;
    }
    if (bookmark.viewType === 'explorer') {
      const state = parseBookmarkState<ExplorerBookmarkState>(bookmark);
      if (state) requestExplorerBookmarkRestore(bookmark, state);
      return;
    }
    if (bookmark.viewType === 'creators') {
      const state = parseBookmarkState<CreatorSummaryBookmarkState>(bookmark);
      if (!state) return;
      if (galleryCreatorSummaries.length === 0 || galleryCreatorSummaryIsLoading) {
        pendingCreatorSummaryBookmarkRestore = { bookmark, state };
        loadGalleryCreatorSummaries();
      }
      else {
        restoreCreatorSummaryBookmark(bookmark, state);
      }
      return;
    }
    if (bookmark.viewType === 'creatorTracking') {
      const state = parseBookmarkState<CreatorTrackingBookmarkState>(bookmark);
      if (!state) return;
      pendingCreatorTrackingBookmarkRestore = { bookmark, state };
      if (galleryCreatorSummaries.length === 0 || galleryCreatorSummaryIsLoading) {
        loadGalleryCreatorSummaries();
      }
      else {
        restoreCreatorTrackingBookmark(bookmark, state);
      }
      return;
    }
    bookmarkRestoreWarnings = [`「${bookmark.name}」の画面種別は現在のバージョンでは開けません。`];
  }

  function requestGalleryBookmarkRestore(bookmark: ViewBookmark, state: GalleryBookmarkState) {
    const section = gallerySections.some((candidate) => candidate.id === state.section) ? state.section : gallerySections[0].id;
    if (section !== state.section) {
      bookmarkRestoreWarnings = [...bookmarkRestoreWarnings, `Gallery区分「${state.section}」は存在しないため、${getGallerySectionLabel(section)}を開きました。`];
    }
    gallerySection = section;
    loadGalleryPins(section);
    activeView = 'library';
    persistNavigationState();
    const requestId = `bookmark-gallery-${nextGalleryRequestId++}`;
    galleryRequestId = requestId;
    galleryIsLoading = true;
    galleryWorks = [];
    galleryTotal = 0;
    pendingGalleryBookmarkRestore = { bookmark, state: { ...state, section }, requestId };
    postHostMessage({
      type: 'gallery.works.filters',
      requestId,
      category: section,
      ratings: state.ratingFilters ?? [],
      tags: state.tagFilters ?? [],
      creators: state.creatorFilters ?? [],
      titles: state.titleFilters ?? [],
      characters: [],
      filterSort: 'rating:desc',
      filterParts: ['ratings', 'tags', 'creators', 'titles', 'characters'],
      isSnapshot: true
    });
  }

  function restoreGalleryBookmarkWithFilters(filters: {
    ratings?: GalleryFilterOption[];
    tags?: GalleryFilterOption[];
    creators?: GalleryFilterOption[];
    titles?: GalleryFilterOption[];
    characters?: GalleryFilterOption[];
  } | null) {
    if (!pendingGalleryBookmarkRestore) return;
    const { bookmark, state } = pendingGalleryBookmarkRestore;
    pendingGalleryBookmarkRestore = null;
    const warnings = [...bookmarkRestoreWarnings];
    const validStrings = (values: string[], options: GalleryFilterOption[] | undefined, label: string, enabled: boolean) => {
      if (!enabled) {
        if (values.length > 0) warnings.push(`${label}フィルターは現在この区分で無効です。`);
        return [];
      }
      if (!options) return values;
      const available = new Set(options.map((option) => option.value));
      const valid = values.filter((value) => available.has(value));
      for (const value of values.filter((value) => !available.has(value))) {
        warnings.push(`${label}「${value}」は無効化または削除されているため復元できませんでした。`);
      }
      return valid;
    };
    const ratingOptions = filters?.ratings ? new Set(filters.ratings.map((option) => Number(option.value))) : null;
    galleryRatingFilters = isGalleryFilterEnabled('rating', state.section)
      ? state.ratingFilters.filter((value) => {
          const valid = !ratingOptions || ratingOptions.has(value);
          if (!valid) warnings.push(`Rating「${value}」は復元できませんでした。`);
          return valid;
        })
      : [];
    if (!isGalleryFilterEnabled('rating', state.section) && state.ratingFilters.length > 0) warnings.push('Ratingフィルターは現在この区分で無効です。');
    galleryCreatorFilters = validStrings(state.creatorFilters ?? [], filters?.creators, 'Creator', isGalleryFilterEnabled('creator', state.section));
    galleryTitleFilters = validStrings(state.titleFilters ?? [], filters?.titles, 'Title', isGalleryFilterEnabled('title', state.section));
    galleryCharacterFilters = validStrings(state.characterFilters ?? [], filters?.characters, 'Character', isGalleryFilterEnabled('character', state.section));
    galleryTagFilters = validStrings(state.tagFilters ?? [], filters?.tags, 'Tag', isGalleryFilterEnabled('tag', state.section));
    galleryQuery = state.query ?? '';
    const validFilterSortKeys = new Set<GalleryFilterSortKey>(['rating', 'files', 'name']);
    galleryFilterSorts = (state.filterSorts ?? []).filter((item) => validFilterSortKeys.has(item.key) && (item.direction === 'asc' || item.direction === 'desc'));
    const validThumbnailSortKeys = new Set<GalleryThumbnailSortKey>(['rating', 'images', 'accessed', 'path']);
    galleryThumbnailSorts = (state.thumbnailSorts ?? []).filter((item) => validThumbnailSortKeys.has(item.key) && (item.direction === 'asc' || item.direction === 'desc'));
    galleryCreatorFiltersExpanded = Boolean(state.expanded?.creator);
    galleryTitleFiltersExpanded = Boolean(state.expanded?.title);
    galleryCharacterFiltersExpanded = Boolean(state.expanded?.character);
    galleryTagFiltersExpanded = Boolean(state.expanded?.tag);
    galleryCollapsePins = {
      creator: Boolean(state.collapsePins?.creator),
      title: Boolean(state.collapsePins?.title),
      character: Boolean(state.collapsePins?.character),
      tag: Boolean(state.collapsePins?.tag)
    };
    const restorePins = (values: string[], options: GalleryFilterOption[] | undefined, label: string) => {
      if (!options) return values;
      const available = new Set(options.map((option) => option.value));
      const valid = values.filter((value) => available.has(value));
      for (const value of values.filter((value) => !available.has(value))) {
        warnings.push(`${label}ピン「${value}」は無効化または削除されているため復元できませんでした。`);
      }
      return valid;
    };
    if (state.pinned) {
      galleryPinnedCreators = restorePins(state.pinned.creators ?? [], filters?.creators, 'Creator');
      galleryPinnedTitles = restorePins(state.pinned.titles ?? [], filters?.titles, 'Title');
      saveGalleryPins();
    }
    galleryPromotedCreators = (state.promoted?.creators ?? []).filter((value) => galleryCreatorFilters.includes(value));
    galleryPromotedTitles = (state.promoted?.titles ?? []).filter((value) => galleryTitleFilters.includes(value));
    galleryPromotedCharacters = (state.promoted?.characters ?? []).filter((value) => galleryCharacterFilters.includes(value));
    galleryPromotedTags = (state.promoted?.tags ?? []).filter((value) => galleryTagFilters.includes(value));
    galleryWorks = [];
    galleryThumbnails = {};
    requestedGalleryThumbnailIds.clear();
    unavailableGalleryThumbnailIds.clear();
    if ([5, 6, 7, 8, 9].includes(Number(state.cardColumns))) {
      setGalleryCardColumns(Number(state.cardColumns), state.section);
    }
    pendingGalleryBookmarkScrollTop = Math.max(0, Number(state.scrollTop ?? 0));
    pendingGalleryBookmarkLoadedCount = Math.max(0, Number(state.loadedCount ?? 0));
    loadGalleryWorks(
      false,
      galleryRatingFilters,
      true,
      [],
      Math.min(200, Math.max(96, pendingGalleryBookmarkLoadedCount))
    );
    restoreStickyNotesFromBookmark(state.stickyNotes, 'library', state.section, warnings);
    bookmarkRestoreWarnings = warnings;
    if (bookmark.id >= 0) showExplorerToast(`Bookmark「${bookmark.name}」を開きました。`, 'success');
  }

  function requestExplorerBookmarkRestore(bookmark: ViewBookmark, state: ExplorerBookmarkState) {
    const paths = (state.tabs ?? []).map((tab) => tab.path).filter(Boolean);
    const requestId = `bookmark-explorer-${nextViewBookmarkRestoreRequestId++}`;
    pendingExplorerBookmarkRestore = { bookmark, state, requestId };
    postHostMessage({ type: 'view.bookmarks.paths.validate', requestId, paths });
  }

  function restoreExplorerBookmark(bookmark: ViewBookmark, state: ExplorerBookmarkState, validPaths: string[], invalidPaths: string[]) {
    const valid = new Set(validPaths.map((path) => path.toLocaleLowerCase('ja-JP')));
    const retainedTabs = (state.tabs ?? [])
      .map((tab, originalIndex) => ({ tab, originalIndex }))
      .filter(({ tab }) => valid.has(tab.path.toLocaleLowerCase('ja-JP')));
    const warnings = invalidPaths.map((path) => `Explorerのパス「${path}」は存在しないため開けませんでした。`);
    if (retainedTabs.length === 0) {
      warnings.push('開けるExplorerタブがないため、既定の場所を開きました。');
      retainedTabs.push({ tab: { path: '', label: '新しいタブ' }, originalIndex: -1 });
    }
    const restoredTabs = retainedTabs.map(({ tab }) => ({ id: `explorer-tab-${nextExplorerTabId++}`, path: tab.path, label: tab.label || getExplorerTabLabel(tab.path) }));
    const activeIndex = retainedTabs.findIndex(({ originalIndex }) => originalIndex === Math.max(0, state.activeIndex));
    explorerTabs = restoredTabs;
    activeExplorerTabId = restoredTabs[Math.max(0, activeIndex)]?.id ?? restoredTabs[0].id;
    explorerQuery = state.query ?? '';
    splitExplorerQuery = state.splitQuery ?? '';
    explorerSort = ['name', 'modified', 'size', 'type'].includes(state.sort) ? state.sort : 'name';
    explorerSortDirection = state.sortDirection === 'desc' ? 'desc' : 'asc';
    explorerCardColumns = [4, 5, 6, 7].includes(state.cardColumns) ? state.cardColumns : explorerCardColumns;
    explorerTabScrollPositions = Object.fromEntries(
      (state.tabScrollPositions ?? [])
        .filter((position) => restoredTabs[position.index])
        .map((position) => [restoredTabs[position.index].id, {
          gridTop: Math.max(0, Number(position.gridTop ?? 0)),
          detailTop: Math.max(0, Number(position.detailTop ?? 0))
        }])
    );
    explorerSplit = null;
    splitFocusedPane = 'left';
    pendingExplorerSplitScroll = null;
    activeView = 'explorer';
    persistNavigationState();
    const activeTab = restoredTabs.find((tab) => tab.id === activeExplorerTabId) ?? restoredTabs[0];
    pendingExplorerScrollRestoreTabId = activeTab.id;
    loadExplorer(activeTab.path || undefined, true);
    if (state.split) {
      const leftRetainedIndex = retainedTabs.findIndex(({ originalIndex }) => originalIndex === state.split?.leftIndex);
      const rightRetainedIndex = retainedTabs.findIndex(({ originalIndex }) => originalIndex === state.split?.rightIndex);
      const left = leftRetainedIndex >= 0 ? restoredTabs[leftRetainedIndex] : null;
      const right = rightRetainedIndex >= 0 ? restoredTabs[rightRetainedIndex] : null;
      if (left && right && left.id !== right.id) {
        activeExplorerTabId = left.id;
        explorerSplit = {
          leftTabId: left.id,
          rightTabId: right.id,
          rightPath: right.path,
          rightParentPath: null,
          rightEntries: [],
          rightSelectedPaths: [],
          rightIsLoading: true,
          rightIsTruncated: false
        };
        splitFocusedPane = state.split.focusedPane === 'right' ? 'right' : 'left';
        pendingExplorerSplitScroll = state.splitScroll
          ? {
              leftTop: Math.max(0, Number(state.splitScroll.leftTop ?? 0)),
              rightTop: Math.max(0, Number(state.splitScroll.rightTop ?? 0))
            }
          : { leftTop: 0, rightTop: 0 };
        loadSplitExplorer(right.path);
      }
      else {
        warnings.push('分割表示の片方のタブを開けないため、通常表示で復元しました。');
      }
    }
    const restoredStickyNotePath = explorerSplit && splitFocusedPane === 'right'
      ? explorerSplit.rightPath
      : (explorerSplit
          ? restoredTabs.find((tab) => tab.id === explorerSplit?.leftTabId)?.path ?? activeTab.path
          : activeTab.path);
    restoreStickyNotesFromBookmark(state.stickyNotes, 'explorer', restoredStickyNotePath, warnings);
    persistExplorerTabs();
    bookmarkRestoreWarnings = warnings;
    if (bookmark.id >= 0) showExplorerToast(`Bookmark「${bookmark.name}」を開きました。`, 'success');
  }

  function restoreCreatorSummaryBookmark(bookmark: ViewBookmark, state: CreatorSummaryBookmarkState) {
    const section = gallerySections.some((candidate) => candidate.id === state.section) ? state.section : gallerySections[0].id;
    const warnings: string[] = [];
    if (section !== state.section) warnings.push(`Creators区分「${state.section}」は存在しないため復元できませんでした。`);
    const sectionItems = galleryCreatorSummaries.filter((item) => item.category === section);
    const validRatings = new Set(galleryCreatorRatingBuckets.map((item) => item.value));
    const validCoreTitles = new Set(sectionItems.flatMap((item) => item.coreTitles));
    const validCoreTags = new Set(sectionItems.flatMap((item) => item.coreTags));
    const validSites = new Set(sectionItems.flatMap((item) => item.trackingSites ?? []));
    const retain = (values: string[], validValues: Set<string>, label: string) => values.filter((value) => {
      const valid = validValues.has(value);
      if (!valid) warnings.push(`${label}「${value}」は無効化または削除されているため復元できませんでした。`);
      return valid;
    });
    galleryCreatorSummarySection = section;
    galleryCreatorSummaryRatings = retain(state.ratings ?? [], validRatings, 'Rating');
    if (isGalleryFilterEnabled('core_title', section)) {
      galleryCreatorSummaryCoreTitles = retain(state.coreTitles ?? [], validCoreTitles, getGalleryFilterLabel('core_title', section));
    }
    else {
      galleryCreatorSummaryCoreTitles = [];
      if ((state.coreTitles?.length ?? 0) > 0) warnings.push(`${getGalleryFilterLabel('core_title', section)}は区分設定で無効のため復元できませんでした。`);
    }
    if (isGalleryFilterEnabled('core_tags', section)) {
      galleryCreatorSummaryCoreTags = retain(state.coreTags ?? [], validCoreTags, getGalleryFilterLabel('core_tags', section));
    }
    else {
      galleryCreatorSummaryCoreTags = [];
      if ((state.coreTags?.length ?? 0) > 0) warnings.push(`${getGalleryFilterLabel('core_tags', section)}は区分設定で無効のため復元できませんでした。`);
    }
    galleryCreatorSummarySites = retain(state.sites ?? [], validSites, 'Site');
    galleryCreatorSummaryOverallRatings = (state.overallRatings ?? []).filter((score) => Number.isInteger(score) && score >= 1 && score <= 5);
    galleryCreatorSummaryMetricScores = Object.fromEntries(creatorTrackingMetricDefinitions.map((metric) => [
      metric.key,
      (state.metricScores?.[metric.key] ?? []).filter((score) => Number.isInteger(score) && score >= 1 && score <= 4)
    ])) as Record<CreatorTrackingMetricKey, number[]>;
    galleryCreatorSummaryReminderFilter = state.followReminder === 'warning' || state.followReminder === 'alert'
      ? state.followReminder
      : '';
    galleryCreatorSummaryQuery = state.query ?? '';
    const validSortKeys = new Set(galleryCreatorSummarySortDefinitions.map((definition) => definition.key));
    const restoredSorts: GalleryCreatorSummarySortCriterion[] = [];
    for (const criterion of state.sorts ?? []) {
      if (restoredSorts.length >= 3
        || !validSortKeys.has(criterion.key)
        || (criterion.direction !== 'asc' && criterion.direction !== 'desc')
        || restoredSorts.some((restored) => restored.key === criterion.key)) {
        continue;
      }
      restoredSorts.push({
        key: criterion.key,
        direction: criterion.key === 'rating' ? 'desc' : criterion.direction
      });
    }
    if (restoredSorts.length > 0) {
      galleryCreatorSummarySorts = restoredSorts;
    }
    else if (state.sort && validSortKeys.has(state.sort)) {
      galleryCreatorSummarySorts = [{
        key: state.sort,
        direction: state.sort === 'rating'
          ? 'desc'
          : state.sortDirections?.[state.sort] ?? getGalleryCreatorSummaryDefaultSortDirection(state.sort)
      }];
    }
    else {
      galleryCreatorSummarySorts = [{ key: 'rating', direction: 'desc' }];
    }
    galleryCreatorSummaryCoreTitlesExpanded = Boolean(state.expanded?.coreTitles);
    galleryCreatorSummaryCoreTagsExpanded = Boolean(state.expanded?.coreTags);
    galleryCreatorSummaryMoreExpanded = Boolean(state.expanded?.more);
    creatorsNavigationExpanded = true;
    activeView = 'creators';
    persistNavigationState();
    restoreStickyNotesFromBookmark(state.stickyNotes, 'creators', section, warnings);
    bookmarkRestoreWarnings = warnings;
    if (bookmark.id >= 0) showExplorerToast(`Bookmark「${bookmark.name}」を開きました。`, 'success');
  }

  function restoreCreatorTrackingBookmark(bookmark: ViewBookmark, state: CreatorTrackingBookmarkState) {
    pendingCreatorTrackingBookmarkRestore = null;
    saveCreatorTracking();
    captureActiveCreatorTrackingTab();
    creatorTrackingTabs = [];
    activeCreatorTrackingTabId = '';
    const warnings: string[] = [];
    const restored: CreatorTrackingTab[] = [];
    for (const savedTab of state.tabs ?? []) {
      const category = savedTab.category && gallerySections.some((section) => section.id === savedTab.category)
        ? savedTab.category
        : galleryCreatorSummarySection || gallerySections[0]?.id || defaultGallerySectionId;
      const summary = galleryCreatorSummaries.find((item) =>
        item.category === category &&
        item.creator.trim().localeCompare(savedTab.creator.trim(), 'ja-JP', { sensitivity: 'base' }) === 0)
        ?? createCreatorTrackingTemplateSummary(savedTab.creator, category, savedTab.creatorFolder ?? '');
      restored.push(createCreatorTrackingTab(summary, savedTab.billingView, savedTab.archiveScale));
    }
    if (restored.length === 0) {
      activeView = 'creators';
      warnings.push('開けるCreator TrackingタブがないためCreatorsを開きました。');
    }
    else {
      creatorTrackingTabs = restored;
      const originalActiveTab = state.tabs?.[Math.max(0, state.activeIndex)];
      const activeTab = restored.find((tab) =>
        tab.creator === originalActiveTab?.creator &&
        (!originalActiveTab.category || tab.summary.category === originalActiveTab.category)) ?? restored[0];
      applyCreatorTrackingTab(activeTab);
      activeView = 'creatorTracking';
      restoreStickyNotesFromBookmark(state.stickyNotes, 'creatorTracking', activeTab.creator, warnings);
    }
    persistNavigationState();
    bookmarkRestoreWarnings = warnings;
    if (bookmark.id >= 0) showExplorerToast(`Bookmark「${bookmark.name}」を開きました。`, 'success');
  }

  function restoreCreatorTrackingSession(state: CreatorTrackingBookmarkState, activate: boolean) {
    saveCreatorTracking();
    captureActiveCreatorTrackingTab();
    creatorTrackingTabs = [];
    activeCreatorTrackingTabId = '';
    const restored: CreatorTrackingTab[] = [];
    for (const savedTab of state.tabs ?? []) {
      const category = savedTab.category && gallerySections.some((section) => section.id === savedTab.category)
        ? savedTab.category
        : galleryCreatorSummarySection || gallerySections[0]?.id || defaultGallerySectionId;
      const summary = galleryCreatorSummaries.find((item) =>
        item.category === category &&
        item.creator.trim().localeCompare(savedTab.creator.trim(), 'ja-JP', { sensitivity: 'base' }) === 0)
        ?? createCreatorTrackingTemplateSummary(savedTab.creator, category, savedTab.creatorFolder ?? '');
      restored.push(createCreatorTrackingTab(summary, savedTab.billingView, savedTab.archiveScale));
    }

    if (restored.length === 0) {
      return;
    }

    creatorTrackingTabs = restored;
    const originalActiveTab = state.tabs?.[Math.max(0, state.activeIndex)];
    const activeTab = restored.find((tab) =>
      tab.creator === originalActiveTab?.creator &&
      (!originalActiveTab.category || tab.summary.category === originalActiveTab.category)) ?? restored[0];
    applyCreatorTrackingTab(activeTab);
    if (activate) {
      activeView = 'creatorTracking';
      queueMicrotask(scrollCreatorTrackingArchiveToEnd);
    }
    else if (activeView === 'creatorTracking') {
      activeView = 'creators';
    }
  }

  function setView(view: ActiveView) {
    requestViewChange({ view });
  }

  async function navigateUserGuideSection(sectionId: UserGuideSectionId) {
    activeUserGuideSection = sectionId;
    await tick();
    document.getElementById(`user-guide-${sectionId}`)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  async function restoreUserGuideSection() {
    await tick();
    document.getElementById(`user-guide-${activeUserGuideSection}`)?.scrollIntoView({ behavior: 'auto', block: 'start' });
  }

  function updateUserGuideSectionFromScroll(event: Event) {
    const container = event.currentTarget as HTMLElement;
    const guideLine = container.getBoundingClientRect().top + 118;
    let currentSection = userGuideSections[0].id;

    for (const section of userGuideSections) {
      const element = document.getElementById(`user-guide-${section.id}`);
      if (element && element.getBoundingClientRect().top <= guideLine) {
        currentSection = section.id;
      }
    }

    activeUserGuideSection = currentSection;
  }

  function requestViewChange(navigation: PendingFilterEditorNavigation) {
    if (activeView === 'filters' && navigation.view !== 'filters' && hasFilterEditorUnsavedChanges()) {
      pendingFilterEditorNavigation = navigation;
      return;
    }

    completeViewChange(navigation);
  }

  function completeViewChange(navigation: PendingFilterEditorNavigation) {
    if (navigation.gallerySection) {
      applyGallerySection(navigation.gallerySection);
      return;
    }

    activateView(navigation.view);
  }

  function activateView(view: ActiveView) {
    if (view !== activeView) recordNavigationHistory();
    if (activeView === 'creatorTracking' && view !== 'creatorTracking') {
      saveCreatorTracking();
    }
    if (view !== 'filters' && galleryTitleAssignmentReturnPending) {
      galleryTitleAssignmentReturnPending = false;
      pendingGalleryTitleAssignmentCategory = '';
      galleryTitleAssignment = null;
    }
    if (view !== 'filters' && galleryCharacterAssignmentReturnPending) {
      galleryCharacterAssignmentReturnPending = false;
      pendingGalleryCharacterAssignmentCategory = '';
      galleryCharacterAssignment = null;
    }
    if (view !== 'tags' && galleryTagAssignmentReturnPending) {
      galleryTagAssignmentReturnPending = false;
      galleryTagAssignment = null;
    }
    activeView = view;
    if (view === 'settings') {
      settingsSection = settingsSection || 'theme';
    }
    if (view === 'userGuide') void restoreUserGuideSection();
    persistNavigationState();
    if (view === 'explorer' && !explorerPath && !explorerIsLoading) {
      const activeTab = explorerTabs.find((tab) => tab.id === activeExplorerTabId);
      loadExplorer(activeTab?.path);
    }
    if (view === 'filters') {
      filterEditorSearch = '';
      postHostMessage({ type: 'filters.editor.list' });
    }
    if (view === 'tags') {
      tagManagementSearch = '';
      postHostMessage({ type: 'tags.manager.list' });
    }
    if (view === 'creators' && galleryCreatorSummaries.length === 0) {
      loadGalleryCreatorSummaries();
    }
    if (view === 'userMetrics' && (!userMetricsDashboard || userMetricsDashboard.category !== userMetricsCategory)) {
      loadUserMetrics();
    }
    if (view === 'board') {
      loadStickyNoteBoard();
    }
    if (view === 'calendar') {
      loadCalendarSubscriptions();
    }
  }

  function getEditableFilterAliases(definition: FilterEditorDefinition) {
    return definition.aliases
      .filter((alias) => alias.localeCompare(definition.canonicalName, 'ja-JP') !== 0)
      .join('\n');
  }

  function hasFilterEditorUnsavedChanges() {
    if (filterEditorAttribute === 'category') {
      const selected = filterEditorCategories.find((category) => category.id === filterEditorSelectedCategoryId);
      return selected
        ? filterEditorCategoryDraft.trim() !== selected.name
        : filterEditorCategoryDraft.trim().length > 0;
    }

    const selected = selectedFilterEditorDefinition();
    const aliases = filterEditorAliases.split(/\r?\n/).map((value) => value.trim()).filter(Boolean).join('\n');
    if (!selected) {
      return Boolean(
        filterEditorCanonicalName.trim() ||
        filterEditorCategoryName.trim() ||
        filterEditorParentTitleId !== null ||
        aliases
      );
    }

    return filterEditorCanonicalName.trim() !== selected.canonicalName ||
      filterEditorCategoryName.trim() !== (selected.categoryName === '未分類' ? '' : selected.categoryName) ||
      filterEditorParentTitleId !== selected.parentTitleId ||
      aliases !== getEditableFilterAliases(selected);
  }

  function cancelFilterEditorNavigation() {
    if (!filterEditorNavigationCommitInProgress) {
      pendingFilterEditorNavigation = null;
    }
  }

  function discardFilterEditorChangesAndNavigate() {
    if (!pendingFilterEditorNavigation || filterEditorNavigationCommitInProgress) {
      return;
    }
    const navigation = pendingFilterEditorNavigation;
    pendingFilterEditorNavigation = null;
    completeViewChange(navigation);
  }

  function commitFilterEditorChangesAndNavigate() {
    if (!pendingFilterEditorNavigation || filterEditorNavigationCommitInProgress) {
      return;
    }

    filterEditorNavigationCommitInProgress = true;
    const committed = filterEditorAttribute === 'category'
      ? saveFilterEditorCategory()
      : saveFilterEditorDefinition();
    if (!committed) {
      filterEditorNavigationCommitInProgress = false;
    }
  }

  function setFilterEditorView(view: 'filter' | 'category') {
    filterEditorView = view;
    filterEditorSearch = '';
    postHostMessage({ type: 'filters.editor.list' });
  }

  function createTagManagementDefinition() {
    tagManagementSelectedId = null;
    tagManagementDraft = '';
  }

  function selectTagManagementDefinition(tag: TagManagementDefinition) {
    tagManagementSelectedId = tag.id;
    tagManagementDraft = tag.tag;
  }

  function saveTagManagementDefinition() {
    const tag = tagManagementDraft.trim();
    if (!tag) {
      showExplorerToast('Tag名を入力してください。', 'error');
      return;
    }
    postHostMessage({
      type: 'tags.manager.save',
      id: tagManagementSelectedId,
      tag
    });
  }

  function disableTagManagementDefinition() {
    const selected = tagManagementTags.find((tag) => tag.id === tagManagementSelectedId);
    if (!selected) {
      return;
    }
    if (!window.confirm(`Tag「${selected.tag}」を無効化します。作品への既存の付与情報は保持されます。`)) {
      return;
    }
    postHostMessage({ type: 'tags.manager.disable', id: selected.id });
  }

  function requestTagManagementCsvPreview() {
    if (!tagManagementCsvReady) {
      return;
    }
    postHostMessage({ type: 'tags.manager.csv.preview', hasHeader: tagManagementCsvHasHeader });
  }

  function importTagManagementCsv() {
    if (!tagManagementCsvReady) {
      return;
    }
    if (!window.confirm(`${tagManagementCsvTotal} 行のTagリストをデータベースへ反映します。実行しますか？`)) {
      return;
    }
    postHostMessage({ type: 'tags.manager.csv.import', hasHeader: tagManagementCsvHasHeader });
  }

  function getTagManagementListRows(tags: TagManagementDefinition[], query: string) {
    const normalizedQuery = query.trim().toLocaleLowerCase('ja-JP');
    return [...tags]
      .sort((left, right) => left.position - right.position || left.tag.localeCompare(right.tag, 'ja-JP'))
      .filter((tag) => !normalizedQuery || tag.tag.toLocaleLowerCase('ja-JP').includes(normalizedQuery));
  }

  function reorderTagManagementDefinition(event: DragEvent, target: TagManagementDefinition) {
    event.preventDefault();
    event.stopPropagation();
    const sourceId = draggedTagManagementId;
    if (sourceId === null || sourceId === target.id) {
      draggedTagManagementId = null;
      tagManagementDropTargetId = null;
      return;
    }

    const next = [...tagManagementTags]
      .sort((left, right) => left.position - right.position || left.tag.localeCompare(right.tag, 'ja-JP'));
    const from = next.findIndex((tag) => tag.id === sourceId);
    const to = next.findIndex((tag) => tag.id === target.id);
    if (from < 0 || to < 0) {
      draggedTagManagementId = null;
      tagManagementDropTargetId = null;
      return;
    }

    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    tagManagementTags = next.map((tag, position) => ({ ...tag, position }));
    draggedTagManagementId = null;
    tagManagementDropTargetId = null;
    postHostMessage({ type: 'tags.manager.reorder', ids: tagManagementTags.map((tag) => tag.id) });
  }

  function getTagManagementRows(
    tags: TagManagementDefinition[],
    query: string,
    sortColumn: string,
    direction: 'asc' | 'desc'
  ) {
    const normalizedQuery = query.trim().toLocaleLowerCase('ja-JP');
    return tags
      .filter((tag) => !normalizedQuery || tag.tag.toLocaleLowerCase('ja-JP').includes(normalizedQuery))
      .sort((left, right) => {
        const leftValue = sortColumn === 'tag' ? left.tag : left.categories.includes(sortColumn);
        const rightValue = sortColumn === 'tag' ? right.tag : right.categories.includes(sortColumn);
        const result = typeof leftValue === 'boolean'
          ? Number(leftValue) - Number(rightValue)
          : leftValue.localeCompare(String(rightValue), 'ja-JP');
        return (direction === 'asc' ? result : -result) || left.tag.localeCompare(right.tag, 'ja-JP');
      });
  }

  function toggleTagManagementSort(column: string) {
    if (tagManagementSortColumn === column) {
      tagManagementSortDirection = tagManagementSortDirection === 'asc' ? 'desc' : 'asc';
    }
    else {
      tagManagementSortColumn = column;
      tagManagementSortDirection = 'asc';
    }
  }

  function tagManagementSortLabel(column: string) {
    return tagManagementSortColumn === column ? (tagManagementSortDirection === 'asc' ? ' ↑' : ' ↓') : '';
  }

  function selectTagManagementRow(event: MouseEvent, tag: TagManagementDefinition) {
    event.stopPropagation();
    const next = new Set(tagManagementSelectedIds);
    const currentIndex = tagManagementRowIndexById.get(tag.id) ?? -1;
    if (event.shiftKey && tagManagementSelectionAnchorId !== null) {
      const anchorIndex = tagManagementRowIndexById.get(tagManagementSelectionAnchorId) ?? -1;
      if (anchorIndex >= 0 && currentIndex >= 0) {
        next.clear();
        const [start, end] = anchorIndex < currentIndex ? [anchorIndex, currentIndex] : [currentIndex, anchorIndex];
        tagManagementVisibleTags.slice(start, end + 1).forEach((candidate) => next.add(candidate.id));
      }
    }
    else if (event.ctrlKey || event.metaKey) {
      if (next.has(tag.id)) next.delete(tag.id);
      else next.add(tag.id);
      tagManagementSelectionAnchorId = tag.id;
    }
    else {
      if (next.size === 1 && next.has(tag.id)) {
        next.clear();
        tagManagementSelectionAnchorId = null;
      }
      else {
        next.clear();
        next.add(tag.id);
        tagManagementSelectionAnchorId = tag.id;
      }
      selectTagManagementDefinition(tag);
    }
    tagManagementSelectedIds = next;
  }

  function clearTagManagementSelection(event: MouseEvent) {
    if ((event.target as Element | null)?.closest('.tag-management-matrix-row, button, input, label')) {
      return;
    }
    tagManagementSelectedIds = new Set();
    tagManagementSelectionAnchorId = null;
  }

  function clearTagManagementSelectionOnBackground(node: HTMLElement) {
    const handleClick = (event: MouseEvent) => clearTagManagementSelection(event);
    node.addEventListener('click', handleClick);
    return {
      destroy() {
        node.removeEventListener('click', handleClick);
      }
    };
  }

  function applyTagManagementMapping(tag: TagManagementDefinition, category: string, isEnabled: boolean) {
    const targetIds = tagManagementSelectedIds.size > 1 && tagManagementSelectedIds.has(tag.id)
      ? [...tagManagementSelectedIds]
      : [tag.id];
    tagManagementTags = tagManagementTags.map((candidate) => {
      if (!targetIds.includes(candidate.id)) return candidate;
      const categories = isEnabled
        ? [...new Set([...candidate.categories, category])]
        : candidate.categories.filter((item) => item !== category);
      return { ...candidate, categories };
    });
    postHostMessage({
      type: 'tags.manager.mapping.batch',
      tagIds: targetIds,
      category,
      isEnabled
    });
  }

  function toggleExplorerNavigation() {
    const wasActive = activeView === 'explorer';
    setView('explorer');
    explorerBookmarksExpanded = wasActive ? !explorerBookmarksExpanded : true;
    persistNavigationState();
  }

  function normalizeThemeColor(value: unknown, fallback: string) {
    const candidate = typeof value === 'string' ? value.trim() : '';
    return /^#[0-9a-f]{6}$/i.test(candidate) ? candidate.toLowerCase() : fallback;
  }

  function parseThemeSettings(value: unknown): ThemeSettings {
    const state = value && typeof value === 'object' ? value as Record<string, unknown> : {};
    const theme: ColorTheme = (state.theme ?? state.colorTheme) === 'light' ? 'light' : 'dark';
    return {
      theme,
      mainColor: normalizeThemeColor(state.mainColor ?? state.accentMain, defaultThemeSettings.mainColor),
      subColor: normalizeThemeColor(state.subColor ?? state.accentSub, defaultThemeSettings.subColor)
    };
  }

  function getThemeCssVariables(settings: ThemeSettings) {
    return `--accent-main: ${settings.mainColor}; --accent-sub: ${settings.subColor};`;
  }

  function applyThemeToDocument(settings: ThemeSettings) {
    document.documentElement.dataset.colorTheme = settings.theme;
    document.documentElement.style.setProperty('--accent-main', settings.mainColor);
    document.documentElement.style.setProperty('--accent-sub', settings.subColor);
  }

  function recommendThemeColors() {
    const recommendations = themeRecommendations[themeSettingsDraft.theme];
    const alternatives = recommendations.filter(recommendation =>
      recommendation.mainColor.toLowerCase() !== themeSettingsDraft.mainColor.toLowerCase() ||
      recommendation.subColor.toLowerCase() !== themeSettingsDraft.subColor.toLowerCase());
    const pool = alternatives.length > 0 ? alternatives : recommendations;
    const recommendation = pool[Math.floor(Math.random() * pool.length)];
    themeSettingsDraft = { ...themeSettingsDraft, ...recommendation };
  }

  function resetThemeSettingsDraft() {
    themeSettingsDraft = { ...appliedThemeSettings };
  }

  function applyDefaultThemeSettingsDraft() {
    themeSettingsDraft = { ...defaultThemeSettings };
  }

  function changeAppLanguage(event: Event) {
    const language = normalizeLanguage((event.currentTarget as HTMLSelectElement).value);
    appLanguage = language;
    applySystemLanguage(language);
    languageSavePending = true;
    postHostMessage({ type: 'settings.language.save', language });
  }

  async function requestThemeSettingsSave() {
    themeSaveConfirmOpen = true;
    await tick();
    themeSaveConfirmElement?.focus();
  }

  function confirmThemeSettingsSave() {
    themeSettingsDraft = {
      theme: themeSettingsDraft.theme,
      mainColor: normalizeThemeColor(themeSettingsDraft.mainColor, defaultThemeSettings.mainColor),
      subColor: normalizeThemeColor(themeSettingsDraft.subColor, defaultThemeSettings.subColor)
    };
    themeSaveConfirmOpen = false;
    themeSavePending = true;
    postHostMessage({
      type: 'settings.theme.save',
      theme: themeSettingsDraft.theme,
      mainColor: themeSettingsDraft.mainColor,
      subColor: themeSettingsDraft.subColor
    });
  }

  function persistNavigationState() {
    postHostMessage({
      type: 'ui.navigation.save',
      activeView,
      explorerBookmarksExpanded,
      explorerDetailColumns: explorerDetailColumns.join(','),
      mouseGestureSettings: JSON.stringify(mouseGestureSettings),
      keyboardShortcutSettings: JSON.stringify(keyboardShortcutSettings),
      galleryCardColumns: JSON.stringify(galleryCardColumnModes),
      galleryFilterSorts: JSON.stringify(galleryFilterSorts),
      galleryThumbnailSorts: JSON.stringify(galleryThumbnailSorts),
      creatorTrackingTabs: captureCreatorTrackingTabSessionState(),
      explorerCardColumns
    });
  }

  function selectFilterEditorAttribute(attribute: 'category' | 'title' | 'character') {
    filterEditorAttribute = attribute;
    filterEditorSearch = '';
    resetFilterEditorCsvPreview();
    if (attribute === 'category') {
      createFilterEditorCategory();
    }
    else {
      createFilterEditorDefinition(attribute);
    }
  }

  function createFilterEditorDefinition(attribute: 'title' | 'character' = filterEditorAttribute === 'character' ? 'character' : 'title') {
    filterEditorAttribute = attribute;
    filterEditorSelectedId = null;
    filterEditorSelectedValue = '';
    filterEditorCanonicalName = '';
    filterEditorStandardNameQuery = '';
    filterEditorStandardNameResults = [];
    filterEditorStandardNameSelected = '';
    filterEditorCategoryName = '';
    filterEditorParentTitleId = null;
    filterEditorParentTitle = '';
    const pendingAssignmentCategory = attribute === 'title'
      ? pendingGalleryTitleAssignmentCategory
      : pendingGalleryCharacterAssignmentCategory;
    filterEditorVisibleCategoriesDraft = pendingAssignmentCategory ? [pendingAssignmentCategory] : [];
    filterEditorAliases = '';
    filterEditorMergeCandidates = new Set();
    filterEditorMergeSearch = '';
  }

  function createFilterEditorCategory() {
    filterEditorAttribute = 'category';
    filterEditorSelectedCategoryId = null;
    filterEditorCategoryDraft = '';
    filterEditorSearch = '';
  }

  function selectFilterEditorCategory(category: FilterEditorCategory) {
    filterEditorAttribute = 'category';
    filterEditorSelectedCategoryId = category.id;
    filterEditorCategoryDraft = category.name;
  }

  function selectFilterEditorDefinition(definition: FilterEditorDefinition) {
    filterEditorAttribute = definition.filterType;
    filterEditorSelectedId = definition.id;
    filterEditorSelectedValue = definition.canonicalName;
    filterEditorCanonicalName = definition.canonicalName;
    filterEditorStandardNameQuery = definition.canonicalName;
    filterEditorStandardNameResults = [];
    filterEditorStandardNameSelected = '';
    filterEditorCategoryName = definition.categoryName === '未分類' ? '' : definition.categoryName;
    filterEditorParentTitleId = definition.parentTitleId;
    filterEditorParentTitle = definition.parentTitleName;
    filterEditorVisibleCategoriesDraft = [...definition.visibleCategories];
    filterEditorAliases = getEditableFilterAliases(definition);
    filterEditorMergeCandidates = new Set();
    filterEditorMergeSearch = '';
  }

  function selectedFilterEditorDefinition() {
    return filterEditorDefinitions.find((definition) => definition.id === filterEditorSelectedId) ?? null;
  }

  function saveFilterEditorCategory() {
    const name = filterEditorCategoryDraft.trim();
    if (!name) {
      showExplorerToast('Category名を入力してください。', 'error');
      return false;
    }
    postHostMessage({
      type: 'filters.editor.category.save',
      id: filterEditorSelectedCategoryId,
      name
    });
    return true;
  }

  function deleteFilterEditorCategory() {
    if (filterEditorSelectedCategoryId === null) {
      return;
    }
    const category = filterEditorCategories.find((item) => item.id === filterEditorSelectedCategoryId);
    if (!window.confirm(`Category「${category?.name ?? ''}」を削除します。所属フィルタは未分類になります。`)) {
      return;
    }
    postHostMessage({ type: 'filters.editor.category.delete', id: filterEditorSelectedCategoryId });
  }

  function reorderFilterEditorCategory(event: DragEvent, target: FilterEditorCategory) {
    event.preventDefault();
    if (!draggedFilterEditorCategory || draggedFilterEditorCategory.id === target.id) {
      return;
    }
    const next = [...filterEditorCategories];
    const from = next.findIndex((category) => category.id === draggedFilterEditorCategory?.id);
    const to = next.findIndex((category) => category.id === target.id);
    if (from < 0 || to < 0) {
      return;
    }
    const [moved] = next.splice(from, 1);
    next.splice(to, 0, moved);
    filterEditorCategories = next.map((category, position) => ({ ...category, position }));
    postHostMessage({ type: 'filters.editor.category.reorder', ids: filterEditorCategories.map((category) => category.id) });
  }

  function requestFilterEditorCsvPreview() {
    if (!filterEditorCsvReady) {
      return;
    }
    postHostMessage({
      type: filterEditorCsvScope === 'category'
        ? 'filters.editor.category.csv.preview'
        : 'filters.editor.csv.preview',
      hasHeader: filterEditorCsvHasHeader
    });
  }

  function importFilterEditorCsv() {
    if (!filterEditorCsvReady) {
      return;
    }
    const isCombinationImport = filterEditorCsvScope === 'combination';
    if (isCombinationImport) {
      const analysis = filterEditorCsvAnalysis;
      if (!analysis) {
        showExplorerToast('取り込み内容の確認結果を取得できていません。', 'error');
        return;
      }
      const message = [
        `${analysis.totalRows} 件を反映します。`,
        `自動統合するTitle: ${analysis.automaticTitleMergeCount} 件`,
        `移動または統合するCharacter: ${analysis.automaticCharacterMoveCount} 件`,
        `追加: ${analysis.addCount} 件（重複スキップ: ${analysis.duplicateAddSkipCount} 件）`,
        'この操作はデータベースへ即時反映されます。実行しますか？'
      ].join('\n');
      if (!window.confirm(message)) {
        return;
      }
    }
    postHostMessage({
      type: filterEditorCsvScope === 'category'
        ? 'filters.editor.category.csv.import'
        : 'filters.editor.csv.import',
      hasHeader: filterEditorCsvHasHeader,
      confirmed: isCombinationImport
    });
  }

  function resetFilterEditorCsvPreview() {
    filterEditorCsvFileName = '';
    filterEditorCsvHasHeader = false;
    filterEditorCsvSuggestedHeader = false;
    filterEditorCsvTotal = 0;
    filterEditorCsvPreview = [];
    filterEditorCsvReady = false;
    filterEditorCsvAnalysis = null;
    filterEditorCsvCaseKey = 'update';
  }

  function openFilterEditorStandardNameSearch() {
    const query = filterEditorStandardNameQuery.trim() || filterEditorCanonicalName.trim();
    if (!query) {
      showExplorerToast('検索する名称を入力してください。', 'error');
      return;
    }
    postHostMessage({ type: 'filters.editor.standardName.search', query });
  }

  function transferFilterEditorStandardName() {
    if (!filterEditorStandardNameSelected) {
      return;
    }
    filterEditorCanonicalName = filterEditorStandardNameSelected;
    filterEditorStandardNameQuery = filterEditorStandardNameSelected;
  }

  function syncFilterEditorParentTitle() {
    if (filterEditorParentTitleId !== null && !filterEditorParentTitleOptions.some((title) => title.id === filterEditorParentTitleId)) {
      filterEditorParentTitleId = null;
    }
  }

  function getAllocationRows(
    query: string,
    titles: FilterEditorDefinition[]
  ): AllocationRow[] {
    const normalizedQuery = query.trim().toLocaleLowerCase('ja-JP');
    return titles
      .map((definition) => ({ ...definition, titleName: definition.canonicalName }))
      .filter((row) => !normalizedQuery || [row.categoryName, row.titleName]
        .some(value => value.toLocaleLowerCase('ja-JP').includes(normalizedQuery)));
  }

  function groupAllocationRows(rows: AllocationRow[], sortColumn: string, direction: 'asc' | 'desc'): AllocationCategoryGroup[] {
    const groups = new Map<string, AllocationRow[]>();
    for (const row of rows) {
      const category = row.categoryName || '未分類';
      const current = groups.get(category) ?? [];
      current.push(row);
      groups.set(category, current);
    }
    const categoryOrder = new Map(filterEditorCategories.map((category, index) => [category.name, index]));
    return [...groups.entries()]
      .sort(([left], [right]) => (categoryOrder.get(left) ?? Number.MAX_SAFE_INTEGER) - (categoryOrder.get(right) ?? Number.MAX_SAFE_INTEGER) || left.localeCompare(right, 'ja-JP'))
      .map(([category, groupedRows]) => ({
        category,
        rows: [...groupedRows].sort((left, right) => compareAllocationRows(left, right, sortColumn, direction))
      }));
  }

  function getAllocationVirtualItems(
    groups: AllocationCategoryGroup[],
    expandedCategories: Record<string, boolean>
  ): AllocationVirtualItem[] {
    return groups.flatMap((group) => [
      { kind: 'category' as const, key: `category:${group.category}`, category: group.category },
      ...(expandedCategories[group.category]
        ? group.rows.map((row) => ({ kind: 'title' as const, key: `title:${row.id}`, row }))
        : [])
    ]);
  }

  function compareAllocationRows(left: AllocationRow, right: AllocationRow, column: string, direction: 'asc' | 'desc') {
    const leftValue = getAllocationSortValue(left, column);
    const rightValue = getAllocationSortValue(right, column);
    const result = typeof leftValue === 'boolean'
      ? Number(leftValue) - Number(rightValue)
      : String(leftValue).localeCompare(String(rightValue), 'ja-JP');
    return direction === 'asc' ? result : -result;
  }

  function getAllocationSortValue(row: AllocationRow, column: string): string | boolean {
    if (column === 'title') return row.titleName;
    return row.visibleCategories.includes(column);
  }

  function toggleAllocationSort(column: string) {
    if (allocationSortColumn === column) {
      allocationSortDirection = allocationSortDirection === 'asc' ? 'desc' : 'asc';
    }
    else {
      allocationSortColumn = column;
      allocationSortDirection = 'asc';
    }
  }

  function allocationSortLabel(column: string) {
    return allocationSortColumn === column ? (allocationSortDirection === 'asc' ? ' ↑' : ' ↓') : '';
  }

  function isAllocationGroupExpanded(category: string) {
    return allocationExpandedCategories[category] === true;
  }

  function toggleAllocationGroup(category: string) {
    flushSync(() => {
      allocationExpandedCategories = {
        ...allocationExpandedCategories,
        [category]: !allocationExpandedCategories[category]
      };
    });
  }

  function openAllocationDefinition(event: MouseEvent, row: AllocationRow) {
    event.preventDefault();
    event.stopPropagation();
    const definition = row.filterType === 'title'
      ? row
      : filterEditorTitles.find((candidate) => candidate.id === row.parentTitleId);
    if (!definition) {
      return;
    }

    filterEditorView = 'filter';
    selectFilterEditorDefinition(definition);
    requestAnimationFrame(() => {
      document.querySelector<HTMLButtonElement>('.filter-editor-option-list button[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
    });
  }

  function openRegisteredCharacterDefinition(event: MouseEvent, character: FilterEditorDefinition) {
    event.preventDefault();
    event.stopPropagation();
    selectFilterEditorDefinition(character);
    requestAnimationFrame(() => {
      document.querySelector<HTMLButtonElement>('.filter-editor-option-list button[aria-selected="true"]')?.scrollIntoView({ block: 'nearest' });
    });
  }

  function clearAllocationSelectionFromBackground(event: MouseEvent) {
    const target = event.target as Element | null;
    if (target?.closest('.filter-editor-allocation-row, button, input, label')) {
      return;
    }

    allocationSelectedIds.clear();
    allocationSelectionAnchorId = null;
    syncAllocationSelectionVisuals();
  }

  function clearAllocationSelectionOnBackground(node: HTMLElement) {
    const handleClick = (event: MouseEvent) => clearAllocationSelectionFromBackground(event);
    node.addEventListener('click', handleClick);
    return {
      destroy() {
        node.removeEventListener('click', handleClick);
      }
    };
  }

  function syncAllocationSelectionVisuals() {
    document.querySelectorAll<HTMLElement>('.filter-editor-allocation-row[data-allocation-row-id]').forEach((element) => {
      const id = Number(element.dataset.allocationRowId);
      element.classList.toggle('selected', allocationSelectedIds.has(id));
    });
  }

  function applyAllocationRowSelection(node: HTMLElement, rowId: number) {
    node.classList.toggle('selected', allocationSelectedIds.has(rowId));
    return {
      update(nextRowId: number) {
        node.classList.toggle('selected', allocationSelectedIds.has(nextRowId));
      }
    };
  }

  function selectAllocationRow(event: MouseEvent, row: AllocationRow) {
    event.stopPropagation();
    const next = allocationSelectedIds;
    const currentRows = allocationVisibleRows;
    const currentIndex = allocationRowIndexById.get(row.id) ?? -1;
    if (event.shiftKey && allocationSelectionAnchorId !== null) {
      const anchorIndex = allocationRowIndexById.get(allocationSelectionAnchorId) ?? -1;
      if (anchorIndex >= 0 && currentIndex >= 0) {
        next.clear();
        const [start, end] = anchorIndex < currentIndex ? [anchorIndex, currentIndex] : [currentIndex, anchorIndex];
        currentRows.slice(start, end + 1).forEach((candidate) => next.add(candidate.id));
      }
    }
    else if (event.ctrlKey || event.metaKey) {
      if (next.has(row.id)) next.delete(row.id);
      else next.add(row.id);
      allocationSelectionAnchorId = row.id;
    }
    else {
      if (next.size === 1 && next.has(row.id)) {
        next.clear();
        allocationSelectionAnchorId = null;
      }
      else {
        next.clear();
        next.add(row.id);
        allocationSelectionAnchorId = row.id;
      }
    }
    syncAllocationSelectionVisuals();
  }

  function applyAllocationVisibility(row: AllocationRow, category: string, isVisible: boolean) {
    const selectedTitleIds = allocationSelectedIds.size > 1
      ? [...allocationSelectedIds]
      : [row.id];
    const targetIds = [...new Set([
      ...selectedTitleIds,
      ...filterEditorCharacters
        .filter((character) => character.parentTitleId !== null && selectedTitleIds.includes(character.parentTitleId))
        .map((character) => character.id)
    ])];
    const updateVisibility = (definition: FilterEditorDefinition): FilterEditorDefinition => {
      if (!targetIds.includes(definition.id)) return definition;
      const visibleCategories = isVisible
        ? [...new Set([...definition.visibleCategories, category])]
        : definition.visibleCategories.filter((item) => item !== category);
      return { ...definition, visibleCategories };
    };
    filterEditorTitles = filterEditorTitles.map(updateVisibility);
    filterEditorCharacters = filterEditorCharacters.map(updateVisibility);
    postHostMessage({
      type: 'filters.editor.visibility.batch',
      filterIds: targetIds,
      category,
      isVisible
    });
  }

  function getFilterEditorVisibleCategories(definition: FilterEditorDefinition) {
    return definition.visibleCategories;
  }

  function isFilterEditorVisibleInCategory(definition: FilterEditorDefinition | null, category: string) {
    return definition?.visibleCategories.includes(category) ?? false;
  }

  function getFilterEditorCategory(definition: FilterEditorDefinition) {
    return definition.categoryName || '未分類';
  }

  function getFilterEditorParentTitle(definition: FilterEditorDefinition) {
    return definition.parentTitleName || '未設定';
  }

  function toggleFilterEditorVisibility(definition: FilterEditorDefinition, category: string) {
    const nextVisibleCategories = definition.visibleCategories.includes(category)
      ? definition.visibleCategories.filter((item) => item !== category)
      : [...definition.visibleCategories, category];
    const targetIds = definition.filterType === 'title'
      ? [
        definition.id,
        ...filterEditorCharacters
          .filter((character) => character.parentTitleId === definition.id)
          .map((character) => character.id)
      ]
      : [definition.id];
    const updateVisibility = (candidate: FilterEditorDefinition): FilterEditorDefinition =>
      targetIds.includes(candidate.id)
        ? { ...candidate, visibleCategories: nextVisibleCategories }
        : candidate;

    filterEditorTitles = filterEditorTitles.map(updateVisibility);
    filterEditorCharacters = filterEditorCharacters.map(updateVisibility);
    postHostMessage({
      type: 'filters.editor.visibility.batch',
      filterIds: targetIds,
      category,
      isVisible: nextVisibleCategories.includes(category)
    });
  }

  function toggleFilterEditorDraftVisibility(category: string) {
    filterEditorVisibleCategoriesDraft = filterEditorVisibleCategoriesDraft.includes(category)
      ? filterEditorVisibleCategoriesDraft.filter((item) => item !== category)
      : [...filterEditorVisibleCategoriesDraft, category];
  }

  function replaceFilterEditorDefinition(nextDefinition: FilterEditorDefinition) {
    if (nextDefinition.filterType === 'title') {
      filterEditorTitles = filterEditorTitles.map((definition) => definition.id === nextDefinition.id ? nextDefinition : definition);
    }
    else {
      filterEditorCharacters = filterEditorCharacters.map((definition) => definition.id === nextDefinition.id ? nextDefinition : definition);
    }
  }

  function saveFilterEditorDefinition(
    definition = selectedFilterEditorDefinition(),
    visibleCategories = definition?.visibleCategories ?? filterEditorVisibleCategoriesDraft
  ) {
    const isCurrentEditor = definition?.id === filterEditorSelectedId || definition === null;
    const aliases = isCurrentEditor
      ? filterEditorAliases.split(/\r?\n/).map((value) => value.trim()).filter(Boolean)
      : definition?.aliases ?? [];
    const canonicalName = isCurrentEditor
      ? filterEditorCanonicalName.trim()
      : definition?.canonicalName ?? '';
    if (!canonicalName) {
      showExplorerToast('表示名を入力してください。', 'error');
      return false;
    }
    const filterType = definition?.filterType ?? (filterEditorAttribute === 'character' ? 'character' : 'title');
    const parentTitleId = filterType === 'character'
      ? (isCurrentEditor ? filterEditorParentTitleId : definition?.parentTitleId ?? null)
      : null;
    const inheritedVisibleCategories = filterType === 'character' && parentTitleId !== null
      ? filterEditorTitles.find((title) => title.id === parentTitleId)?.visibleCategories ?? visibleCategories
      : visibleCategories;
    const requestedVisibleCategories = inheritedVisibleCategories;
    postHostMessage({
      type: 'filters.editor.save',
      id: definition?.id ?? null,
      filterType,
      canonicalName,
      categoryName: isCurrentEditor ? filterEditorCategoryName : definition?.categoryName ?? '',
      parentTitleId,
      aliases,
      visibleCategories: requestedVisibleCategories
    });
    return true;
  }

  function requestFilterEditorDefinitionDeletion() {
    const definition = selectedFilterEditorDefinition();
    if (!definition) {
      return;
    }
    pendingFilterEditorDefinitionDeletion = definition;
  }

  function deleteFilterEditorDefinition() {
    const definition = pendingFilterEditorDefinitionDeletion;
    if (!definition) {
      return;
    }
    postHostMessage({
      type: 'filters.editor.delete',
      id: definition.id,
      filterType: definition.filterType
    });
  }

  function getFilterEditorCharacterCounts(characters: FilterEditorDefinition[]) {
    const counts = new Map<number, number>();
    for (const character of characters) {
      if (character.parentTitleId !== null) {
        counts.set(character.parentTitleId, (counts.get(character.parentTitleId) ?? 0) + 1);
      }
    }
    return counts;
  }

  function getTitleCharacterCount(title: FilterEditorDefinition) {
    return filterEditorCharacterCounts.get(title.id) ?? 0;
  }

  function toggleFilterEditorTitleCharacterSort() {
    if (filterEditorTitleSort !== 'characterCount') {
      filterEditorTitleSort = 'characterCount';
      filterEditorTitleSortDirection = 'desc';
      return;
    }

    filterEditorTitleSortDirection = filterEditorTitleSortDirection === 'desc' ? 'asc' : 'desc';
  }

  function toggleFilterEditorMergeCandidate(id: number) {
    const next = new Set(filterEditorMergeCandidates);
    if (next.has(id)) {
      next.delete(id);
    }
    else {
      next.add(id);
    }
    filterEditorMergeCandidates = next;
  }

  function mergeFilterEditorDefinitions() {
    if (filterEditorSelectedId === null || filterEditorMergeCandidates.size === 0) {
      return;
    }
    postHostMessage({
      type: 'filters.editor.merge',
      filterType: filterEditorAttribute,
      targetFilterId: filterEditorSelectedId,
      sourceFilterIds: [...filterEditorMergeCandidates]
    });
  }

  function getFilterEditorOptions(
    definitions: FilterEditorDefinition[],
    query: string,
    selectedCategories: readonly string[],
    characterCounts: ReadonlyMap<number, number>,
    isTitleList: boolean,
    titleSort: 'name' | 'characterCount',
    titleSortDirection: 'asc' | 'desc'
  ) {
    const normalizedQuery = query.toLocaleLowerCase('ja-JP');
    const categoryFilter = new Set(selectedCategories);
    const options = definitions
      .filter((definition) => {
        const category = getFilterEditorCategory(definition);
        const matchesQuery = definition.canonicalName.toLocaleLowerCase('ja-JP').includes(normalizedQuery) ||
          category.toLocaleLowerCase('ja-JP').includes(normalizedQuery);
        return matchesQuery && (categoryFilter.size === 0 || categoryFilter.has(category));
      });
    if (isTitleList && titleSort === 'characterCount') {
      return [...options].sort((left, right) => {
        const difference = (characterCounts.get(left.id) ?? 0) - (characterCounts.get(right.id) ?? 0);
        if (difference !== 0) {
          return titleSortDirection === 'asc' ? difference : -difference;
        }
        return left.canonicalName.localeCompare(right.canonicalName, 'ja-JP');
      });
    }
    return options;
  }

  function getFilterEditorCategoryFilterOptions(
    definitions: FilterEditorDefinition[],
    categories: FilterEditorCategory[]
  ) {
    return [...new Set([
      ...categories.map((category) => category.name),
      ...definitions.map((definition) => getFilterEditorCategory(definition))
    ])].sort((left, right) => left.localeCompare(right, 'ja-JP'));
  }

  function toggleFilterEditorCategoryFilter(category: string) {
    filterEditorCategoryFilters = filterEditorCategoryFilters.includes(category)
      ? filterEditorCategoryFilters.filter((value) => value !== category)
      : [...filterEditorCategoryFilters, category];
  }

  function clearFilterEditorCategoryFilters() {
    filterEditorCategoryFilters = [];
  }

  function groupFilterEditorOptions(options: FilterEditorDefinition[]) {
    const groups = new Map<string, FilterEditorDefinition[]>();
    for (const option of options) {
      const category = getFilterEditorCategory(option);
      const values = groups.get(category) ?? [];
      values.push(option);
      groups.set(category, values);
    }
    return [...groups.entries()]
      .sort(([left], [right]) => left.localeCompare(right, 'ja-JP'))
      .map(([category, options]) => ({ category, options }));
  }

  function getFilterEditorMergeOptions(
    options: FilterEditorDefinition[],
    selectedId: number | null,
    selectedName: string,
    search: string
  ) {
    const normalizedSearch = search.trim().toLocaleLowerCase('ja-JP');
    const normalizedSelected = selectedName.replace(/[\s\-_・]/g, '').toLocaleLowerCase('ja-JP');
    return options
      .filter((option) => option.id !== selectedId)
      .filter((option) => {
        const normalizedOption = option.canonicalName.replace(/[\s\-_・]/g, '').toLocaleLowerCase('ja-JP');
        if (normalizedSearch) {
          return normalizedOption.includes(normalizedSearch);
        }
        return normalizedSelected.length >= 2 &&
          (normalizedOption.includes(normalizedSelected) || normalizedSelected.includes(normalizedOption));
      })
      .slice(0, 80);
  }

  function parseGalleryCardColumnModes(value: unknown): Record<string, number> {
    const defaults: Record<string, number> = Object.fromEntries(
      gallerySections.map((section) => [section.id, 7])
    );

    if (typeof value !== 'string' || !value.trim()) {
      return defaults;
    }

    try {
      const parsed = JSON.parse(value) as Record<string, unknown>;
      for (const section of gallerySections) {
        const columns = Number(parsed[section.id]);
        if ([5, 6, 7, 8, 9].includes(columns)) {
          defaults[section.id] = columns;
        }
      }
    }
    catch {
      // The current defaults remain valid when the stored state is from an older build.
    }

    return defaults;
  }

  function parseGalleryFilterSortState(value: unknown): GalleryFilterSortCriterion[] {
    if (typeof value !== 'string' || !value.trim()) return [];
    try {
      const parsed = JSON.parse(value) as Array<Partial<GalleryFilterSortCriterion>>;
      const validKeys = new Set<GalleryFilterSortKey>(['rating', 'files', 'name']);
      return Array.isArray(parsed)
        ? parsed
            .filter((item): item is GalleryFilterSortCriterion =>
              validKeys.has(item.key as GalleryFilterSortKey) && (item.direction === 'asc' || item.direction === 'desc'))
            .slice(0, 3)
        : [];
    }
    catch {
      return [];
    }
  }

  function parseGalleryThumbnailSortState(value: unknown): GalleryThumbnailSortCriterion[] {
    if (typeof value !== 'string' || !value.trim()) return [];
    try {
      const parsed = JSON.parse(value) as Array<Partial<GalleryThumbnailSortCriterion>>;
      const validKeys = new Set<GalleryThumbnailSortKey>(['rating', 'images', 'accessed', 'path']);
      return Array.isArray(parsed)
        ? parsed
            .filter((item): item is GalleryThumbnailSortCriterion =>
              validKeys.has(item.key as GalleryThumbnailSortKey) && (item.direction === 'asc' || item.direction === 'desc'))
            .slice(0, 4)
        : [];
    }
    catch {
      return [];
    }
  }

  function setGalleryCardColumns(columns: number, section = gallerySection) {
    const normalizedColumns = Math.min(9, Math.max(5, columns));
    galleryCardColumnModes = { ...galleryCardColumnModes, [section]: normalizedColumns };
    persistNavigationState();
  }

  function loadExplorer(path = explorerPathDraft, restoreScroll = false) {
    if (isExplorerFolderUpdateLocked()) {
      return;
    }

    if (restoreScroll) {
      // The caller captured the previous tab before changing the active tab.
    }
    else if (path === explorerPath && activeExplorerTabId) {
      saveExplorerTabScroll();
      pendingExplorerScrollRestoreTabId = activeExplorerTabId;
    }
    else {
      pendingExplorerScrollRestoreTabId = null;
    }
    explorerIsLoading = true;
    explorerThumbnailPriority += 1;
    postHostMessage({ type: 'explorer.list', path });
  }

  function saveExplorerTabScroll() {
    if (!activeExplorerTabId || !explorerGridPaneElement || !explorerDetailPaneElement) {
      return;
    }

    explorerTabScrollPositions = {
      ...explorerTabScrollPositions,
      [activeExplorerTabId]: {
        gridTop: explorerGridPaneElement.scrollTop,
        detailTop: explorerDetailPaneElement.scrollTop
      }
    };
  }

  function restoreExplorerTabScroll() {
    const tabId = pendingExplorerScrollRestoreTabId;
    if (!tabId || tabId !== activeExplorerTabId) {
      return;
    }

    const position = explorerTabScrollPositions[tabId] ?? { gridTop: 0, detailTop: 0 };
    requestAnimationFrame(() => {
      if (tabId !== activeExplorerTabId) {
        return;
      }

      if (explorerGridPaneElement) {
        explorerGridPaneElement.scrollTop = position.gridTop;
      }
      if (explorerDetailPaneElement) {
        explorerDetailPaneElement.scrollTop = position.detailTop;
      }
      pendingExplorerScrollRestoreTabId = null;
    });
  }

  async function restoreExplorerSplitScroll() {
    if (!pendingExplorerSplitScroll || !explorerSplit || explorerIsLoading || explorerSplit.rightIsLoading) return;
    const position = pendingExplorerSplitScroll;
    pendingExplorerSplitScroll = null;
    await tick();
    requestAnimationFrame(() => {
      if (explorerSplitLeftPaneElement) explorerSplitLeftPaneElement.scrollTop = position.leftTop;
      if (explorerSplitRightPaneElement) explorerSplitRightPaneElement.scrollTop = position.rightTop;
    });
  }

  function enterExplorerSplit(event: MouseEvent, rightTab: ExplorerTab) {
    event.preventDefault();
    startExplorerSplit(rightTab);
  }

  function startExplorerSplit(rightTab: ExplorerTab) {
    const leftTab = explorerTabs.find((tab) => tab.id === activeExplorerTabId);
    if (!leftTab || leftTab.id === rightTab.id) {
      return;
    }

    explorerSplit = {
      leftTabId: leftTab.id,
      rightTabId: rightTab.id,
      rightPath: rightTab.path,
      rightParentPath: null,
      rightEntries: [],
      rightSelectedPaths: [],
      rightIsLoading: true,
      rightIsTruncated: false
    };
    splitFocusedPane = 'left';
    loadSplitExplorer(rightTab.path);
  }

  function splitExplorerFromTabContextMenu() {
    const tab = explorerTabContextMenu?.tab;
    explorerTabContextMenu = null;
    if (tab) startExplorerSplit(tab);
  }

  function getExplorerCreatorContextFromTab(tab: ExplorerTab) {
    const normalizedPath = normalizeWindowsPath(tab.path);
    const matches = [...normalizedPath.matchAll(/(?:^|\\)(【([^\\]+)】)(?=\\|$)/g)];
    const nearest = matches.at(-1);
    if (!nearest || nearest.index === undefined) return null;
    return {
      creator: nearest[2].trim(),
      creatorFolder: normalizedPath.slice(0, nearest.index + nearest[0].length)
    };
  }

  function getGalleryCategoryForExplorerPath(path: string) {
    const normalizedPath = normalizeWindowsPath(path);
    return [...galleryScanTargets]
      .sort((left, right) => right.path.length - left.path.length)
      .find(target => {
        const normalizedTarget = normalizeWindowsPath(target.path);
        if (!normalizedTarget) return false;
        const targetPrefix = normalizedTarget.endsWith('\\') ? normalizedTarget : `${normalizedTarget}\\`;
        return normalizedPath === normalizedTarget || normalizedPath.startsWith(targetPrefix);
      })?.category ?? '';
  }

  function openExplorerTabContextMenu(event: MouseEvent, tab: ExplorerTab) {
    const creatorContext = getExplorerCreatorContextFromTab(tab);
    if (!creatorContext?.creator) {
      enterExplorerSplit(event, tab);
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    explorerTabContextMenu = {
      tab,
      creator: creatorContext.creator,
      creatorFolder: creatorContext.creatorFolder,
      category: getGalleryCategoryForExplorerPath(tab.path),
      x: Math.min(event.clientX, window.innerWidth - 284),
      y: Math.min(event.clientY, window.innerHeight - 128)
    };
  }

  function navigateExplorerCreatorTabToGallery() {
    const context = explorerTabContextMenu;
    explorerTabContextMenu = null;
    if (!context?.category) {
      showExplorerToast('この作者フォルダはSettings > Appearance > 区分別の設定にある対象ディレクトリ配下ではありません。', 'error');
      return;
    }

    openGalleryForCreator(context.category, context.creator);
  }

  function openGalleryForCreator(category: string, creator: string) {
    gallerySection = category;
    loadGalleryPins(category);
    galleryRatingFilters = [];
    galleryTagFilters = [];
    galleryCreatorFilters = [creator];
    galleryTitleFilters = [];
    galleryCharacterFilters = [];
    galleryQuery = '';
    galleryCreatorFiltersExpanded = false;
    galleryTitleFiltersExpanded = false;
    galleryCharacterFiltersExpanded = false;
    galleryTagFiltersExpanded = false;
    galleryPromotedCreators = [creator];
    galleryPromotedTitles = [];
    galleryPromotedCharacters = [];
    galleryPromotedTags = [];
    selectedGalleryWorkIds = new Set();
    galleryWorks = [];
    galleryTotal = 0;
    activateView('library');
    loadGalleryWorks(false, galleryRatingFilters, true);
  }

  function navigateCreatorTrackingToGallery() {
    const activeTab = creatorTrackingTabs.find(tab => tab.id === activeCreatorTrackingTabId);
    const creator = creatorTracking?.creator?.trim() || activeTab?.creator?.trim() || '';
    const category = creatorTrackingSummary?.category?.trim() || activeTab?.summary?.category?.trim() || '';
    if (!creator || !category) {
      showExplorerToast('Galleryへ移動する作者または区分を特定できませんでした。', 'error');
      return;
    }
    saveCreatorTracking();
    openGalleryForCreator(category, creator);
  }

  function navigateExplorerCreatorTabToTracking() {
    const context = explorerTabContextMenu;
    explorerTabContextMenu = null;
    if (!context?.category) {
      showExplorerToast('この作者フォルダはSettings > Appearance > 区分別の設定にある対象ディレクトリ配下ではありません。', 'error');
      return;
    }

    galleryCreatorSummarySection = context.category;
    const summary = galleryCreatorSummaries.find(item =>
      item.category === context.category &&
      item.creator.trim().localeCompare(context.creator, 'ja-JP', { sensitivity: 'base' }) === 0);
    if (summary) {
      openCreatorTrackingForSummary(summary);
      return;
    }
    pendingGalleryCreatorTracking = { creator: context.creator, category: context.category, creatorFolder: context.creatorFolder };
    loadGalleryCreatorSummaries();
    showExplorerToast(`Creator「${context.creator}」の情報を読み込んでいます。`, 'progress');
  }

  function exitExplorerSplit() {
    explorerSplit = null;
    splitFocusedPane = 'left';
  }

  function loadSplitExplorer(path: string) {
    if (!explorerSplit || isExplorerFolderUpdateLocked()) {
      return;
    }

    explorerSplit = { ...explorerSplit, rightIsLoading: true };
    explorerThumbnailPriority += 1;
    postHostMessage({ type: 'explorer.list', path, pane: 'split-right' });
  }

  function navigateSplitParent() {
    if (explorerSplit?.rightParentPath) {
      loadSplitExplorer(explorerSplit.rightParentPath);
    }
  }

  function rememberExplorerHistory(path: string, pane: 'left' | 'right') {
    if (!path) {
      return;
    }

    const paths = pane === 'left' ? explorerHistoryPaths : splitExplorerHistoryPaths;
    const index = pane === 'left' ? explorerHistoryIndex : splitExplorerHistoryIndex;
    if (paths[index] === path) {
      return;
    }

    const nextPaths = [...paths.slice(0, index + 1), path];
    if (pane === 'left') {
      explorerHistoryPaths = nextPaths;
      explorerHistoryIndex = nextPaths.length - 1;
    }
    else {
      splitExplorerHistoryPaths = nextPaths;
      splitExplorerHistoryIndex = nextPaths.length - 1;
    }
  }

  function navigateExplorerHistory(direction: -1 | 1, pane: 'left' | 'right') {
    const paths = pane === 'left' ? explorerHistoryPaths : splitExplorerHistoryPaths;
    const index = pane === 'left' ? explorerHistoryIndex : splitExplorerHistoryIndex;
    const targetIndex = index + direction;
    const targetPath = paths[targetIndex];
    if (!targetPath) {
      return;
    }

    if (pane === 'left') {
      explorerHistoryIndex = targetIndex;
      loadExplorer(targetPath);
    }
    else {
      splitExplorerHistoryIndex = targetIndex;
      loadSplitExplorer(targetPath);
    }
  }

  function executeMouseGesture(deltaX: number, deltaY: number, pane: 'left' | 'right') {
    if (Math.max(Math.abs(deltaX), Math.abs(deltaY)) < mouseGestureSettings.threshold) {
      return false;
    }

    const gesture = Math.abs(deltaY) >= Math.abs(deltaX)
      ? deltaY < 0 ? '↑' : '↓'
      : deltaX < 0 ? '←' : '→';
    const command = getMouseGestureCommand(gesture);
    if (command === 'none') {
      return false;
    }

    if (command === 'parent') {
      pane === 'left' ? navigateToParent() : navigateSplitParent();
    }
    else if (command === 'back') {
      navigateAppHistory(-1);
    }
    else if (command === 'forward') {
      navigateAppHistory(1);
    }
    else if (command === 'clearFilter') {
      if (pane === 'left') {
        explorerQuery = '';
      }
      else {
        splitExplorerQuery = '';
      }
    }
    else if (command === 'refresh') {
      if (pane === 'left') {
        loadExplorer(explorerPath);
      }
      else if (explorerSplit) {
        loadSplitExplorer(explorerSplit.rightPath);
      }
    }
    else if (command === 'copyPath') {
      const path = pane === 'left' ? explorerPath : explorerSplit?.rightPath;
      if (path) {
        postHostMessage({ type: 'explorer.path.copy', path });
      }
    }

    return true;
  }

  function selectSplitEntry(event: MouseEvent, entry: ExplorerEntry) {
    if (!explorerSplit) {
      return;
    }

    focusSplitPane('right');
    if (event.shiftKey && splitSelectionAnchorPath) {
      const range = getSelectionRange(splitRightEntries, splitSelectionAnchorPath, entry.path);
      explorerSplit = { ...explorerSplit, rightSelectedPaths: range.length > 0 ? range : [entry.path] };
      return;
    }

    const selected = event.ctrlKey || event.metaKey
      ? explorerSplit.rightSelectedPaths.includes(entry.path)
        ? explorerSplit.rightSelectedPaths.filter((path) => path !== entry.path)
        : [...explorerSplit.rightSelectedPaths, entry.path]
      : [entry.path];
    explorerSplit = { ...explorerSplit, rightSelectedPaths: selected };
    splitSelectionAnchorPath = entry.path;
    if (!event.ctrlKey && !event.metaKey && !event.shiftKey && hasExplorerLaunchRule(entry, 'single')) {
      launchExplorerEntry(entry, 'single');
    }
  }

  function focusSplitPane(pane: 'left' | 'right') {
    splitFocusedPane = pane;
  }

  function updateActiveExplorerQuery(event: Event) {
    const query = (event.currentTarget as HTMLInputElement).value;
    if (explorerSplit && splitFocusedPane === 'right') {
      splitExplorerQuery = query;
      return;
    }

    explorerQuery = query;
  }

  function focusExplorerFilter() {
    requestAnimationFrame(() => explorerFilterInputElement?.focus());
  }

  function beginExplorerPathEdit() {
    explorerDriveMenuOpen = false;
    explorerPathEditing = true;
    requestAnimationFrame(() => {
      explorerPathInputElement?.focus();
      explorerPathInputElement?.select();
    });
  }

  function openExplorerBreadcrumb(path: string) {
    explorerDriveMenuOpen = false;
    explorerPathEditing = false;
    loadExplorer(path);
  }

  function toggleExplorerDriveMenu(event: MouseEvent) {
    event.preventDefault();
    event.stopPropagation();
    if (!explorerDriveMenuOpen) {
      const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect();
      explorerDriveMenuPosition = { x: bounds.left, y: bounds.bottom + 4 };
    }
    explorerDriveMenuOpen = !explorerDriveMenuOpen;
  }

  function openExplorerDrive(root: string) {
    explorerDriveMenuOpen = false;
    explorerPathEditing = false;
    loadExplorer(root);
  }

  function getExplorerDriveLabel(root: string) {
    const normalized = root.replace(/\//g, '\\');
    return /^[a-z]:/i.test(normalized) ? normalized.slice(0, 2).toUpperCase() : normalized;
  }

  function openSplitEntry(entry: ExplorerEntry, activation: 'single' | 'double' = 'double') {
    if (entry.isDirectory) {
      if (activation === 'double') {
        loadSplitExplorer(entry.path);
      }
      return;
    }

    launchExplorerEntry(entry, activation);
  }

  function beginSplitGesture(event: PointerEvent) {
    focusSplitPane('right');
    if (mouseGestureSettings.enabled && event.button === 2) {
      splitGestureStart = { x: event.clientX, y: event.clientY };
      gestureTrail = [{ x: event.clientX, y: event.clientY }];
    }
  }

  function updateSplitGesture(event: PointerEvent) {
    if (splitGestureStart && (event.buttons & 2) === 2) {
      appendGesturePoint(event);
    }
  }

  function finishSplitGesture(event: PointerEvent) {
    if (!splitGestureStart || event.button !== 2) {
      return;
    }

    const deltaX = event.clientX - splitGestureStart.x;
    const deltaY = event.clientY - splitGestureStart.y;
    splitGestureStart = null;
    if (executeMouseGesture(deltaX, deltaY, 'right')) {
      suppressSplitContextMenu = true;
    }
    clearGestureTrail();
  }

  function suppressSplitBlankContextMenu(event: MouseEvent) {
    event.preventDefault();
    suppressSplitContextMenu = false;
  }

  function toggleExplorerSort(sort: string) {
    if (explorerSort === sort) {
      explorerSortDirection = explorerSortDirection === 'asc' ? 'desc' : 'asc';
    }
    else {
      explorerSort = sort;
      explorerSortDirection = sort === 'modified' ? 'desc' : 'asc';
    }
  }

  function openExplorerContextMenu(event: MouseEvent, entry: ExplorerEntry, pane: 'left' | 'right') {
    event.preventDefault();
    event.stopPropagation();
    if (pane === 'left' && suppressExplorerContextMenu) {
      suppressExplorerContextMenu = false;
      return;
    }

    if (pane === 'right' && suppressSplitContextMenu) {
      suppressSplitContextMenu = false;
      return;
    }

    if (pane === 'right' && explorerSplit) {
      focusSplitPane('right');
      if (!explorerSplit.rightSelectedPaths.includes(entry.path)) {
        explorerSplit = { ...explorerSplit, rightSelectedPaths: [entry.path] };
      }
    }
    else {
      focusSplitPane('left');
      if (!selectedPaths.includes(entry.path)) {
        selectedPaths = [entry.path];
      }
    }

    explorerContextMenu = {
      entry,
      pane,
      x: Math.max(8, Math.min(event.clientX, window.innerWidth - (entry.isDirectory ? 740 : isWinRarArchive(entry) ? 550 : 276))),
      y: Math.min(event.clientY, window.innerHeight - (entry.isDirectory ? 196 : 156))
    };
    explorerThumbnailSubmenuOpen = false;
    explorerCompressionSubmenuOpen = false;
    explorerFolderCreateSubmenuOpen = false;
    explorerDbManagementSubmenuOpen = false;
    explorerGidSubmenuOpen = false;
    explorerColumnMenu = null;
    explorerBlankContextMenu = null;
  }

  function prioritizeExplorerContextMenu(event: PointerEvent, entry: ExplorerEntry, pane: 'left' | 'right') {
    if (event.button !== 2) {
      return;
    }

    openExplorerContextMenu(event, entry, pane);
  }

  function startExplorerEntryDrag(event: DragEvent, entry: ExplorerEntry, pane: 'left' | 'right') {
    const selected = pane === 'left'
      ? selectedPaths
      : explorerSplit?.rightSelectedPaths ?? [];
    const paths = selected.includes(entry.path) ? selected : [entry.path];
    const sourcePath = pane === 'left' ? explorerPath : explorerSplit?.rightPath ?? '';
    if (paths.length === 0 || !sourcePath) {
      return;
    }

    draggedExplorerEntries = { paths, sourcePane: pane, sourcePath };
    event.dataTransfer?.setData('application/x-gallerybrowser-paths', JSON.stringify(paths));
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'copyMove';
    }
  }

  function updateExplorerDropTarget(event: DragEvent, entry: ExplorerEntry) {
    if (!entry.isDirectory || !draggedExplorerEntries) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    explorerDropTargetPath = entry.path;
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = event.ctrlKey ? 'copy' : 'move';
    }
  }

  function clearExplorerDropTarget() {
    explorerDropTargetPath = '';
  }

  function dropExplorerEntries(event: DragEvent, destination: ExplorerEntry, pane: 'left' | 'right') {
    event.preventDefault();
    event.stopPropagation();
    if (isExplorerFolderUpdateLocked()) {
      return;
    }

    const drag = draggedExplorerEntries;
    clearExplorerDropTarget();
    draggedExplorerEntries = null;
    if (!drag || !destination.isDirectory) {
      return;
    }

    const copy = event.ctrlKey;
    if (pane === 'left') {
      explorerIsLoading = true;
    }
    else if (explorerSplit) {
      explorerSplit = { ...explorerSplit, rightIsLoading: true };
    }

    pendingMoveRefresh = copy
      ? null
      : {
          sourcePane: drag.sourcePane,
          sourcePath: drag.sourcePath,
          destinationPane: pane,
          destinationPath: destination.path
        };
    postHostMessage({
      type: 'explorer.dragdrop',
      paths: drag.paths,
      destination: destination.path,
      copy,
      pane: pane === 'right' ? 'split-right' : undefined
    });
  }

  function openExplorerBlankContextMenu(event: MouseEvent, pane: 'left' | 'right') {
    if (!(event.target as Element).closest('.explorer-grid-pane, .split-grid-pane')) {
      return;
    }

    event.preventDefault();
    if (pane === 'left' && suppressExplorerContextMenu) {
      suppressExplorerContextMenu = false;
      return;
    }
    if (pane === 'right' && suppressSplitContextMenu) {
      suppressSplitContextMenu = false;
      return;
    }

    explorerContextMenu = null;
    explorerColumnMenu = null;
    explorerFolderCreateSubmenuOpen = false;
    explorerBlankContextMenu = {
      pane,
      x: Math.max(8, Math.min(event.clientX, window.innerWidth - 220)),
      y: Math.max(8, Math.min(event.clientY, window.innerHeight - 60))
    };
  }

  function createNewExplorerFolder(pane: 'left' | 'right') {
    const path = pane === 'left' ? explorerPath : explorerSplit?.rightPath;
    explorerBlankContextMenu = null;
    if (!path || isExplorerFolderUpdateLocked()) {
      return;
    }

    if (pane === 'left') {
      explorerIsLoading = true;
    }
    else if (explorerSplit) {
      explorerSplit = { ...explorerSplit, rightIsLoading: true };
    }
    postHostMessage({ type: 'explorer.folder.create', path, pane: pane === 'right' ? 'split-right' : undefined });
  }

  function getExplorerSelectionForPane(pane: 'left' | 'right') {
    return pane === 'right'
      ? explorerSplit?.rightSelectedPaths ?? []
      : selectedPaths;
  }

  function getSelectedExplorerFoldersForPane(pane: 'left' | 'right') {
    const selected = new Set(getExplorerSelectionForPane(pane));
    const entries = pane === 'right' ? explorerSplit?.rightEntries ?? [] : explorerEntries;
    return entries
      .filter((entry) => entry.isDirectory && selected.has(entry.path))
      .map((entry) => entry.path);
  }

  function closeExplorerContextMenus() {
    explorerContextMenu = null;
    explorerBlankContextMenu = null;
    explorerTabContextMenu = null;
    explorerColumnMenu = null;
    explorerFolderCreateSubmenuOpen = false;
    explorerCompressionSubmenuOpen = false;
    explorerThumbnailSubmenuOpen = false;
    explorerDbManagementSubmenuOpen = false;
    explorerGidSubmenuOpen = false;
  }

  function inferCreatorNameFromFolderPath(path: string) {
    const originalName = getExplorerPathLabel(path).trim();
    const creatorMatch = /^【(.+)】$/.exec(originalName);
    return (creatorMatch?.[1] ?? originalName).trim();
  }

  function requestGidAssignment(extensions: string[]) {
    const pane = explorerContextMenu?.pane ?? 'left';
    const folderPaths = getSelectedExplorerFoldersForPane(pane);
    closeExplorerContextMenus();
    if (folderPaths.length === 0) {
      showExplorerToast('gidを発行するフォルダを選択してください。', 'error');
      return;
    }
    gidAssignmentConfirmation = { pane, folderPaths, extensions };
  }

  function confirmGidAssignment() {
    const request = gidAssignmentConfirmation;
    if (!request || gidAssignmentInProgress) return;
    const directory = request.pane === 'right' ? explorerSplit?.rightPath : explorerPath;
    if (!directory) return;
    gidAssignmentInProgress = true;
    gidAssignmentConfirmation = null;
    closeExplorerContextMenus();
    const extensionLabel = request.extensions.join(' / ');
    showExplorerToast(
      `${extensionLabel}ファイルへの${gidSettings.digitCount}桁gid発番を開始しました。`,
      'progress',
      null
    );
    postHostMessage({
      type: 'explorer.gid.assign',
      directory,
      folders: request.folderPaths,
      extensions: request.extensions,
      pane: request.pane === 'right' ? 'split-right' : undefined
    });
  }

  function closeGidAssignmentConfirmation() {
    if (!gidAssignmentInProgress) gidAssignmentConfirmation = null;
  }

  function requestCreatorFolderConversion() {
    const pane = explorerContextMenu?.pane ?? 'left';
    const folderPaths = getSelectedExplorerFoldersForPane(pane);
    explorerContextMenu = null;
    explorerDbManagementSubmenuOpen = false;
    explorerGidSubmenuOpen = false;
    if (folderPaths.length === 0) {
      showExplorerToast('作者フォルダ化するフォルダを選択してください。', 'error');
      return;
    }
    creatorFolderConversionConfirmation = { pane, folderPaths };
  }

  function confirmCreatorFolderConversion() {
    const request = creatorFolderConversionConfirmation;
    if (!request || creatorFolderConversionInProgress) return;
    const directory = request.pane === 'right' ? explorerSplit?.rightPath : explorerPath;
    if (!directory) return;
    creatorFolderConversionInProgress = true;
    postHostMessage({
      type: 'explorer.creatorFolder.convert',
      directory,
      folders: request.folderPaths,
      pane: request.pane === 'right' ? 'split-right' : undefined
    });
  }

  function closeCreatorFolderConversionConfirmation() {
    if (!creatorFolderConversionInProgress) creatorFolderConversionConfirmation = null;
  }

  function requestCreatorReassignment() {
    const pane = explorerContextMenu?.pane ?? 'left';
    const folderPaths = getSelectedExplorerFoldersForPane(pane);
    explorerContextMenu = null;
    explorerDbManagementSubmenuOpen = false;
    explorerGidSubmenuOpen = false;
    if (folderPaths.length === 0) {
      showExplorerToast('作者情報を付け替えるフォルダを選択してください。', 'error');
      return;
    }

    const sourceCreator = inferCreatorNameFromFolderPath(folderPaths[0]);
    creatorReassignmentConfirmation = {
      pane,
      folderPaths,
      sourceCreator,
      targetCreator: ''
    };
  }

  function confirmCreatorReassignment() {
    const request = creatorReassignmentConfirmation;
    if (!request || creatorReassignmentInProgress) return;
    const targetCreator = request.targetCreator.trim();
    if (!targetCreator) {
      showExplorerToast('正しいCreator名を入力してください。', 'error');
      return;
    }

    creatorReassignmentInProgress = true;
    postHostMessage({
      type: 'explorer.creator.reassign',
      folders: request.folderPaths,
      sourceCreator: request.sourceCreator,
      targetCreator
    });
  }

  function closeCreatorReassignmentConfirmation() {
    if (!creatorReassignmentInProgress) creatorReassignmentConfirmation = null;
  }

  function getCreatorFolderTargetName(path: string) {
    const creatorName = inferCreatorNameFromFolderPath(path);
    return creatorName ? `【${creatorName}】` : '【元の名前】';
  }

  function organizeExplorerSelection(pane: 'left' | 'right', mode: 'aggregate' | 'separate') {
    const path = pane === 'left' ? explorerPath : explorerSplit?.rightPath;
    const paths = getExplorerSelectionForPane(pane);
    explorerContextMenu = null;
    explorerBlankContextMenu = null;
    explorerFolderCreateSubmenuOpen = false;
    if (!path || paths.length === 0 || isExplorerFolderUpdateLocked()) {
      return;
    }

    if (pane === 'left') {
      explorerIsLoading = true;
    }
    else if (explorerSplit) {
      explorerSplit = { ...explorerSplit, rightIsLoading: true };
    }

    postHostMessage({
      type: 'explorer.folder.organize',
      path,
      paths,
      mode,
      pane: pane === 'right' ? 'split-right' : undefined
    });
  }

  function openExplorerColumnMenu(event: MouseEvent) {
    event.preventDefault();
    event.stopPropagation();
    explorerContextMenu = null;
    explorerColumnMenu = {
      x: Math.max(8, Math.min(event.clientX, window.innerWidth - 220)),
      y: Math.max(8, Math.min(event.clientY, window.innerHeight - 510))
    };
  }

  function parseExplorerDetailColumns(rawValue: unknown): ExplorerDetailColumnId[] {
    if (typeof rawValue !== 'string') {
      return [];
    }

    const knownColumns = new Set(explorerDetailColumnDefinitions.map((column) => column.id));
    const requested = rawValue.split(',')
      .map((column) => column.trim() as ExplorerDetailColumnId)
      .filter((column) => knownColumns.has(column));
    return [...new Set(requested)];
  }

  function toggleExplorerDetailColumn(columnId: ExplorerDetailColumnId) {
    if (explorerDetailColumns.includes(columnId)) {
      if (explorerDetailColumns.length === 1) {
        return;
      }
      explorerDetailColumns = explorerDetailColumns.filter((column) => column !== columnId);
    }
    else {
      explorerDetailColumns = [...explorerDetailColumns, columnId];
    }

    persistNavigationState();
  }

  function startExplorerDetailColumnDrag(event: DragEvent, columnId: ExplorerDetailColumnId) {
    draggedExplorerDetailColumn = columnId;
    event.dataTransfer?.setData('text/plain', columnId);
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
    }
  }

  function reorderExplorerDetailColumn(event: DragEvent, targetColumnId: ExplorerDetailColumnId) {
    event.preventDefault();
    const sourceColumnId = draggedExplorerDetailColumn;
    if (!sourceColumnId || sourceColumnId === targetColumnId) {
      return;
    }

    const sourceIndex = explorerDetailColumns.indexOf(sourceColumnId);
    const targetIndex = explorerDetailColumns.indexOf(targetColumnId);
    if (sourceIndex < 0 || targetIndex < 0) {
      return;
    }

    const reordered = [...explorerDetailColumns];
    reordered.splice(sourceIndex, 1);
    reordered.splice(targetIndex, 0, sourceColumnId);
    explorerDetailColumns = reordered;
    draggedExplorerDetailColumn = null;
    persistNavigationState();
  }

  function parseMouseGestureSettings(rawValue: unknown): MouseGestureSettings {
    if (typeof rawValue !== 'string' || !rawValue.trim()) {
      return structuredClone(defaultMouseGestureSettings);
    }

    try {
      const parsed = JSON.parse(rawValue) as Partial<MouseGestureSettings>;
      const allowedCommands = new Set<MouseGestureCommand>(mouseGestureCommands.map((command) => command.value));
      const bindings = Array.isArray(parsed.bindings)
        ? parsed.bindings
          .filter((binding): binding is MouseGestureBinding =>
            typeof binding?.gesture === 'string' && allowedCommands.has(binding.command as MouseGestureCommand))
        : [];
      return {
        enabled: typeof parsed.enabled === 'boolean' ? parsed.enabled : defaultMouseGestureSettings.enabled,
        lineColor: typeof parsed.lineColor === 'string' ? parsed.lineColor : defaultMouseGestureSettings.lineColor,
        lineWidth: Math.min(16, Math.max(1, Number(parsed.lineWidth) || defaultMouseGestureSettings.lineWidth)),
        threshold: Math.min(600, Math.max(8, Number(parsed.threshold) || defaultMouseGestureSettings.threshold)),
        bindings: bindings.length > 0 ? bindings : structuredClone(defaultMouseGestureSettings.bindings)
      };
    }
    catch {
      return structuredClone(defaultMouseGestureSettings);
    }
  }

  function parseKeyboardShortcutSettings(rawValue: unknown): KeyboardShortcutSettings {
    const defaults = structuredClone(defaultKeyboardShortcutSettings);
    if (typeof rawValue !== 'string' || !rawValue.trim()) return defaults;
    try {
      const parsed = JSON.parse(rawValue) as Partial<KeyboardShortcutSettings>;
      keyboardShortcutDefinitions.forEach(({ command }) => {
        if (typeof parsed[command] === 'string') defaults[command] = parsed[command].trim();
      });
      return defaults;
    }
    catch {
      return defaults;
    }
  }

  function getKeyboardShortcutFromEvent(event: KeyboardEvent) {
    const modifierKeys = new Set(['Control', 'Shift', 'Alt', 'Meta']);
    if (modifierKeys.has(event.key)) return '';
    const parts: string[] = [];
    if (event.ctrlKey || event.metaKey) parts.push('Ctrl');
    if (event.altKey) parts.push('Alt');
    if (event.shiftKey) parts.push('Shift');
    const key = event.key === ' ' ? 'Space' : event.key.length === 1 ? event.key.toUpperCase() : event.key;
    parts.push(key);
    return parts.join('+');
  }

  function matchesKeyboardShortcut(event: KeyboardEvent, command: KeyboardShortcutCommand) {
    const shortcut = keyboardShortcutSettings[command];
    return Boolean(shortcut) && getKeyboardShortcutFromEvent(event) === shortcut;
  }

  function formatKeyboardShortcut(shortcut: string) {
    return shortcut
      .replaceAll('ArrowUp', '↑')
      .replaceAll('ArrowDown', '↓')
      .replaceAll('ArrowLeft', '←')
      .replaceAll('ArrowRight', '→');
  }

  function captureKeyboardShortcut(event: KeyboardEvent, command: KeyboardShortcutCommand) {
    event.preventDefault();
    event.stopPropagation();
    const shortcut = getKeyboardShortcutFromEvent(event);
    if (!shortcut) return;
    const conflict = keyboardShortcutDefinitions.find(definition =>
      definition.command !== command && keyboardShortcutSettings[definition.command] === shortcut);
    if (conflict) {
      showExplorerToast(`「${formatKeyboardShortcut(shortcut)}」は「${conflict.label}」で使用されています。`, 'error');
      return;
    }
    keyboardShortcutSettings = { ...keyboardShortcutSettings, [command]: shortcut };
    persistNavigationState();
  }

  function clearKeyboardShortcut(command: KeyboardShortcutCommand) {
    keyboardShortcutSettings = { ...keyboardShortcutSettings, [command]: '' };
    persistNavigationState();
  }

  function resetKeyboardShortcutSettings() {
    keyboardShortcutSettings = structuredClone(defaultKeyboardShortcutSettings);
    persistNavigationState();
  }

  function updateMouseGestureSettings() {
    mouseGestureSettings = {
      ...mouseGestureSettings,
      lineWidth: Math.min(16, Math.max(1, Number(mouseGestureSettings.lineWidth) || 1)),
      threshold: Math.min(600, Math.max(8, Number(mouseGestureSettings.threshold) || 8))
    };
    persistNavigationState();
  }

  function resetMouseGestureSettings() {
    mouseGestureSettings = structuredClone(defaultMouseGestureSettings);
    newGestureDirection = '↑';
    newGestureCommand = 'parent';
    persistNavigationState();
  }

  function resetCreatorTrackingSettingsDraft() {
    creatorTrackingSettingsDraft = structuredClone(defaultCreatorTrackingSettingsDraft);
    creatorTrackingCompositionRefreshPending = true;
    saveCreatorTrackingSettings();
  }

  function addCreatorTrackingActivityPlaceSetting() {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      activityPlaces: [...creatorTrackingSettingsDraft.activityPlaces, { label: '', placeholder: '', iconDataUri: '' }]
    };
  }

  function removeCreatorTrackingActivityPlaceSetting(index: number) {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      activityPlaces: creatorTrackingSettingsDraft.activityPlaces.filter((_, candidateIndex) => candidateIndex !== index)
    };
    saveCreatorTrackingSettings();
  }

  function updateCreatorTrackingActivityPlaceSetting(
    index: number,
    patch: Partial<CreatorTrackingActivityPlaceSetting>) {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      activityPlaces: creatorTrackingSettingsDraft.activityPlaces.map((place, candidateIndex) =>
        candidateIndex === index ? { ...place, ...patch } : place)
    };
  }

  function saveCreatorTrackingSettings() {
    postHostMessage({
      type: 'settings.creatorTracking.save',
      activityPlaces: creatorTrackingSettingsDraft.activityPlaces,
      compositionLabelLimit: creatorTrackingSettingsDraft.compositionLabelLimit,
      followPolicyOptions: creatorTrackingSettingsDraft.followPolicyOptions,
      metrics: creatorTrackingSettingsDraft.metrics.map((metric) => ({
        key: metric.key,
        label: metric.label,
        weightPercent: metric.weightPercent
      }))
    });
  }

  function addCreatorTrackingStringOption(kind: 'followPolicyOptions') {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      [kind]: [...creatorTrackingSettingsDraft[kind], '']
    };
  }

  function updateCreatorTrackingStringOption(
    kind: 'followPolicyOptions',
    index: number,
    value: string) {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      [kind]: creatorTrackingSettingsDraft[kind].map((option, candidateIndex) =>
        candidateIndex === index ? value : option)
    };
  }

  function removeCreatorTrackingStringOption(kind: 'followPolicyOptions', index: number) {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      [kind]: creatorTrackingSettingsDraft[kind].filter((_, candidateIndex) => candidateIndex !== index)
    };
    saveCreatorTrackingSettings();
  }

  function saveCreatorTrackingCompositionLabelLimit() {
    creatorTrackingSettingsDraft = {
      ...creatorTrackingSettingsDraft,
      compositionLabelLimit: Math.max(1, Math.min(8, Math.round(Number(creatorTrackingSettingsDraft.compositionLabelLimit) || 5)))
    };
    creatorTrackingCompositionRefreshPending = true;
    saveCreatorTrackingSettings();
  }

  function fetchCreatorTrackingActivityPlaceIcon(index: number) {
    const place = creatorTrackingSettingsDraft.activityPlaces[index];
    if (!place) return;
    const requestId = `creator-tracking-icon-${nextCreatorTrackingIconFetchRequestId++}`;
    creatorTrackingIconFetchRequests = { ...creatorTrackingIconFetchRequests, [requestId]: index };
    postHostMessage({
      type: 'settings.creatorTracking.icon.fetch',
      requestId,
      url: place.placeholder
    });
  }

  function isCreatorTrackingActivityPlaceIconLoading(index: number) {
    return Object.values(creatorTrackingIconFetchRequests).includes(index);
  }

  function addMouseGestureBinding() {
    if (mouseGestureSettings.bindings.some((binding) => binding.gesture === newGestureDirection)) {
      return;
    }

    mouseGestureSettings = {
      ...mouseGestureSettings,
      bindings: [...mouseGestureSettings.bindings, { gesture: newGestureDirection, command: newGestureCommand }]
    };
    persistNavigationState();
  }

  function updateMouseGestureBinding(index: number, command: MouseGestureCommand) {
    mouseGestureSettings = {
      ...mouseGestureSettings,
      bindings: mouseGestureSettings.bindings.map((binding, bindingIndex) =>
        bindingIndex === index ? { ...binding, command } : binding)
    };
    persistNavigationState();
  }

  function removeMouseGestureBinding(index: number) {
    mouseGestureSettings = {
      ...mouseGestureSettings,
      bindings: mouseGestureSettings.bindings.filter((_, bindingIndex) => bindingIndex !== index)
    };
    persistNavigationState();
  }

  function getMouseGestureCommand(gesture: string) {
    return mouseGestureSettings.bindings.find((binding) => binding.gesture === gesture)?.command ?? 'none';
  }

  function getExplorerDetailValue(entry: ExplorerEntry, column: ExplorerDetailColumnId) {
    switch (column) {
      case 'pages':
        return entry.pageCount?.toLocaleString('ja-JP') ?? '-';
      case 'gid':
        return getEntryIdentifier(entry) || '-';
      case 'type':
        return entry.isDirectory ? 'フォルダ' : entry.extension || 'ファイル';
      case 'size':
        return formatSize(entry.size);
      case 'created':
        return formatModifiedAt(entry.createdAt);
      case 'accessed':
        return formatModifiedAt(entry.accessedAt);
      case 'modified':
        return formatModifiedAt(entry.modifiedAt);
      default:
        return '-';
    }
  }

  function openContextMenuEntryInNewTab() {
    const context = explorerContextMenu;
    explorerContextMenu = null;
    if (context?.entry.isDirectory) {
      openNewExplorerTab(context.entry.path, context.entry.name);
    }
  }

  function deleteContextFolderThumbnail() {
    const context = explorerContextMenu;
    explorerContextMenu = null;
    if (context?.entry.isDirectory) {
      postHostMessage({ type: 'explorer.thumbnail.delete', path: context.entry.path });
    }
  }

  function setContextEntryAsParentThumbnail() {
    const context = explorerContextMenu;
    explorerContextMenu = null;
    if (context) {
      postHostMessage({
        type: 'explorer.thumbnail.setParent',
        path: context.entry.path,
        pane: context.pane === 'right' ? 'split-right' : undefined
      });
    }
  }

  function isWinRarArchive(entry: ExplorerEntry) {
    return winRarSettings.supportedExtensions
      .split(/[;,\s]+/)
      .filter(Boolean)
      .map((extension) => extension.startsWith('.') ? extension.toLowerCase() : `.${extension.toLowerCase()}`)
      .includes(entry.extension.toLowerCase());
  }

  function isRarArchive(entry: ExplorerEntry) {
    return !entry.isDirectory && entry.extension.toLowerCase() === '.rar';
  }

  function getContextRarConversionTargets() {
    const context = explorerContextMenu;
    if (!context || !isRarArchive(context.entry)) {
      return [] as ExplorerEntry[];
    }

    const entries = context.pane === 'right'
      ? explorerSplit?.rightEntries ?? []
      : explorerEntries;
    const selected = context.pane === 'right'
      ? explorerSplit?.rightSelectedPaths ?? []
      : selectedPaths;
    const targetPaths = selected.includes(context.entry.path)
      ? new Set(selected)
      : new Set([context.entry.path]);
    return entries.filter((entry) => targetPaths.has(entry.path) && isRarArchive(entry));
  }

  function openContextEntryWithWinRar() {
    const context = explorerContextMenu;
    explorerContextMenu = null;
    if (!context) {
      return;
    }

    postHostMessage({ type: 'explorer.winrar.open', path: context.entry.path });
  }

  function extractContextArchiveWithWinRar() {
    const context = explorerContextMenu;
    explorerContextMenu = null;
    if (!context || !isWinRarArchive(context.entry)) {
      return;
    }

    startWinRarProgress('WinRAR: 内容を確認して解凍中');

    postHostMessage({
      type: 'explorer.winrar.extractAuto',
      path: context.entry.path,
      pane: context.pane === 'right' ? 'split-right' : undefined
    });
  }

  function convertContextRarToZip() {
    const context = explorerContextMenu;
    const targets = getContextRarConversionTargets();
    closeExplorerContextMenus();
    if (!context || targets.length === 0) {
      return;
    }
    if (rarToZipBatchInProgress) {
      showExplorerToast('別のRARからZIPへの変換を実行中です。', 'error');
      return;
    }

    rarToZipBatchInProgress = true;
    startWinRarProgress(`WinRAR: RAR ${targets.length}件のZIP変換を準備しています。`);
    postHostMessage({
      type: 'explorer.winrar.convertRarToZip',
      paths: targets.map((entry) => entry.path),
      directory: context.pane === 'right' ? explorerSplit?.rightPath : explorerPath,
      pane: context.pane === 'right' ? 'split-right' : undefined
    });
  }

  function getContextFolderCompressionRequest(): WinRarCompressionRequest | null {
    const context = explorerContextMenu;
    explorerContextMenu = null;
    explorerCompressionSubmenuOpen = false;
    if (!context?.entry.isDirectory) {
      return null;
    }

    const entries = context.pane === 'right'
      ? explorerSplit?.rightEntries ?? []
      : explorerEntries;
    const selected = context.pane === 'right'
      ? explorerSplit?.rightSelectedPaths ?? []
      : selectedPaths;
    const targetPaths = selected.includes(context.entry.path) ? selected : [context.entry.path];
    const targets = entries.filter((entry) => targetPaths.includes(entry.path));
    if (targets.some((entry) => !entry.isDirectory)) {
      showExplorerToast('圧縮と処理後削除はフォルダだけを選択して実行してください。', 'error');
      return null;
    }

    const folders = targets.filter((entry) => entry.isDirectory);
    if (folders.length === 0) {
      showExplorerToast('圧縮するフォルダを選択してください。', 'error');
      return null;
    }

    return {
      pane: context.pane,
      folderPaths: folders.map((entry) => entry.path),
      folderNames: folders.map((entry) => entry.name)
    };
  }

  function getContextFolderCompressionTargetCount() {
    const context = explorerContextMenu;
    if (!context?.entry.isDirectory) {
      return 0;
    }

    const entries = context.pane === 'right'
      ? explorerSplit?.rightEntries ?? []
      : explorerEntries;
    const selected = context.pane === 'right'
      ? explorerSplit?.rightSelectedPaths ?? []
      : selectedPaths;
    const targetPaths = selected.includes(context.entry.path) ? selected : [context.entry.path];
    return entries.filter((entry) => entry.isDirectory && targetPaths.includes(entry.path)).length;
  }

  function requestIndividualWinRarCompression() {
    pendingWinRarIndividualCompression = getContextFolderCompressionRequest();
  }

  function requestWinRarPackageCompression() {
    const request = getContextFolderCompressionRequest();
    if (!request) {
      return;
    }

    pendingWinRarPackageCompression = request;
    const firstName = [...request.folderNames].sort((left, right) => left.localeCompare(right, 'ja'))[0] ?? '';
    winRarPackageName = firstName.replace(/^\d{2}[\s_-]*/, '') || firstName;
  }

  function executeIndividualWinRarCompression() {
    const request = pendingWinRarIndividualCompression;
    pendingWinRarIndividualCompression = null;
    if (!request) {
      return;
    }

    startWinRarProgress(`WinRAR: 個別に圧縮と処理後削除中 (0/${request.folderPaths.length})`);
    postHostMessage({
      type: 'explorer.winrar.compressDelete',
      paths: request.folderPaths,
      pane: request.pane === 'right' ? 'split-right' : undefined
    });
  }

  function executeWinRarPackageCompression() {
    const request = pendingWinRarPackageCompression;
    const archiveName = winRarPackageName.trim();
    if (!request || !archiveName) {
      showExplorerToast('書庫名を入力してください。', 'error');
      return;
    }

    pendingWinRarPackageCompression = null;
    startWinRarProgress('WinRAR: 1 パッケージに圧縮と処理後削除中');
    postHostMessage({
      type: 'explorer.winrar.compressPackageDelete',
      paths: request.folderPaths,
      archiveName,
      pane: request.pane === 'right' ? 'split-right' : undefined
    });
  }

  function refreshExplorerThumbnail(path: string) {
    if (!path) {
      return;
    }

    const { [path]: _, ...remainingThumbnails } = explorerThumbnails;
    explorerThumbnails = remainingThumbnails;
    requestedExplorerThumbnailPaths.delete(path);
    unavailableExplorerThumbnailPaths.delete(path);
    postHostMessage({ type: 'explorer.thumbnail', path, priority: explorerThumbnailPriority + 1, forceRefresh: true });
  }

  function beginExplorerGesture(event: PointerEvent) {
    if (mouseGestureSettings.enabled && event.button === 2) {
      explorerGestureStart = { x: event.clientX, y: event.clientY };
      gestureTrail = [{ x: event.clientX, y: event.clientY }];
    }
  }

  function updateExplorerGesture(event: PointerEvent) {
    if (explorerGestureStart && (event.buttons & 2) === 2) {
      appendGesturePoint(event);
    }
  }

  function finishExplorerGesture(event: PointerEvent) {
    if (!explorerGestureStart || event.button !== 2) {
      return;
    }

    const deltaX = event.clientX - explorerGestureStart.x;
    const deltaY = event.clientY - explorerGestureStart.y;
    explorerGestureStart = null;

    if (executeMouseGesture(deltaX, deltaY, 'left')) {
      suppressExplorerContextMenu = true;
    }
    clearGestureTrail();
  }

  function suppressExplorerBlankContextMenu(event: MouseEvent) {
    event.preventDefault();
    suppressExplorerContextMenu = false;
  }

  function appendGesturePoint(event: PointerEvent) {
    const last = gestureTrail[gestureTrail.length - 1];
    if (!last || Math.hypot(event.clientX - last.x, event.clientY - last.y) >= 5) {
      gestureTrail = [...gestureTrail.slice(-79), { x: event.clientX, y: event.clientY }];
    }
  }

  function clearGestureTrail() {
    window.setTimeout(() => {
      gestureTrail = [];
    }, 140);
  }

  function showExplorerToast(
    message: string,
    kind: 'success' | 'error' | 'progress',
    duration: number | null = 3_200,
    action: 'cancelDatabaseScan' | null = null
  ) {
    if (!message) {
      return;
    }

    explorerToastMessage = translateSystemText(message, appLanguage);
    explorerToastKind = kind;
    explorerToastAction = action;
    if (explorerToastTimer) {
      clearTimeout(explorerToastTimer);
    }
    if (duration !== null) {
      explorerToastTimer = setTimeout(() => {
        explorerToastMessage = '';
        explorerToastAction = null;
      }, duration);
    }
  }

  function startWinRarProgress(label: string) {
    finishWinRarProgress();
    winRarProgressLabel = label;
    winRarProgressSeconds = 0;
    winRarProgressTimer = setInterval(() => {
      winRarProgressSeconds += 1;
    }, 1_000);
  }

  function updateWinRarProgress(label: string) {
    if (!winRarProgressLabel) {
      startWinRarProgress(label);
      return;
    }

    winRarProgressLabel = label;
  }

  function finishWinRarProgress() {
    if (winRarProgressTimer) {
      clearInterval(winRarProgressTimer);
      winRarProgressTimer = undefined;
    }
    winRarProgressLabel = '';
    winRarProgressSeconds = 0;
  }

  function openExplorerBookmark(bookmark: ExplorerBookmark) {
    activeView = 'explorer';
    explorerBookmarksExpanded = true;
    persistNavigationState();
    loadExplorer(bookmark.path);
  }

  function deleteExplorerBookmark(event: MouseEvent, bookmark: ExplorerBookmark) {
    if (event.button !== 1) {
      return;
    }

    event.preventDefault();
    removeExplorerBookmark(bookmark);
  }

  function removeExplorerBookmark(bookmark: ExplorerBookmark) {
    postHostMessage({ type: 'explorer.bookmarks.delete', path: bookmark.path });
  }

  function bookmarkExplorerTab(tab: ExplorerTab) {
    if (!tab.path) {
      return;
    }

    postHostMessage({ type: 'explorer.bookmarks.save', path: tab.path, label: tab.label });
  }

  function handleExplorerBookmarkDragOver(event: DragEvent) {
    event.preventDefault();
  }

  function handleExplorerBookmarkDrop(event: DragEvent) {
    event.preventDefault();
    if (draggedExplorerTab) {
      bookmarkExplorerTab(draggedExplorerTab);
    }
    draggedExplorerTab = null;
    explorerBookmarksExpanded = true;
    persistNavigationState();
  }

  function startExplorerBookmarkDrag(event: DragEvent, bookmark: ExplorerBookmark) {
    draggedExplorerBookmark = bookmark;
    event.dataTransfer?.setData('text/plain', bookmark.path);
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
    }
  }

  function reorderExplorerBookmark(event: DragEvent, target: ExplorerBookmark) {
    event.preventDefault();
    event.stopPropagation();
    const source = draggedExplorerBookmark;
    if (!source || source.path === target.path) {
      return;
    }

    const sourceIndex = explorerBookmarks.findIndex((bookmark) => bookmark.path === source.path);
    const targetIndex = explorerBookmarks.findIndex((bookmark) => bookmark.path === target.path);
    if (sourceIndex < 0 || targetIndex < 0) {
      return;
    }

    const reordered = [...explorerBookmarks];
    reordered.splice(sourceIndex, 1);
    reordered.splice(targetIndex, 0, source);
    explorerBookmarks = reordered;
    postHostMessage({ type: 'explorer.bookmarks.reorder', paths: reordered.map((bookmark) => bookmark.path) });
    draggedExplorerBookmark = null;
  }

  function navigateToParent() {
    if (explorerParentPath) {
      loadExplorer(explorerParentPath);
    }
  }

  function handleExplorerCardZoom(event: WheelEvent) {
    if (!event.ctrlKey) {
      return;
    }

    event.preventDefault();
    const columns = [4, 5, 6, 7];
    const currentIndex = columns.indexOf(explorerCardColumns);
    const nextIndex = Math.max(0, Math.min(columns.length - 1, currentIndex + (event.deltaY < 0 ? -1 : 1)));
    explorerCardColumns = columns[nextIndex];
    persistNavigationState();
  }

  function openExplorerEntry(entry: ExplorerEntry, openInNewTab = false, activation: 'single' | 'double' = 'double') {
    if (entry.isDirectory) {
      if (openInNewTab) {
        openNewExplorerTab(entry.path, entry.name);
      }
      else if (activation === 'double') {
        loadExplorer(entry.path);
      }
      return;
    }

    if (activation === 'double' && !hasExplorerLaunchRule(entry, 'double')) {
      // A single-click-only rule remains responsible for the first click of a double-click.
      return;
    }

    launchExplorerEntry(entry, activation);
  }

  function launchExplorerEntry(entry: ExplorerEntry, activation: 'single' | 'double') {
    if (entry.isDirectory || !hasExplorerLaunchRule(entry, activation)) {
      return;
    }

    if (activation === 'single') {
      const now = Date.now();
      const lastLaunch = explorerSingleClickLaunches[entry.path] ?? 0;
      if (now - lastLaunch < 700) {
        return;
      }

      explorerSingleClickLaunches = { ...explorerSingleClickLaunches, [entry.path]: now };
      postHostMessage({ type: 'explorer.open', path: entry.path, activation: 'single' });
      return;
    }

    if (hasExplorerLaunchRule(entry, 'single')) {
      return;
    }

    postHostMessage({ type: 'explorer.open', path: entry.path, activation: 'double' });
  }

  function selectExplorerEntry(event: MouseEvent, entry: ExplorerEntry, source: 'grid' | 'list' = 'grid') {
    if (event.shiftKey && explorerSelectionAnchorPath) {
      const range = getSelectionRange(filteredExplorerEntries, explorerSelectionAnchorPath, entry.path);
      selectedPaths = range.length > 0 ? range : [entry.path];
      revealExplorerEntry(entry.path, source === 'grid' ? 'list' : 'grid');
      return;
    }

    if (event.ctrlKey || event.metaKey) {
      const wasSelected = selectedPaths.includes(entry.path);
      selectedPaths = wasSelected
        ? selectedPaths.filter((path) => path !== entry.path)
        : [...selectedPaths, entry.path];
      if (!wasSelected) {
        revealExplorerEntry(entry.path, source === 'grid' ? 'list' : 'grid');
      }
      explorerSelectionAnchorPath = entry.path;
      return;
    }

    selectedPaths = [entry.path];
    explorerSelectionAnchorPath = entry.path;
    revealExplorerEntry(entry.path, source === 'grid' ? 'list' : 'grid');
    if (hasExplorerLaunchRule(entry, 'single')) {
      launchExplorerEntry(entry, 'single');
    }
  }

  function getSelectionRange(entries: ExplorerEntry[], anchorPath: string, targetPath: string) {
    const anchorIndex = entries.findIndex((entry) => entry.path === anchorPath);
    const targetIndex = entries.findIndex((entry) => entry.path === targetPath);
    if (anchorIndex < 0 || targetIndex < 0) {
      return [];
    }

    const start = Math.min(anchorIndex, targetIndex);
    const end = Math.max(anchorIndex, targetIndex);
    return entries.slice(start, end + 1).map((entry) => entry.path);
  }

  function revealExplorerEntry(path: string, target: 'grid' | 'list') {
    requestAnimationFrame(() => {
      const container = target === 'grid' ? explorerGridPaneElement : explorerDetailPaneElement;
      if (!container) {
        return;
      }

      const item = Array.from(container.querySelectorAll<HTMLElement>('[data-explorer-path]'))
        .find((element) => element.dataset.explorerPath === path);
      if (!item) {
        return;
      }

      const containerBounds = container.getBoundingClientRect();
      const itemBounds = item.getBoundingClientRect();
      const topPadding = target === 'list' ? 38 : 10;
      if (itemBounds.top < containerBounds.top + topPadding) {
        container.scrollBy({ top: itemBounds.top - containerBounds.top - topPadding, behavior: 'smooth' });
      }
      else if (itemBounds.bottom > containerBounds.bottom - 10) {
        container.scrollBy({ top: itemBounds.bottom - containerBounds.bottom + 10, behavior: 'smooth' });
      }
    });
  }

  function onExplorerKeydown(event: KeyboardEvent, entry: ExplorerEntry) {
    if (event.key === 'Enter') {
      openExplorerEntry(entry, event.ctrlKey || event.metaKey);
    }

    if (event.key === ' ') {
      event.preventDefault();
      selectedPaths = [entry.path];
    }
  }

  function handleExplorerShortcut(event: KeyboardEvent) {
    if (activeView !== 'explorer' || renamingEntry || deleteConfirmation || splitRenamingEntry || splitDeleteConfirmation) {
      return;
    }

    const target = event.target as HTMLElement | null;
    if (target?.matches('input, select, textarea, [contenteditable="true"]')) {
      return;
    }
    if (matchesKeyboardShortcut(event, 'navigateExplorerParent')) {
      event.preventDefault();
      if (explorerSplit && splitFocusedPane === 'right') {
        navigateSplitParent();
      }
      else {
        navigateToParent();
      }
      return;
    }
    if (matchesKeyboardShortcut(event, 'createExplorerFolder')) {
      event.preventDefault();
      createNewExplorerFolder(explorerSplit && splitFocusedPane === 'right' ? 'right' : 'left');
      return;
    }
    if (matchesKeyboardShortcut(event, 'selectPreviousTab')) {
      event.preventDefault();
      selectRelativeExplorerTab(-1);
      return;
    }
    if (matchesKeyboardShortcut(event, 'selectNextTab')) {
      event.preventDefault();
      selectRelativeExplorerTab(1);
      return;
    }

    if (explorerSplit && splitFocusedPane === 'right') {
      handleSplitShortcut(event);
      return;
    }

    if (matchesKeyboardShortcut(event, 'copyExplorerSelection')) {
      event.preventDefault();
      copyExplorerSelection(false);
    }
    else if (matchesKeyboardShortcut(event, 'cutExplorerSelection')) {
      event.preventDefault();
      copyExplorerSelection(true);
    }
    else if (matchesKeyboardShortcut(event, 'pasteExplorerSelection')) {
      event.preventDefault();
      pasteExplorerSelection();
    }
    else if (matchesKeyboardShortcut(event, 'selectAllExplorerEntries')) {
      event.preventDefault();
      selectedPaths = filteredExplorerEntries.map((entry) => entry.path);
    }
    else if (matchesKeyboardShortcut(event, 'renameExplorerEntry')) {
      event.preventDefault();
      beginRename();
    }
    else if (matchesKeyboardShortcut(event, 'deleteExplorerSelection')) {
      event.preventDefault();
      requestDelete();
    }
  }

  function handleSplitShortcut(event: KeyboardEvent) {
    if (matchesKeyboardShortcut(event, 'copyExplorerSelection')) {
      event.preventDefault();
      copySplitSelection(false);
    }
    else if (matchesKeyboardShortcut(event, 'cutExplorerSelection')) {
      event.preventDefault();
      copySplitSelection(true);
    }
    else if (matchesKeyboardShortcut(event, 'pasteExplorerSelection')) {
      event.preventDefault();
      pasteSplitSelection();
    }
    else if (matchesKeyboardShortcut(event, 'selectAllExplorerEntries') && explorerSplit) {
      event.preventDefault();
      explorerSplit = { ...explorerSplit, rightSelectedPaths: splitRightEntries.map((entry) => entry.path) };
    }
    else if (matchesKeyboardShortcut(event, 'renameExplorerEntry')) {
      event.preventDefault();
      beginSplitRename();
    }
    else if (matchesKeyboardShortcut(event, 'deleteExplorerSelection')) {
      event.preventDefault();
      requestSplitDelete();
    }
  }

  function openNewExplorerTab(path = '', label = '新しいタブ') {
    saveExplorerTabScroll();
    exitExplorerSplit();
    const tab = { id: `explorer-tab-${nextExplorerTabId++}`, path, label };
    explorerTabs = [...explorerTabs, tab];
    activeExplorerTabId = tab.id;
    pendingExplorerScrollRestoreTabId = tab.id;
    persistExplorerTabs();
    loadExplorer(path, true);
  }

  function selectRelativeExplorerTab(offset: number) {
    if (explorerTabs.length < 2) {
      return;
    }

    const currentIndex = explorerTabs.findIndex((tab) => tab.id === activeExplorerTabId);
    const nextIndex = (currentIndex + offset + explorerTabs.length) % explorerTabs.length;
    selectExplorerTab(explorerTabs[nextIndex]);
  }

  function selectExplorerTab(tab: ExplorerTab) {
    saveExplorerTabScroll();
    exitExplorerSplit();
    activeExplorerTabId = tab.id;
    pendingExplorerScrollRestoreTabId = tab.id;
    persistExplorerTabs();
    loadExplorer(tab.path, true);
  }

  function closeExplorerTab(tab: ExplorerTab) {
    if (tab.id === activeExplorerTabId) {
      saveExplorerTabScroll();
    }
    if (explorerSplit && (tab.id === explorerSplit.leftTabId || tab.id === explorerSplit.rightTabId)) {
      exitExplorerSplit();
    }

    const index = explorerTabs.findIndex((candidate) => candidate.id === tab.id);
    const remainingTabs = explorerTabs.filter((candidate) => candidate.id !== tab.id);
    explorerTabs = remainingTabs;

    if (tab.id !== activeExplorerTabId) {
      persistExplorerTabs();
      return;
    }

    if (remainingTabs.length === 0) {
      activeExplorerTabId = '';
      openNewExplorerTab();
      return;
    }

    const nextTab = remainingTabs[Math.min(index, remainingTabs.length - 1)];
    activeExplorerTabId = nextTab.id;
    pendingExplorerScrollRestoreTabId = nextTab.id;
    persistExplorerTabs();
    loadExplorer(nextTab.path, true);
  }

  function closeExplorerTabWithMiddleClick(event: MouseEvent, tab: ExplorerTab) {
    if (event.button !== 1) {
      return;
    }

    event.preventDefault();
    closeExplorerTab(tab);
  }

  function duplicateExplorerTab(event: MouseEvent, tab: ExplorerTab) {
    event.preventDefault();
    saveExplorerTabScroll();
    const duplicate = { id: `explorer-tab-${nextExplorerTabId++}`, path: tab.path, label: tab.label };
    explorerTabs = [...explorerTabs, duplicate];
    activeExplorerTabId = duplicate.id;
    pendingExplorerScrollRestoreTabId = duplicate.id;
    loadExplorer(duplicate.path, true);
  }

  function reorderExplorerTab(event: DragEvent, target: ExplorerTab) {
    event.preventDefault();
    const source = draggedExplorerTab;
    if (!source || source.id === target.id) {
      return;
    }

    const sourceIndex = explorerTabs.findIndex((tab) => tab.id === source.id);
    const targetIndex = explorerTabs.findIndex((tab) => tab.id === target.id);
    if (sourceIndex < 0 || targetIndex < 0) {
      return;
    }

    const reordered = [...explorerTabs];
    reordered.splice(sourceIndex, 1);
    reordered.splice(targetIndex, 0, source);
    explorerTabs = reordered;
    persistExplorerTabs();
    draggedExplorerTab = null;
  }

  function syncExplorerTab(path: string) {
    const label = getExplorerTabLabel(path);
    if (!activeExplorerTabId) {
      const initialTab = { id: `explorer-tab-${nextExplorerTabId++}`, path, label };
      explorerTabs = [initialTab];
      activeExplorerTabId = initialTab.id;
      persistExplorerTabs();
      return;
    }

    explorerTabs = explorerTabs.map((tab) => tab.id === activeExplorerTabId ? { ...tab, path, label } : tab);
    persistExplorerTabs();
  }

  function syncExplorerTabById(tabId: string, path: string) {
    if (!tabId || !path) {
      return;
    }

    const label = getExplorerTabLabel(path);
    explorerTabs = explorerTabs.map((tab) => tab.id === tabId ? { ...tab, path, label } : tab);
    persistExplorerTabs();
  }

  function getExplorerTabLabel(path: string) {
    return path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() || path;
  }

  function persistExplorerTabs() {
    if (!explorerTabsRestored) {
      return;
    }

    postHostMessage({
      type: 'explorer.tabs.save',
      tabs: explorerTabs.map((tab) => ({ path: tab.path, label: tab.label })),
      activeIndex: explorerTabs.findIndex((tab) => tab.id === activeExplorerTabId)
    });
  }

  function copyExplorerSelection(cut: boolean) {
    if (selectedPaths.length === 0) {
      showExplorerToast('操作する項目を選択してください。', 'error');
      return;
    }

    postHostMessage({ type: cut ? 'explorer.cut' : 'explorer.copy', paths: selectedPaths });
    cutClipboardSource = cut && explorerPath ? { pane: 'left', path: explorerPath } : null;
  }

  function pasteExplorerSelection() {
    if (!explorerPath || isExplorerFolderUpdateLocked()) {
      return;
    }

    beginExplorerPaste('left', explorerPath);
    pendingMoveRefresh = cutClipboardSource
      ? {
          sourcePane: cutClipboardSource.pane,
          sourcePath: cutClipboardSource.path,
          destinationPane: 'left',
          destinationPath: explorerPath
        }
      : null;
    postHostMessage({ type: 'explorer.paste', path: explorerPath });
  }

  function beginRename() {
    if (isExplorerFolderUpdateLocked()) {
      return;
    }

    if (selectedExplorerEntries.length !== 1) {
      showExplorerToast('名前を変更する項目を1つ選択してください。', 'error');
      return;
    }

    beginRenameForEntry(selectedExplorerEntries[0]);
  }

  function beginRenameForEntry(entry: ExplorerEntry) {
    renamingEntry = entry;
    const nameParts = getEntryNameParts(entry);
    renameValue = nameParts.displayName;
    renameIdentifier = nameParts.identifier;
    renameExtension = nameParts.extension;
    renameTagBeforeExtension = nameParts.tagBeforeExtension;
  }

  function focusAtEnd(node: HTMLInputElement) {
    requestAnimationFrame(() => {
      node.focus();
      node.setSelectionRange(0, 0);
    });
  }

  function closeRenameOnEscape(event: KeyboardEvent, pane: 'left' | 'right') {
    if (event.key !== 'Escape') {
      return;
    }

    event.preventDefault();
    if (pane === 'left') {
      renamingEntry = null;
    }
    else {
      splitRenamingEntry = null;
    }
  }

  function saveRename(nextPath?: string) {
    if (!renamingEntry || !explorerPath || isExplorerFolderUpdateLocked()) {
      return;
    }

    const newName = buildEntryName(
      renameValue,
      renameIdentifier,
      renameExtension,
      renameTagBeforeExtension,
      renamingEntry.isDirectory
    );
    if (!renameValue.trim() || newName.length > 255) {
      showExplorerToast('識別タグを含めて255文字以内の名前を入力してください。', 'error');
      return;
    }

    explorerIsLoading = true;
    pendingRenameNavigation = nextPath ? { pane: 'left', targetPath: nextPath } : null;
    postHostMessage({
      type: 'explorer.rename',
      path: renamingEntry.path,
      newName,
      directory: explorerPath
    });
    renamingEntry = null;
  }

  function requestDelete() {
    if (isExplorerFolderUpdateLocked()) {
      return;
    }

    if (selectedPaths.length === 0) {
      showExplorerToast('削除する項目を選択してください。', 'error');
      return;
    }

    deleteConfirmation = true;
  }

  function deleteExplorerSelection() {
    if (!explorerPath || isExplorerFolderUpdateLocked()) {
      return;
    }

    deleteConfirmation = false;
    explorerIsLoading = true;
    postHostMessage({ type: 'explorer.delete', paths: selectedPaths, directory: explorerPath });
  }

  function copySplitSelection(cut: boolean) {
    if (!explorerSplit || explorerSplit.rightSelectedPaths.length === 0) {
      showExplorerToast('操作する項目を選択してください。', 'error');
      return;
    }

    postHostMessage({
      type: cut ? 'explorer.cut' : 'explorer.copy',
      paths: explorerSplit.rightSelectedPaths
    });
    cutClipboardSource = cut ? { pane: 'right', path: explorerSplit.rightPath } : null;
  }

  function pasteSplitSelection() {
    if (!explorerSplit?.rightPath || isExplorerFolderUpdateLocked()) {
      return;
    }

    beginExplorerPaste('right', explorerSplit.rightPath);
    pendingMoveRefresh = cutClipboardSource
      ? {
          sourcePane: cutClipboardSource.pane,
          sourcePath: cutClipboardSource.path,
          destinationPane: 'right',
          destinationPath: explorerSplit.rightPath
        }
      : null;
    postHostMessage({ type: 'explorer.paste', path: explorerSplit.rightPath, pane: 'split-right' });
  }

  function beginSplitRename() {
    if (!explorerSplit || isExplorerFolderUpdateLocked()) {
      return;
    }

    const selected = explorerSplit.rightEntries.filter((entry) => explorerSplit?.rightSelectedPaths.includes(entry.path));
    if (selected.length !== 1) {
      showExplorerToast('名前を変更する項目を1つ選択してください。', 'error');
      return;
    }

    beginSplitRenameForEntry(selected[0]);
  }

  function beginSplitRenameForEntry(entry: ExplorerEntry) {
    splitRenamingEntry = entry;
    const nameParts = getEntryNameParts(entry);
    splitRenameValue = nameParts.displayName;
    splitRenameIdentifier = nameParts.identifier;
    splitRenameExtension = nameParts.extension;
    splitRenameTagBeforeExtension = nameParts.tagBeforeExtension;
  }

  function saveSplitRename(nextPath?: string) {
    if (!explorerSplit || !splitRenamingEntry || isExplorerFolderUpdateLocked()) {
      return;
    }

    const newName = buildEntryName(
      splitRenameValue,
      splitRenameIdentifier,
      splitRenameExtension,
      splitRenameTagBeforeExtension,
      splitRenamingEntry.isDirectory
    );
    if (!splitRenameValue.trim() || newName.length > 255) {
      showExplorerToast('識別タグを含めて255文字以内の名前を入力してください。', 'error');
      return;
    }

    explorerSplit = { ...explorerSplit, rightIsLoading: true };
    pendingRenameNavigation = nextPath ? { pane: 'right', targetPath: nextPath } : null;
    postHostMessage({
      type: 'explorer.rename',
      path: splitRenamingEntry.path,
      newName,
      directory: explorerSplit.rightPath,
      pane: 'split-right'
    });
    splitRenamingEntry = null;
  }

  function renameRelativeEntry(direction: -1 | 1, pane: 'left' | 'right') {
    const currentEntry = pane === 'left' ? renamingEntry : splitRenamingEntry;
    const entries = pane === 'left' ? filteredExplorerEntries : splitRightEntries;
    if (!currentEntry) {
      return;
    }

    const currentIndex = entries.findIndex((entry) => entry.path === currentEntry.path);
    const nextEntry = currentIndex >= 0 ? entries[currentIndex + direction] : undefined;
    if (pane === 'left') {
      saveRename(nextEntry?.path);
    }
    else {
      saveSplitRename(nextEntry?.path);
    }
  }

  function handleRenameDialogKeydown(event: KeyboardEvent, pane: 'left' | 'right') {
    if (event.defaultPrevented) {
      return;
    }

    closeRenameOnEscape(event, pane);
    if (event.defaultPrevented) {
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      renameRelativeEntry(1, pane);
    }
    else if (event.key === 'ArrowUp') {
      event.preventDefault();
      renameRelativeEntry(-1, pane);
    }
  }

  function handleRenameInputKeydown(event: KeyboardEvent, pane: 'left' | 'right') {
    if (event.key === 'Enter') {
      event.preventDefault();
      if (pane === 'left') {
        saveRename();
      }
      else {
        saveSplitRename();
      }
      return;
    }

    handleRenameDialogKeydown(event, pane);
  }

  function continueRenameAfterRefresh(pane: 'left' | 'right', entries: ExplorerEntry[]) {
    const pending = pendingRenameNavigation;
    if (!pending || pending.pane !== pane) {
      return;
    }

    pendingRenameNavigation = null;
    const nextEntry = entries.find((entry) => entry.path === pending.targetPath);
    if (!nextEntry) {
      return;
    }

    if (pane === 'left') {
      beginRenameForEntry(nextEntry);
    }
    else {
      beginSplitRenameForEntry(nextEntry);
    }
  }

  function requestSplitDelete() {
    if (!explorerSplit || isExplorerFolderUpdateLocked() || explorerSplit.rightSelectedPaths.length === 0) {
      showExplorerToast('削除する項目を選択してください。', 'error');
      return;
    }

    splitDeleteConfirmation = true;
  }

  function deleteSplitSelection() {
    if (!explorerSplit || isExplorerFolderUpdateLocked()) {
      return;
    }

    splitDeleteConfirmation = false;
    explorerSplit = { ...explorerSplit, rightIsLoading: true };
    postHostMessage({
      type: 'explorer.delete',
      paths: explorerSplit.rightSelectedPaths,
      directory: explorerSplit.rightPath,
      pane: 'split-right'
    });
  }

  function finishPendingMoveRefresh(destinationPane: 'left' | 'right', destinationPath: string) {
    const pending = pendingMoveRefresh;
    if (!pending || pending.destinationPane !== destinationPane || pending.destinationPath !== destinationPath) {
      return;
    }

    pendingMoveRefresh = null;
    cutClipboardSource = null;
    if (pending.sourcePane === destinationPane || pending.sourcePath === destinationPath) {
      return;
    }

    if (pending.sourcePane === 'left') {
      loadExplorer(pending.sourcePath);
    }
    else {
      loadSplitExplorer(pending.sourcePath);
    }
  }

  function isExplorerFolderUpdateLocked() {
    return explorerPasteInProgress !== null || gidAssignmentInProgress || creatorFolderConversionInProgress || creatorReassignmentInProgress;
  }

  function beginExplorerPaste(pane: 'left' | 'right', path: string) {
    explorerPasteInProgress = { pane, path };
    showExplorerToast('貼り付け中...', 'progress', null);
  }

  function finishExplorerPaste(pane: 'left' | 'right', path: string) {
    if (explorerPasteInProgress?.pane === pane && explorerPasteInProgress.path === path) {
      explorerPasteInProgress = null;
    }
  }

  function saveProgramRule() {
    postHostMessage({
      type: 'settings.externalApps.save',
      id: programId,
      name: programName,
      executablePath: programExecutablePath,
      launchOptions: programLaunchOptions,
      allowMultiple: programAllowMultiple,
      clickExtensions: programClickExtensions,
      doubleClickExtensions: programDoubleClickExtensions,
      contextMenuExtensions: programContextMenuExtensions
    });
  }

  function removeExtensionDots(extensions: string) {
    return extensions.replaceAll('.', '');
  }

  function parseGidTargetExtensions(value: string) {
    return value
      .split(';')
      .map((extension) => extension.trim().toLocaleLowerCase('en-US'))
      .filter(Boolean)
      .map((extension) => extension.replace(/^\.+/, ''))
      .filter(Boolean)
      .filter((extension, index, extensions) => extensions.indexOf(extension) === index);
  }

  function saveGidSettings() {
    postHostMessage({
      type: 'settings.gid.save',
      targetExtensions: gidSettings.targetExtensions
    });
  }

  function previewGidMigration() {
    if (gidMigrationPreviewInProgress || gidMigrationInProgress) return;
    gidMigrationTargetDigitCount = Math.max(4, Math.min(8, Math.round(Number(gidMigrationTargetDigitCount) || 6)));
    gidMigrationPreview = null;
    gidMigrationPreviewInProgress = true;
    postHostMessage({
      type: 'settings.gid.migration.preview',
      targetDigitCount: gidMigrationTargetDigitCount
    });
  }

  function executeGidMigration() {
    if (!gidMigrationPreview?.canExecute || gidMigrationInProgress) return;
    gidMigrationInProgress = true;
    gidMigrationProgress = 'バックアップを準備しています。';
    startGidMigrationProgressTimer();
    postHostMessage({
      type: 'settings.gid.migration.execute',
      targetDigitCount: gidMigrationPreview.targetDigitCount
    });
  }

  function closeGidMigrationPreview() {
    if (!gidMigrationInProgress) gidMigrationPreview = null;
  }

  function startGidMigrationProgressTimer() {
    finishGidMigrationProgressTimer();
    gidMigrationProgressStartedAt = Date.now();
    gidMigrationElapsedSeconds = 0;
    gidMigrationOverallPercent = 0;
    gidMigrationDatabasePercent = 0;
    gidMigrationProgressPhase = 'preflight';
    gidMigrationPhaseCompleted = 0;
    gidMigrationPhaseTotal = 1;
    gidMigrationProgressTimer = setInterval(() => {
      gidMigrationElapsedSeconds = Math.max(0, Math.floor((Date.now() - gidMigrationProgressStartedAt) / 1_000));
    }, 1_000);
  }

  function finishGidMigrationProgressTimer() {
    if (gidMigrationProgressTimer) {
      clearInterval(gidMigrationProgressTimer);
      gidMigrationProgressTimer = undefined;
    }
    if (gidMigrationProgressStartedAt > 0) {
      gidMigrationElapsedSeconds = Math.max(0, Math.floor((Date.now() - gidMigrationProgressStartedAt) / 1_000));
    }
  }

  function updateGidMigrationProgress(phase: string, completed: number, total: number) {
    const ranges: Record<string, [number, number]> = {
      preflight: [0, 3],
      backup: [3, 7],
      files: [7, 62],
      database_prepare: [62, 67],
      database: [67, 97],
      finalize: [97, 100],
      completed: [100, 100]
    };
    const [start, end] = ranges[phase] ?? [0, 100];
    const ratio = total > 0 ? Math.max(0, Math.min(1, completed / total)) : 0;
    gidMigrationProgressPhase = phase;
    gidMigrationPhaseCompleted = Math.max(0, completed);
    gidMigrationPhaseTotal = Math.max(0, total);
    gidMigrationOverallPercent = phase === 'completed'
      ? 100
      : Math.max(gidMigrationOverallPercent, start + (end - start) * ratio);
    if (phase === 'database_prepare') {
      gidMigrationDatabasePercent = Math.max(gidMigrationDatabasePercent, ratio * (5 / 35) * 100);
    }
    else if (phase === 'database') {
      gidMigrationDatabasePercent = Math.max(gidMigrationDatabasePercent, (5 / 35) * 100 + ratio * (30 / 35) * 100);
    }
    else if (phase === 'finalize' || phase === 'completed') {
      gidMigrationDatabasePercent = 100;
    }
  }

  function formatGidMigrationDuration(totalSeconds: number) {
    const safeSeconds = Math.max(0, Math.floor(totalSeconds));
    const hours = Math.floor(safeSeconds / 3_600);
    const minutes = Math.floor((safeSeconds % 3_600) / 60);
    const seconds = safeSeconds % 60;
    return hours > 0
      ? `${hours}時間 ${String(minutes).padStart(2, '0')}分 ${String(seconds).padStart(2, '0')}秒`
      : `${minutes}分 ${String(seconds).padStart(2, '0')}秒`;
  }

  function saveWinRarSettings() {
    postHostMessage({
      type: 'settings.winrar.save',
      executablePath: winRarSettings.executablePath,
      supportedExtensions: winRarSettings.supportedExtensions,
      showOpenInContextMenu: winRarSettings.showOpenInContextMenu
    });
  }

  function saveFfmpegSettings() {
    postHostMessage({
      type: 'settings.ffmpeg.save',
      executablePath: ffmpegSettings.executablePath,
      supportedExtensions: ffmpegSettings.supportedExtensions
    });
  }

  function saveThumbnailCacheTargets(targets: string[]) {
    thumbnailCacheTargets = targets;
    postHostMessage({ type: 'settings.thumbnailCache.saveTargets', targets });
  }

  function saveSearchEngineSettings() {
    postHostMessage({
      type: 'settings.searchEngine.save',
      provider: searchEngineSettings.provider,
      googleSearchUrlTemplate: searchEngineSettings.googleSearchUrlTemplate,
      braveApiKey: searchEngineSettings.braveApiKey,
      geminiApiKey: searchEngineSettings.geminiApiKey
    });
  }

  function focusPastedExplorerEntries(paths: string[], pane: 'left' | 'right') {
    if (paths.length === 0) {
      return;
    }

    const entries = pane === 'left'
      ? explorerEntries
      : explorerSplit?.rightEntries ?? [];
    const pastedPaths = paths.filter((path) => entries.some((entry) => entry.path === path));
    if (pastedPaths.length === 0) {
      return;
    }

    if (pane === 'left') {
      selectedPaths = pastedPaths;
      explorerSelectionAnchorPath = pastedPaths[0];
    }
    else if (explorerSplit) {
      explorerSplit = { ...explorerSplit, rightSelectedPaths: pastedPaths };
      splitSelectionAnchorPath = pastedPaths[0];
    }

    requestAnimationFrame(() => {
      const selector = `[data-explorer-path="${CSS.escape(pastedPaths[0])}"]`;
      const element = pane === 'left'
        ? document.querySelector<HTMLElement>(`.explorer-grid ${selector}, .explorer-detail-body ${selector}`)
        : document.querySelector<HTMLElement>(`.split-pane-right ${selector}`);
      element?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      element?.focus({ preventScroll: true });
    });
  }

  function selectGalleryTargetCategory(category: string) {
    galleryTargetCategory = category;
    const settings = galleryScanSettings.find((setting) => setting.category === category);
    galleryTargetExtensions = removeExtensionDots(settings?.supportedExtensions ?? '');
    galleryTargetCardAspect = getGalleryCardAspect(category);
    galleryTargetEnabledFilters = settings?.enabledFilters?.length
      ? [...settings.enabledFilters]
      : getDefaultGalleryEnabledFilters(category);
    galleryTargetFileNameLines = Number(settings?.fileNameLines ?? 3);
    galleryTargetCreatorLabel = settings?.creatorLabel?.trim() || 'Creator';
    galleryTargetTitleLabel = settings?.titleLabel?.trim() || 'Title';
    galleryTargetCharacterLabel = settings?.characterLabel?.trim() || 'Character';
    galleryTargetTagLabel = settings?.tagLabel?.trim() || 'Tag';
    galleryTargetCoreTitleLabel = settings?.coreTitleLabel?.trim() || 'Core title';
    galleryTargetCoreTagsLabel = settings?.coreTagsLabel?.trim() || 'Core tags';
    selectThumbnailAdjustmentCategory(category);
  }

  function applyGallerySectionDefinitions(value: unknown, preferredSettingsSection?: string) {
    const sections = Array.isArray(value)
      ? value
        .filter((section): section is GallerySectionDefinition => Boolean(
          section && typeof section.id === 'string' && section.id.trim() &&
          typeof section.label === 'string' && section.label.trim()))
        .map((section, index) => ({
          id: section.id.trim(),
          label: section.label.trim(),
          position: Number.isFinite(Number(section.position)) ? Number(section.position) : index
        }))
        .sort((left, right) => left.position - right.position || left.label.localeCompare(right.label, 'ja-JP'))
      : [];
    if (sections.length === 0) {
      return { galleryChanged: false, creatorChanged: false, metricsChanged: false };
    }

    const previousGallerySection = gallerySection;
    const previousCreatorSection = galleryCreatorSummarySection;
    const previousMetricsSection = userMetricsCategory;
    gallerySections = sections;
    const firstSection = sections[0].id;
    const exists = (section: string) => sections.some(candidate => candidate.id === section);
    sqliteDatabaseUpdateCategories = sqliteDatabaseUpdateCategories.filter(exists);
    databaseScanSchedules = databaseScanSchedules.map(schedule => ({
      ...schedule,
      categories: schedule.categories.filter(exists)
    }));
    filterEditorVisibleCategoriesDraft = filterEditorVisibleCategoriesDraft.filter(exists);
    gallerySection = exists(gallerySection) ? gallerySection : firstSection;
    galleryCreatorSummarySection = exists(galleryCreatorSummarySection) ? galleryCreatorSummarySection : firstSection;
    userMetricsCategory = exists(userMetricsCategory) ? userMetricsCategory : firstSection;
    thumbnailAdjustmentCategory = exists(thumbnailAdjustmentCategory) ? thumbnailAdjustmentCategory : firstSection;
    galleryTargetCategory = preferredSettingsSection && exists(preferredSettingsSection)
      ? preferredSettingsSection
      : exists(galleryTargetCategory) ? galleryTargetCategory : firstSection;
    return {
      galleryChanged: previousGallerySection !== gallerySection,
      creatorChanged: previousCreatorSection !== galleryCreatorSummarySection,
      metricsChanged: previousMetricsSection !== userMetricsCategory
    };
  }

  function addGallerySectionDraft() {
    gallerySectionDrafts = [...gallerySectionDrafts, { key: crypto.randomUUID(), label: '' }];
  }

  function updateGallerySectionDraft(key: string, label: string) {
    gallerySectionDrafts = gallerySectionDrafts.map(draft => draft.key === key ? { ...draft, label } : draft);
  }

  function createGallerySectionFromDraft(key: string) {
    const draft = gallerySectionDrafts.find(candidate => candidate.key === key);
    const label = draft?.label.trim() ?? '';
    if (!label) return;
    gallerySectionDrafts = gallerySectionDrafts.filter(candidate => candidate.key !== key);
    postHostMessage({ type: 'settings.gallerySections.create', label });
  }

  function removeGallerySectionDraft(key: string) {
    gallerySectionDrafts = gallerySectionDrafts.filter(candidate => candidate.key !== key);
  }

  function updateGallerySectionLabel(sectionId: string, label: string) {
    gallerySections = gallerySections.map(section => section.id === sectionId ? { ...section, label } : section);
  }

  function saveGallerySectionLabel(sectionId: string, label: string) {
    const normalized = label.trim();
    if (!normalized) {
      showExplorerToast('区分名を入力してください。', 'error');
      postHostMessage({ type: 'settings.galleryTargets.list' });
      return;
    }
    postHostMessage({ type: 'settings.gallerySections.rename', sectionId, label: normalized });
  }

  function deleteGallerySection(sectionId: string) {
    if (gallerySections.length <= 1) {
      showExplorerToast('区分は1件以上必要なため削除できません。', 'error');
      return;
    }
    postHostMessage({ type: 'settings.gallerySections.delete', sectionId });
  }

  function selectThumbnailAdjustmentCategory(category: string) {
    thumbnailAdjustmentCategory = category;
    const adjustment = thumbnailCropAdjustments.find((item) => item.category === category);
    thumbnailAdjustmentHorizontalOffset = Number(adjustment?.horizontalOffsetPercent ?? 0);
    thumbnailAdjustmentVerticalOffset = Number(adjustment?.verticalOffsetPercent ?? -10);
    thumbnailAdjustmentScale = Number(adjustment?.scalePercent ?? 100);
  }

  function saveThumbnailAdjustment() {
    postHostMessage({
      type: 'settings.thumbnailAdjustments.save',
      category: thumbnailAdjustmentCategory,
      horizontalOffsetPercent: Number(thumbnailAdjustmentHorizontalOffset) || 0,
      verticalOffsetPercent: Number(thumbnailAdjustmentVerticalOffset) || 0,
      scalePercent: Number(thumbnailAdjustmentScale) || 100
    });
  }

  function getGalleryCardAspect(category: string): 'portrait' | 'landscape' {
    const configuredAspect = galleryScanSettings.find((setting) => setting.category === category)?.cardAspect;
    return configuredAspect === 'landscape'
      ? 'landscape'
      : galleryCardAspectDefaults[category] ?? 'portrait';
  }

  function getGalleryFileNameLines(category: string) {
    const lines = Number(galleryScanSettings.find((setting) => setting.category === category)?.fileNameLines ?? 3);
    return Math.max(1, Math.min(4, lines || 3));
  }

  function saveGalleryScanTargets(paths: string[]) {
    const fileNameLines = Math.max(1, Math.min(4, Number(galleryTargetFileNameLines) || 3));
    const nextSettings: GalleryScanSettings = {
      category: galleryTargetCategory,
      supportedExtensions: galleryTargetExtensions,
      cardAspect: galleryTargetCardAspect,
      enabledFilters: [...galleryTargetEnabledFilters],
      fileNameLines,
      creatorLabel: galleryTargetCreatorLabel.trim() || 'Creator',
      titleLabel: galleryTargetTitleLabel.trim() || 'Title',
      characterLabel: galleryTargetCharacterLabel.trim() || 'Character',
      tagLabel: galleryTargetTagLabel.trim() || 'Tag',
      coreTitleLabel: galleryTargetCoreTitleLabel.trim() || 'Core title',
      coreTagsLabel: galleryTargetCoreTagsLabel.trim() || 'Core tags'
    };
    const existingIndex = galleryScanSettings.findIndex((setting) => setting.category === galleryTargetCategory);
    galleryScanSettings = existingIndex >= 0
      ? galleryScanSettings.map((setting, index) => index === existingIndex ? nextSettings : setting)
      : [...galleryScanSettings, nextSettings];
    galleryTargetFileNameLines = fileNameLines;
    postHostMessage({
      type: 'settings.galleryTargets.save',
      category: galleryTargetCategory,
      paths,
      supportedExtensions: galleryTargetExtensions,
      cardAspect: galleryTargetCardAspect,
      enabledFilters: galleryTargetEnabledFilters,
      fileNameLines,
      creatorLabel: nextSettings.creatorLabel,
      titleLabel: nextSettings.titleLabel,
      characterLabel: nextSettings.characterLabel,
      tagLabel: nextSettings.tagLabel,
      coreTitleLabel: nextSettings.coreTitleLabel,
      coreTagsLabel: nextSettings.coreTagsLabel
    });
    if (galleryCreatorSummarySection === galleryTargetCategory) {
      if (!galleryTargetEnabledFilters.includes('core_title')) galleryCreatorSummaryCoreTitles = [];
      if (!galleryTargetEnabledFilters.includes('core_tags')) galleryCreatorSummaryCoreTags = [];
    }
  }

  function saveGallerySectionSettings() {
    saveGalleryScanTargets(selectedGalleryScanTargets.map((target) => target.path));
    saveThumbnailAdjustment();
  }

  function getDefaultGalleryEnabledFilters(category: string): string[] {
    return category === 'av'
      ? ['rating', 'creator', 'character', 'tag', 'core_tags']
      : ['rating', 'creator', 'title', 'character', 'tag', 'core_title', 'core_tags'];
  }

  function isGalleryFilterEnabled(filter: string, category = gallerySection) {
    const configured = galleryScanSettings.find((setting) => setting.category === category)?.enabledFilters;
    return (configured?.length ? configured : getDefaultGalleryEnabledFilters(category)).includes(filter);
  }

  function getGalleryFilterLabel(
    filter: 'creator' | 'title' | 'character' | 'tag' | 'core_title' | 'core_tags',
    category = gallerySection) {
    const settings = galleryScanSettings.find((setting) => setting.category === category);
    const labels = {
      creator: settings?.creatorLabel,
      title: settings?.titleLabel,
      character: settings?.characterLabel,
      tag: settings?.tagLabel,
      core_title: settings?.coreTitleLabel,
      core_tags: settings?.coreTagsLabel
    };
    const defaults = {
      creator: 'Creator',
      title: 'Title',
      character: 'Character',
      tag: 'Tag',
      core_title: 'Core title',
      core_tags: 'Core tags'
    };
    return labels[filter]?.trim() || defaults[filter];
  }

  function setGalleryTargetTitleFilterEnabled(enabled: boolean) {
    galleryTargetEnabledFilters = enabled
      ? [...new Set([...galleryTargetEnabledFilters, 'title'])]
      : galleryTargetEnabledFilters.filter((filter) => filter !== 'title');
  }

  function setGalleryTargetCoreFilterEnabled(filter: 'core_title' | 'core_tags', enabled: boolean) {
    galleryTargetEnabledFilters = enabled
      ? [...new Set([...galleryTargetEnabledFilters, filter])]
      : galleryTargetEnabledFilters.filter((candidate) => candidate !== filter);
  }

  function addGalleryScanTarget() {
    const path = galleryTargetDraft.trim();
    if (!path || isExplorerFolderUpdateLocked()) {
      return;
    }

    const paths = selectedGalleryScanTargets.map((target) => target.path);
    if (!paths.some((target) => target.toLowerCase() === path.toLowerCase())) {
      saveGalleryScanTargets([...paths, path]);
    }
    galleryTargetDraft = '';
  }

  function removeGalleryScanTarget(path: string) {
    saveGalleryScanTargets(selectedGalleryScanTargets
      .map((target) => target.path)
      .filter((target) => target !== path));
  }

  function addThumbnailCacheTarget() {
    const path = thumbnailCacheTargetDraft.trim();
    if (!path) {
      return;
    }

    if (!thumbnailCacheTargets.some((target) => target.toLowerCase() === path.toLowerCase())) {
      saveThumbnailCacheTargets([...thumbnailCacheTargets, path]);
    }
    thumbnailCacheTargetDraft = '';
  }

  function saveThumbnailCacheRoot() {
    thumbnailCacheStatus = 'キャッシュを新しい保存先へ移動中...';
    postHostMessage({ type: 'settings.thumbnailCache.saveRoot', cacheRoot: thumbnailCacheRootDraft });
  }

  function removeThumbnailCacheTarget(path: string) {
    saveThumbnailCacheTargets(thumbnailCacheTargets.filter((target) => target !== path));
  }

  function maintainThumbnailCache() {
    thumbnailCacheStatus = 'キャッシュをメンテナンス中...';
    postHostMessage({ type: 'settings.thumbnailCache.maintain' });
  }

  function rebuildThumbnailCache() {
    thumbnailCacheStatus = 'キャッシュを再構築中...';
    postHostMessage({ type: 'settings.thumbnailCache.rebuild' });
  }

  function moveSqliteDatabase() {
    const directory = sqliteDatabasePathDraft.trim();
    if (!directory) {
      sqliteDatabaseStatus = 'SQLiteDBの保存先ディレクトリを入力してください。';
      return;
    }

    sqliteDatabaseBusy = true;
    sqliteDatabaseStatus = 'SQLiteDBを新しい保存先へ移動中...';
    postHostMessage({ type: 'settings.sqliteDatabase.move', directory });
  }

  function moveSqliteCacheDatabase() {
    const directory = sqliteCacheDatabasePathDraft.trim();
    if (!directory) {
      sqliteDatabaseStatus = 'キャッシュDBの保存先ディレクトリを入力してください。';
      return;
    }

    sqliteDatabaseBusy = true;
    sqliteDatabaseStatus = 'キャッシュDBを新しい保存先へコピー中...';
    postHostMessage({ type: 'settings.sqliteDatabase.cache.move', directory });
  }

  function pickSqliteDatabaseForMerge() {
    postHostMessage({ type: 'settings.sqliteDatabase.merge.pick' });
  }

  function mergeSqliteDatabase() {
    const path = sqliteMergeDatabasePath.trim();
    if (!path) {
      sqliteDatabaseStatus = '結合するSQLiteDBを選択してください。';
      return;
    }
    const canonicalLabel = sqliteMergeCanonical === 'selected' ? '選択したDB' : '現在使用中のDB';
    if (!window.confirm(
      `${canonicalLabel}を正として2つのDBを結合します。両方のDBをバックアップしてから処理します。続行しますか？`)) {
      return;
    }
    sqliteDatabaseBusy = true;
    sqliteDatabaseStatus = 'SQLiteDBをバックアップして結合しています...';
    postHostMessage({
      type: 'settings.sqliteDatabase.merge',
      path,
      canonical: sqliteMergeCanonical
    });
  }

  function getPCloudSettingsMessage(type: string) {
    return {
      type,
      apiHost: pCloudApiHost,
      targetFolder: pCloudTargetFolder.trim(),
      clientId: pCloudClientId.trim(),
      accessToken: pCloudAccessTokenDraft.trim(),
      autoBackupEnabled: pCloudAutoBackupEnabled,
      checkIntervalMinutes: pCloudCheckIntervalMinutes,
      backupIntervalDays: pCloudBackupIntervalDays,
      maximumSnapshots: pCloudMaximumSnapshots,
      idleThresholdMinutes: pCloudIdleThresholdMinutes
    };
  }

  function savePCloudSettings() {
    pCloudBusy = true;
    pCloudStatus = 'pCloudバックアップ設定を保存中...';
    postHostMessage(getPCloudSettingsMessage('settings.pcloud.save'));
  }

  function connectPCloud() {
    if (!pCloudClientId.trim()) {
      pCloudStatus = 'pCloud Developersで作成したアプリのClient IDを入力してください。';
      return;
    }

    pCloudBusy = true;
    pCloudStatus = '既定のブラウザでpCloudの認証を完了してください...';
    postHostMessage(getPCloudSettingsMessage('settings.pcloud.connect'));
  }

  function testPCloudConnection() {
    pCloudBusy = true;
    pCloudStatus = 'pCloudとの接続を確認中...';
    postHostMessage(getPCloudSettingsMessage('settings.pcloud.test'));
  }

  function backupToPCloud() {
    pCloudBusy = true;
    pCloudStatus = 'バックアップ処理を開始しています...';
    postHostMessage(getPCloudSettingsMessage('settings.pcloud.backup'));
  }

  function loadPCloudSnapshots() {
    pCloudBusy = true;
    pCloudStatus = 'pCloudのスナップショット一覧を取得しています...';
    postHostMessage({ type: 'settings.pcloud.snapshots.list' });
  }

  function restorePCloudSnapshot(snapshot: PCloudSnapshot) {
    if (!window.confirm(
      `${formatModifiedAt(snapshot.createdAt)} のスナップショットを本体DBへ復元します。\n` +
      `現在の本体DBをローカルにバックアップし、取得したDBを検証してからアプリを再起動します。続行しますか？`)) {
      return;
    }
    pCloudBusy = true;
    pCloudStatus = '復元用スナップショットを取得しています...';
    postHostMessage({ type: 'settings.pcloud.restore', fileId: snapshot.fileId });
  }

  function disconnectPCloud() {
    pCloudBusy = true;
    pCloudStatus = 'pCloud連携を解除中...';
    postHostMessage({ type: 'settings.pcloud.disconnect' });
  }

  function startDatabaseScheduledScanToast(message: string, estimatedSeconds: number | null) {
    finishDatabaseScheduledScanToast();
    databaseScheduledScanInProgress = true;
    databaseScheduledScanStartedAt = Date.now();
    databaseScheduledScanEstimatedSeconds = estimatedSeconds !== null && estimatedSeconds > 0
      ? Math.round(estimatedSeconds)
      : null;
    databaseScheduledScanBaseMessage = message;
    updateDatabaseScheduledScanToast();
    databaseScheduledScanToastTimer = setInterval(updateDatabaseScheduledScanToast, 1_000);
  }

  function updateDatabaseScheduledScanToast() {
    if (!databaseScheduledScanInProgress) return;
    const elapsedSeconds = Math.max(0, Math.floor((Date.now() - databaseScheduledScanStartedAt) / 1_000));
    const elapsedLabel = translateSystemText('経過時間', appLanguage);
    const estimateLabel = translateSystemText('予想所要時間', appLanguage);
    const estimate = databaseScheduledScanEstimatedSeconds === null
      ? translateSystemText('実績なし', appLanguage)
      : formatGidMigrationDuration(databaseScheduledScanEstimatedSeconds);
    const baseMessage = sqliteDatabaseCancelRequested
      ? translateSystemText('定期フォルダ走査を安全に中断しています...', appLanguage)
      : translateSystemText(databaseScheduledScanBaseMessage, appLanguage);
    showExplorerToast(
      `${baseMessage}\n${elapsedLabel} ${formatGidMigrationDuration(elapsedSeconds)} / ${estimateLabel} ${estimate}`,
      'progress',
      null,
      'cancelDatabaseScan');
  }

  function finishDatabaseScheduledScanToast() {
    if (databaseScheduledScanToastTimer) {
      clearInterval(databaseScheduledScanToastTimer);
      databaseScheduledScanToastTimer = undefined;
    }
    databaseScheduledScanInProgress = false;
    databaseScheduledScanStartedAt = 0;
    databaseScheduledScanEstimatedSeconds = null;
    databaseScheduledScanBaseMessage = '';
    if (explorerToastAction === 'cancelDatabaseScan') {
      explorerToastAction = null;
    }
  }

  function addDatabaseScanSchedule() {
    const id = typeof crypto?.randomUUID === 'function'
      ? crypto.randomUUID().replaceAll('-', '')
      : `schedule-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    databaseScanSchedules = [
      ...databaseScanSchedules,
      {
        id,
        weekdays: [new Date().getDay()],
        time: '03:00',
        categories: gallerySections.map(section => section.id),
        lastStartedAt: null
      }
    ];
  }

  function removeDatabaseScanSchedule(id: string) {
    databaseScanSchedules = databaseScanSchedules.filter(schedule => schedule.id !== id);
  }

  function updateDatabaseScanScheduleTime(id: string, time: string) {
    databaseScanSchedules = databaseScanSchedules.map(schedule =>
      schedule.id === id ? { ...schedule, time } : schedule);
  }

  function toggleDatabaseScanScheduleWeekday(id: string, weekday: number) {
    databaseScanSchedules = databaseScanSchedules.map(schedule => {
      if (schedule.id !== id) return schedule;
      const weekdays = schedule.weekdays.includes(weekday)
        ? schedule.weekdays.filter(value => value !== weekday)
        : [...schedule.weekdays, weekday].sort((left, right) => left - right);
      return { ...schedule, weekdays };
    });
  }

  function toggleDatabaseScanScheduleCategory(id: string, category: string) {
    databaseScanSchedules = databaseScanSchedules.map(schedule => {
      if (schedule.id !== id) return schedule;
      const categories = schedule.categories.includes(category)
        ? schedule.categories.filter(value => value !== category)
        : [...schedule.categories, category];
      return { ...schedule, categories };
    });
  }

  function databaseScanScheduleCategorySummary(schedule: DatabaseScanSchedule) {
    const labels = gallerySections
      .filter(section => schedule.categories.includes(section.id))
      .map(section => section.label);
    if (labels.length === 0) return '対象区分を選択';
    if (labels.length <= 2) return labels.join(' / ');
    return `${labels.length}区分を選択`;
  }

  function saveDatabaseScanSchedules() {
    if (databaseScanSchedules.some(schedule => schedule.weekdays.length === 0)) {
      sqliteDatabaseStatus = '各スケジュールに曜日を1つ以上指定してください。';
      return;
    }
    if (databaseScanSchedules.some(schedule => !/^([01]\d|2[0-3]):[0-5]\d$/.test(schedule.time))) {
      sqliteDatabaseStatus = '各スケジュールに有効な時刻を指定してください。';
      return;
    }
    if (databaseScanSchedules.some(schedule => schedule.categories.length === 0)) {
      sqliteDatabaseStatus = '各スケジュールに対象区分を1つ以上指定してください。';
      return;
    }

    databaseScanScheduleSaving = true;
    sqliteDatabaseStatus = 'フォルダ走査スケジュールを保存中...';
    postHostMessage({
      type: 'settings.sqliteDatabase.schedules.save',
      schedules: databaseScanSchedules
    });
  }

  function maintainSqliteDatabase() {
    sqliteDatabaseBusy = true;
    sqliteDatabaseStatus = 'SQLiteDBをメンテナンス中...';
    postHostMessage({ type: 'settings.sqliteDatabase.maintain' });
  }

  function updateSqliteDatabase() {
    if (sqliteDatabaseUpdateCategories.length === 0) {
      sqliteDatabaseStatus = '更新するギャラリー対象を選択してください。';
      return;
    }

    sqliteDatabaseBusy = true;
    sqliteDatabaseUpdateInProgress = true;
    sqliteDatabaseCancelRequested = false;
    sqliteDatabaseStatus = 'SQLiteDBを更新中...';
    sqliteDatabaseProgressLog = ['SQLiteDB更新を開始しました'];
    postHostMessage({ type: 'settings.sqliteDatabase.update', categories: sqliteDatabaseUpdateCategories });
  }

  function cancelSqliteDatabaseUpdate() {
    if (!sqliteDatabaseUpdateInProgress || sqliteDatabaseCancelRequested) {
      return;
    }

    sqliteDatabaseCancelRequested = true;
    sqliteDatabaseStatus = 'SQLiteDB更新の中断を要求しています...';
    appendSqliteDatabaseProgress(sqliteDatabaseStatus);
    if (databaseScheduledScanInProgress) {
      updateDatabaseScheduledScanToast();
    }
    postHostMessage({ type: 'settings.sqliteDatabase.update.cancel' });
  }

  function appendSqliteDatabaseProgress(message: string) {
    if (!message) {
      return;
    }

    sqliteDatabaseProgressLog = [...sqliteDatabaseProgressLog.slice(-119), message];
  }

  function deleteProgramRule(rule: ExternalAppRule) {
    postHostMessage({ type: 'settings.externalApps.delete', id: rule.id });
  }

  function startProgramRuleDrag(event: DragEvent, rule: ExternalAppRule) {
    draggedProgramRule = rule;
    event.dataTransfer?.setData('text/plain', String(rule.id));
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
    }
  }

  function reorderProgramRule(event: DragEvent, target: ExternalAppRule) {
    event.preventDefault();
    event.stopPropagation();
    const draggedId = Number(event.dataTransfer?.getData('text/plain'));
    const source = draggedProgramRule ?? externalAppRules.find((rule) => rule.id === draggedId) ?? null;
    if (!source || source.id === target.id) {
      return;
    }

    const sourceIndex = externalAppRules.findIndex((rule) => rule.id === source.id);
    const targetIndex = externalAppRules.findIndex((rule) => rule.id === target.id);
    if (sourceIndex < 0 || targetIndex < 0) {
      return;
    }

    const reordered = [...externalAppRules];
    reordered.splice(sourceIndex, 1);
    reordered.splice(targetIndex, 0, source);
    externalAppRules = reordered;
    draggedProgramRule = null;
    postHostMessage({ type: 'settings.externalApps.reorder', ids: reordered.map((rule) => rule.id) });
  }

  function saveNewTabCandidate() {
    if (!newTabCandidatePath.trim()) {
      showExplorerToast('フォルダパスを入力してください。', 'error');
      return;
    }

    postHostMessage({
      type: 'settings.newTabCandidates.save',
      path: newTabCandidatePath,
      label: newTabCandidateLabel
    });
    newTabCandidatePath = '';
    newTabCandidateLabel = '';
    selectedNewTabCandidatePath = '';
  }

  function copyNewTabCandidatePath() {
    const path = newTabCandidatePath.trim();
    if (!path) {
      showExplorerToast('クリップボードへ格納するフォルダパスを入力してください。', 'error');
      return;
    }
    postHostMessage({ type: 'explorer.path.copy', path });
  }

  function deleteNewTabCandidate(candidate: NewTabCandidate) {
    postHostMessage({ type: 'settings.newTabCandidates.delete', path: candidate.path });
    if (selectedNewTabCandidatePath === candidate.path) {
      selectedNewTabCandidatePath = '';
      newTabCandidatePath = '';
      newTabCandidateLabel = '';
    }
  }

  function addNewTabSeparator() {
    postHostMessage({ type: 'settings.newTabCandidates.separator.add' });
  }

  function selectNewTabCandidate(candidate: NewTabCandidate) {
    if (candidate.kind !== 'folder') {
      return;
    }
    selectedNewTabCandidatePath = candidate.path;
    newTabCandidatePath = candidate.path;
    newTabCandidateLabel = candidate.label;
  }

  function startNewTabCandidateDrag(event: DragEvent, candidate: NewTabCandidate) {
    draggedNewTabCandidate = candidate;
    event.dataTransfer?.setData('text/plain', candidate.path);
    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
    }
  }

  function reorderNewTabCandidate(event: DragEvent, target: NewTabCandidate) {
    event.preventDefault();
    const source = draggedNewTabCandidate;
    if (!source || source.path === target.path) {
      return;
    }

    const sourceIndex = newTabCandidates.findIndex((candidate) => candidate.path === source.path);
    const targetIndex = newTabCandidates.findIndex((candidate) => candidate.path === target.path);
    if (sourceIndex < 0 || targetIndex < 0) {
      return;
    }

    const reordered = [...newTabCandidates];
    reordered.splice(sourceIndex, 1);
    reordered.splice(targetIndex, 0, source);
    newTabCandidates = reordered;
    draggedNewTabCandidate = null;
    postHostMessage({ type: 'settings.newTabCandidates.reorder', paths: reordered.map((candidate) => candidate.path) });
  }

  function openNewExplorerTabMenu(event: MouseEvent | PointerEvent) {
    event.preventDefault();
    event.stopPropagation();
    explorerNewTabMenuOpen = true;
    postHostMessage({ type: 'settings.newTabCandidates.list' });
  }

  function handleNewExplorerTabPointerDown(event: PointerEvent) {
    if (event.button === 2) {
      openNewExplorerTabMenu(event);
    }
  }

  function duplicateActiveExplorerTab() {
    const activeTab = explorerTabs.find((tab) => tab.id === activeExplorerTabId);
    openNewExplorerTab(activeTab?.path ?? explorerPath, activeTab?.label ?? '新しいタブ');
  }

  function openNewExplorerTabFromCandidate(candidate: NewTabCandidate) {
    explorerNewTabMenuOpen = false;
    openNewExplorerTab(candidate.path, candidate.label);
  }

  function editProgramRule(rule: ExternalAppRule) {
    programId = rule.id;
    programName = rule.name;
    programExecutablePath = rule.executablePath;
    programLaunchOptions = rule.launchOptions;
    programAllowMultiple = rule.allowMultiple;
    programClickExtensions = rule.clickExtensions.replaceAll('.', '');
    programDoubleClickExtensions = rule.doubleClickExtensions.replaceAll('.', '');
    programContextMenuExtensions = rule.contextMenuExtensions.replaceAll('.', '');
  }

  function createProgramRule() {
    programId = null;
    programName = '';
    programExecutablePath = '';
    programLaunchOptions = '"{path}"';
    programAllowMultiple = false;
    programClickExtensions = '';
    programDoubleClickExtensions = '';
    programContextMenuExtensions = '';
  }

  function isRuleAssignedToExtension(extensions: string, extension: string) {
    return extensions.split(/[;,\s]+/)
      .filter(Boolean)
      .some((candidate) => candidate.replace(/^\./, '').toLowerCase() === extension.replace(/^\./, '').toLowerCase());
  }

  function hasExplorerLaunchRule(entry: ExplorerEntry, activation: 'single' | 'double') {
    if (entry.isDirectory) return false;
    return hasExternalAppLaunchRule(entry.extension, activation);
  }

  function hasGallerySingleClickLaunchRule(work: GalleryWork) {
    return hasGalleryLaunchRule(work, 'single');
  }

  function hasGalleryLaunchRule(work: GalleryWork, activation: 'single' | 'double') {
    const extension = work.path.match(/(\.[^\\/.]+)$/)?.[1] ?? '';
    return hasExternalAppLaunchRule(extension, activation);
  }

  function hasExternalAppLaunchRule(extension: string, activation: 'single' | 'double') {
    return externalAppRules.some((rule) => isRuleAssignedToExtension(
      activation === 'single' ? rule.clickExtensions : rule.doubleClickExtensions,
      extension
    ));
  }

  function getContextAppRules(entry: ExplorerEntry) {
    return externalAppRules.filter((rule) => isRuleAssignedToExtension(rule.contextMenuExtensions, entry.extension));
  }

  function openEntryWithProgram(rule: ExternalAppRule, entry: ExplorerEntry) {
    explorerContextMenu = null;
    postHostMessage({ type: 'explorer.externalApp.open', id: rule.id, path: entry.path });
  }

  function getUserMetricsRanking(
    dashboard: UserMetricsDashboard,
    metric: 'files' | 'images' | 'rating',
    entity: UserMetricsEntity
  ) {
    const rankings = metric === 'files'
      ? dashboard.fileRankings
      : metric === 'images'
        ? dashboard.imageRankings
        : dashboard.ratingRankings;
    return rankings[entity] ?? [];
  }

  function getUserMetricsRankValue(item: UserMetricsRankItem, metric: 'files' | 'images' | 'rating' | 'spend' | 'trackingDays') {
    if (metric === 'images') return item.imageCount;
    if (metric === 'rating') return item.totalRating;
    if (metric === 'spend') return item.spend;
    if (metric === 'trackingDays') return item.trackingDays;
    return item.fileCount;
  }

  function getUserMetricsRankPercent(items: UserMetricsRankItem[], item: UserMetricsRankItem, metric: 'files' | 'images' | 'rating' | 'spend' | 'trackingDays') {
    const maximum = Math.max(1, ...items.map(candidate => getUserMetricsRankValue(candidate, metric)));
    return Math.max(2, getUserMetricsRankValue(item, metric) / maximum * 100);
  }

  function formatUserMetricsCompact(value: number) {
    return new Intl.NumberFormat('ja-JP', { notation: 'compact', maximumFractionDigits: 1 }).format(value || 0);
  }

  function formatUserMetricsCurrency(value: number, currency = 'JPY') {
    return new Intl.NumberFormat('ja-JP', {
      style: 'currency',
      currency,
      maximumFractionDigits: currency === 'JPY' ? 0 : 2
    }).format(value || 0);
  }

  function getUserMetricsMetricLabel(key: string, metrics: CreatorTrackingSettingsMetricDraft[]) {
    if (key === 'overall') return '総合評価';
    return metrics.find(metric => metric.key === key)?.label ?? key;
  }

  function getUserMetricsMetricRanking(dashboard: UserMetricsDashboard, key: string) {
    return dashboard.metricRankings.find(ranking => ranking.key === key)?.items ?? [];
  }

  function getUserMetricsTrendX(index: number, count: number, width = 680) {
    return count <= 1 ? width / 2 : 34 + index * ((width - 68) / (count - 1));
  }

  function getUserMetricsTrendY(value: number, values: number[], height = 220) {
    const maximum = Math.max(1, ...values);
    return height - 28 - value / maximum * (height - 58);
  }

  function getUserMetricsTrendLine(
    trends: UserMetricsTrendPoint[],
    key: 'fileCount' | 'imageCount' | 'cumulativeSpend',
    width = 680,
    height = 220
  ) {
    const values = trends.map(point => Number(point[key]) || 0);
    return trends.map((point, index) =>
      `${getUserMetricsTrendX(index, trends.length, width)},${getUserMetricsTrendY(Number(point[key]) || 0, values, height)}`)
      .join(' ');
  }

  function getUserMetricsBubbleX(item: UserMetricsDashboard['titleBubbles'][number], items: UserMetricsDashboard['titleBubbles']) {
    const maximum = Math.max(1, ...items.map(candidate => candidate.totalRating));
    return 58 + item.totalRating / maximum * 548;
  }

  function getUserMetricsBubbleY(item: UserMetricsDashboard['titleBubbles'][number], items: UserMetricsDashboard['titleBubbles']) {
    const maximum = Math.max(1, ...items.map(candidate => candidate.targetTagFileCount));
    return 236 - item.targetTagFileCount / maximum * 174;
  }

  function getUserMetricsBubbleRadius(item: UserMetricsDashboard['titleBubbles'][number], items: UserMetricsDashboard['titleBubbles']) {
    const maximum = Math.max(1, ...items.map(candidate => candidate.fileCount));
    return 7 + Math.sqrt(item.fileCount / maximum) * 22;
  }

  function formatSize(size: number | null) {
    if (size === null) {
      return '-';
    }

    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = size;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit += 1;
    }
    return `${value >= 10 || unit === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
  }

  function formatModifiedAt(value: string) {
    return new Date(value).toLocaleString('ja-JP', { dateStyle: 'short', timeStyle: 'short' });
  }

</script>

<main
  class:settings-open={activeView === 'settings'}
  class:user-guide-open={activeView === 'userGuide'}
  class:filters-open={activeView === 'filters' || activeView === 'tags'}
  class:theme-light={appliedThemeSettings.theme === 'light'}
  class:theme-dark={appliedThemeSettings.theme === 'dark'}
  class="shell"
  style={getThemeCssVariables(appliedThemeSettings)}
>
  <aside class="sidebar" aria-label="Navigation">
    <div class="brand">
      <div class="brand-mark"><Grid3X3 size={18} /></div>
      <div>
        <strong>GalleryBrowser</strong>
        <span>{hostStatus}</span>
      </div>
    </div>

    <nav>
      <div class:nav-active={activeView === 'library'} class="gallery-nav-group">
        <button class="primary-nav-toggle" aria-expanded={galleryNavigationExpanded} onclick={toggleGalleryNavigation}>
          <PanelLeft size={17} /> <span>Gallery</span>
          {#if galleryNavigationExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
        </button>
        {#if galleryNavigationExpanded}
          <div class="gallery-subnav" data-i18n-skip aria-label="Gallery sections">
            {#each gallerySections as section}
              <button class:gallery-section-active={activeView === 'library' && gallerySection === section.id} onclick={() => selectGallerySection(section.id)}>
                {section.label}
              </button>
            {/each}
          </div>
        {/if}
      </div>
      <div
        class="explorer-nav-group"
        role="group"
        class:nav-active={activeView === 'explorer'}
        ondragover={handleExplorerBookmarkDragOver}
        ondrop={handleExplorerBookmarkDrop}
      >
        <button class="primary-nav-toggle" aria-expanded={explorerBookmarksExpanded} onclick={toggleExplorerNavigation}>
          <FolderOpen size={17} /> <span>Explorer</span>
          {#if explorerBookmarksExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
        </button>
        {#if explorerBookmarksExpanded}
          <div class="explorer-bookmarks" aria-label="Explorer bookmarks">
            {#each explorerBookmarks as bookmark}
              <div
                class="explorer-bookmark-row"
                role="listitem"
                draggable="true"
                ondragstart={(event) => startExplorerBookmarkDrag(event, bookmark)}
                ondragend={() => (draggedExplorerBookmark = null)}
                ondragover={(event) => event.preventDefault()}
                ondrop={(event) => reorderExplorerBookmark(event, bookmark)}
              >
                <button
                  class="explorer-bookmark"
                  title={`${bookmark.path} (中クリックで解除)`}
                  onclick={() => openExplorerBookmark(bookmark)}
                  onauxclick={(event) => deleteExplorerBookmark(event, bookmark)}
                >
                  {bookmark.label}
                </button>
                <button
                  class="explorer-bookmark-remove"
                  title="ブックマークを解除"
                  onclick={() => removeExplorerBookmark(bookmark)}
                >
                  <X size={14} />
                </button>
              </div>
            {/each}
          </div>
        {/if}
      </div>
      <button class:nav-active={activeView === 'bookmarks'} onclick={openBookmarks}><Bookmark size={17} /> Bookmark</button>
      <div class:nav-active={activeView === 'creators' || activeView === 'creatorTracking'} class="gallery-nav-group">
        <button class="primary-nav-toggle" aria-expanded={creatorsNavigationExpanded} onclick={toggleCreatorsNavigation}>
          <UserRound size={17} /> <span>Creators</span>
          {#if creatorsNavigationExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
        </button>
        {#if creatorsNavigationExpanded}
          <div class="gallery-subnav" data-i18n-skip aria-label="Creator sections">
            {#each gallerySections as section}
              <button class:gallery-section-active={activeView === 'creators' && galleryCreatorSummarySection === section.id} onclick={() => selectGalleryCreatorSummarySection(section.id)}>
                {section.label}
              </button>
            {/each}
          </div>
        {/if}
      </div>
      <button class:nav-active={activeView === 'filters'} onclick={() => setView('filters')}>
        <ListFilter size={17} /> Filters
      </button>
      <button class:nav-active={activeView === 'tags'} onclick={() => setView('tags')}><Tags size={17} /> Tags</button>
      <button class:nav-active={activeView === 'userMetrics'} onclick={() => setView('userMetrics')}>
        <ChartNoAxesCombined size={17} /> User Metrics
      </button>
      <button class:nav-active={activeView === 'board'} onclick={() => setView('board')}>
        <LayoutGrid size={17} /> Board
      </button>
      <button class:nav-active={activeView === 'calendar'} onclick={() => setView('calendar')}>
        <CalendarCheck size={17} /> Calendar
      </button>
      <button class:nav-active={activeView === 'settings'} onclick={() => setView('settings')}>
        <Settings size={17} /> Settings
      </button>
      <button class:nav-active={activeView === 'userGuide'} onclick={() => setView('userGuide')}>
        <BookOpenText size={17} /> User Guide
      </button>
    </nav>
  </aside>

  {#if activeView === 'settings'}
    <aside class="settings-sidebar" aria-label="Settings navigation">
      <button class="settings-category" onclick={() => (settingsAppearanceExpanded = !settingsAppearanceExpanded)}>
        <Palette size={17} />
        <span>Appearance</span>
        {#if settingsAppearanceExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
      </button>
      {#if settingsAppearanceExpanded}
        <div class="settings-subnav">
          <button class:settings-active={settingsSection === 'theme'} onclick={() => (settingsSection = 'theme')}>
            テーマ
          </button>
          <button class:settings-active={settingsSection === 'language'} onclick={() => (settingsSection = 'language')}>
            Language
          </button>
        </div>
      {/if}
      <button class="settings-category" onclick={() => (settingsBehaviorExpanded = !settingsBehaviorExpanded)}>
        <Wrench size={17} />
        <span>Behavior</span>
        {#if settingsBehaviorExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
      </button>
      {#if settingsBehaviorExpanded}
        <div class="settings-subnav">
          <button class:settings-active={settingsSection === 'creatorTracking'} onclick={() => (settingsSection = 'creatorTracking')}>
            Creator Tracking
          </button>
          <button class:settings-active={settingsSection === 'tabCandidates'} onclick={() => (settingsSection = 'tabCandidates')}>
            新規タブ候補
          </button>
          <button class:settings-active={settingsSection === 'gestures'} onclick={() => (settingsSection = 'gestures')}>
            マウスジェスチャ
          </button>
          <button class:settings-active={settingsSection === 'keyboardShortcuts'} onclick={() => (settingsSection = 'keyboardShortcuts')}>
            キーボードショートカット
          </button>
          <button class:settings-active={settingsSection === 'calendar'} onclick={() => (settingsSection = 'calendar')}>
            Calendar
          </button>
        </div>
      {/if}
      <button class="settings-category" onclick={() => (settingsFilesExpanded = !settingsFilesExpanded)}>
        <Folder size={17} />
        <span>Files</span>
        {#if settingsFilesExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
      </button>
      {#if settingsFilesExpanded}
        <div class="settings-subnav">
          <button class:settings-active={settingsSection === 'galleryTargets'} onclick={() => (settingsSection = 'galleryTargets')}>
            区分別の設定
          </button>
          <button class:settings-active={settingsSection === 'gid'} onclick={() => (settingsSection = 'gid')}>
            gid管理
          </button>
          <button class:settings-active={settingsSection === 'thumbnailCache'} onclick={() => (settingsSection = 'thumbnailCache')}>
            サムネイルキャッシュ
          </button>
          <button class:settings-active={settingsSection === 'sqliteDatabase'} onclick={() => (settingsSection = 'sqliteDatabase')}>
            データベース
          </button>
        </div>
      {/if}
      <button class="settings-category" onclick={() => (settingsAdvancedExpanded = !settingsAdvancedExpanded)}>
        <Code2 size={17} />
        <span>Advanced</span>
        {#if settingsAdvancedExpanded}<ChevronDown size={15} />{:else}<ChevronRight size={15} />{/if}
      </button>
      {#if settingsAdvancedExpanded}
        <div class="settings-subnav">
          <button class:settings-active={settingsSection === 'programs'} onclick={() => (settingsSection = 'programs')}>
            起動プログラム
          </button>
          <button class:settings-active={settingsSection === 'winrar'} onclick={() => (settingsSection = 'winrar')}>
            WinRAR設定
          </button>
          <button class:settings-active={settingsSection === 'ffmpeg'} onclick={() => (settingsSection = 'ffmpeg')}>
            FFmpeg設定
          </button>
          <button class:settings-active={settingsSection === 'searchEngine'} onclick={() => (settingsSection = 'searchEngine')}>
            検索エンジン
          </button>
        </div>
      {/if}
    </aside>
  {/if}

  {#if activeView === 'userGuide'}
    <aside class="user-guide-sidebar" aria-label="User Guide index">
      <div class="user-guide-index-heading">
        <span><BookOpenText size={18} /></span>
        <div><strong>INDEX</strong><small>ユーザーガイド</small></div>
      </div>
      <nav class="user-guide-index">
        {#each userGuideSections as section}
          <button
            class:active={activeUserGuideSection === section.id}
            aria-current={activeUserGuideSection === section.id ? 'page' : undefined}
            onfocus={() => (activeUserGuideSection = section.id)}
            onclick={() => navigateUserGuideSection(section.id)}
          >
            <span>{section.number}</span>
            <strong>{section.label}</strong>
          </button>
        {/each}
      </nav>
    </aside>
  {/if}

  <section class:gallery-workspace={activeView === 'library' || activeView === 'creators'} class:creator-tracking-workspace={activeView === 'creatorTracking'} class="workspace" use:clearGallerySelectionOnClick>
    <header class="toolbar">
      {#if activeView === 'explorer'}
        <div class="explorer-toolbar">
          <div class="path-input explorer-address" aria-label="フォルダパス">
            {#if explorerPathEditing}
              <input
                bind:this={explorerPathInputElement}
                bind:value={explorerPathDraft}
                aria-label="フォルダパスを入力"
                onblur={() => (explorerPathEditing = false)}
                onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    explorerPathEditing = false;
                    loadExplorer();
                  }
                  else if (event.key === 'Escape') {
                    explorerPathDraft = explorerPath;
                    explorerPathEditing = false;
                  }
                }}
              />
            {:else}
              <div class="breadcrumb-bar" role="navigation" aria-label="フォルダ階層">
                {#each explorerBreadcrumbs as crumb, index}
                  {#if index > 0}<ChevronRight size={14} />{/if}
                  {#if index === 0 && /^[a-z]:/i.test(crumb.label) && explorerRoots.length > 0}
                    <div class="explorer-drive-selector">
                      <button class="explorer-drive-trigger" class:open={explorerDriveMenuOpen} title="ドライブを選択" aria-haspopup="menu" aria-expanded={explorerDriveMenuOpen} onclick={toggleExplorerDriveMenu}>
                        {crumb.label}<ChevronDown size={12} />
                      </button>
                      {#if explorerDriveMenuOpen}
                        <div class="explorer-drive-menu" role="menu" aria-label="アクセス可能なドライブ" style={`left: ${explorerDriveMenuPosition.x}px; top: ${explorerDriveMenuPosition.y}px;`}>
                          {#each explorerRoots as root}
                            <button class:active={getExplorerDriveLabel(root) === crumb.label} type="button" role="menuitem" title={root} onclick={() => openExplorerDrive(root)}>
                              <Folder size={15} /><span>{getExplorerDriveLabel(root)}</span>
                            </button>
                          {/each}
                        </div>
                      {/if}
                    </div>
                  {:else}
                    <button title={crumb.path} onclick={() => openExplorerBreadcrumb(crumb.path)}>{crumb.label}</button>
                  {/if}
                {/each}
                <button class="breadcrumb-edit" title="フルパスを入力" onclick={beginExplorerPathEdit}>
                  <Pencil size={15} />
                </button>
              </div>
            {/if}
          </div>
          <button title="更新" onclick={() => loadExplorer(explorerPath)}><RefreshCw size={18} /></button>
          <button class="sticky-note-launch-button" title="Explorerに付箋を追加" onclick={createStickyNoteFromToolbar}><StickyNote size={18} /></button>
          <button class="view-bookmark-button" title="現在のExplorerをBookmark" onclick={captureViewBookmarkFromToolbar}><Bookmark size={18} /></button>
        </div>
        <label class="explorer-filter toolbar-filter">
          <Search size={16} />
          <input
            bind:this={explorerFilterInputElement}
            value={activeExplorerQuery}
            placeholder={explorerFilterPlaceholder}
            oninput={updateActiveExplorerQuery}
          />
        </label>
      {:else if activeView === 'filters'}
        <div class="filters-toolbar-heading">
          <strong>フィルタエディタ</strong>
          <span>フィルタセットの管理とGallery各区分へのマッピングを設定します</span>
        </div>
      {:else if activeView === 'tags'}
        <div class="filters-toolbar-heading">
          <strong>Tagエディタ</strong>
          <span>Tagの管理とGallery各区分へのマッピングを設定します</span>
        </div>
      {:else if activeView === 'userMetrics'}
        <div class="user-metrics-toolbar">
          <div class="filters-toolbar-heading">
            <strong>User Metrics</strong>
            <span>ライブラリ・Creator Tracking・課金記録を横断して可視化します</span>
          </div>
          <div class="user-metrics-toolbar-actions">
            <button class="sticky-note-launch-button" title="User Metricsに付箋を追加" onclick={createStickyNoteFromToolbar}><StickyNote size={18} /></button>
            <button title="User Metricsを再集計" disabled={userMetricsIsLoading} onclick={() => loadUserMetrics(userMetricsCategory, true)}><RefreshCw size={18} /></button>
          </div>
        </div>
      {:else if activeView === 'board'}
        <div class="board-toolbar">
          <div class="filters-toolbar-heading">
            <strong>Board</strong>
            <span>各画面とBookmarkに保存された付箋をまとめて管理します</span>
          </div>
          <div class="board-view-toggle" role="group" aria-label="Boardの表示方法">
            <button class:active={stickyNoteBoardMode === 'all'} onclick={() => stickyNoteBoardMode = 'all'}><LayoutGrid size={16} />すべて</button>
            <button class:active={stickyNoteBoardMode === 'grouped'} onclick={() => stickyNoteBoardMode = 'grouped'}><Layers3 size={16} />機能別</button>
          </div>
        </div>
      {:else if activeView === 'calendar'}
        <div class="calendar-toolbar">
          <div class="filters-toolbar-heading">
            <strong>Calendar</strong>
            <span>Creator Trackingの有効なサブスク更新予定を月・週ビューで確認します</span>
          </div>
          <div class="calendar-toolbar-actions">
            <button title="Calendarを更新" disabled={calendarIsLoading} onclick={loadCalendarSubscriptions}><RefreshCw size={18} /></button>
            <button class:connected={googleCalendarHasRefreshToken} title="Google Calendarへ今すぐ同期" disabled={googleCalendarBusy || !googleCalendarHasRefreshToken} onclick={syncGoogleCalendar}><CloudUpload size={18} />Google</button>
            <button title="Google Calendarへ取り込めるiCalendarファイルを保存" onclick={exportCalendarIcs}><CalendarCheck size={18} />ICS</button>
          </div>
        </div>
      {:else if activeView === 'userGuide'}
        <div class="filters-toolbar-heading">
          <strong>User Guide</strong>
          <span>GalleryBrowserの基本操作と機能別の使い方を確認できます</span>
        </div>
      {:else if activeView === 'bookmarks'}
        <div class="filters-toolbar-heading">
          <strong>Bookmark</strong>
          <span>Gallery・Explorer・Creatorの現在のビューを保存して再現します</span>
        </div>
      {:else if activeView === 'creatorTracking'}
        <div class="creator-tracking-toolbar">
          <button class="creator-tracking-back" onclick={closeCreatorTracking}>
            <ChevronLeft size={17} /> Creators
          </button>
          <div class="filters-toolbar-heading">
            <strong>Creator Tracking</strong>
            <span>作者の活動・保管・評価・課金状況を一か所に集約します</span>
          </div>
          <div class="creator-tracking-toolbar-actions">
            <button class="creator-tracking-new-button" title="Creator Trackingを新規作成" aria-label="Creator Trackingを新規作成" onclick={openCreatorTrackingNewDialog}><UserPlus size={18} /></button>
            <button class="creator-tracking-gallery-button" title="この作者をGalleryで表示" aria-label="この作者をGalleryで表示" disabled={!creatorTracking && !creatorTrackingSummary} onclick={navigateCreatorTrackingToGallery}><LayoutGrid size={18} /></button>
            <button class="creator-tracking-refresh-button" title="この作者の最新データを反映" aria-label="この作者の最新データを反映" disabled={!creatorTracking || creatorTrackingIsLoading || creatorTrackingIsSaving} onclick={refreshCreatorTracking}><RefreshCw size={18} /></button>
            <button class="creator-tracking-delete-button" title="作者データを削除" aria-label="作者データを削除" disabled={(!creatorTracking && !creatorTrackingSummary) || creatorTrackingDeleteInProgress} onclick={requestCreatorTrackingDelete}><Trash2 size={18} /></button>
            <button class="sticky-note-launch-button" title="Creator Trackingに付箋を追加" onclick={createStickyNoteFromToolbar}><StickyNote size={18} /></button>
            <button class="view-bookmark-button" title="現在のCreator TrackingをBookmark" onclick={captureViewBookmarkFromToolbar}><Bookmark size={18} /></button>
          </div>
        </div>
      {:else if activeView === 'creators'}
        <div class="filters-toolbar-heading">
          <strong>Creators</strong>
          <span>Creator単位で作品数・評価・画像数・更新日時を集計します</span>
        </div>
      {:else}
        <div class="settings-toolbar-heading">
          <strong>{settingsSectionDetails[settingsSection].title}</strong>
          <span>{settingsSectionDetails[settingsSection].description}</span>
        </div>
        {#if settingsSection === 'creatorTracking'}
          <button class="quiet-button" onclick={resetCreatorTrackingSettingsDraft}>初期値に戻す</button>
        {/if}
      {/if}
    </header>

    {#if activeView === 'creatorTracking' && creatorTrackingTabs.length > 0}
      <nav class="creator-tracking-tabs" aria-label="開いているCreator Tracking">
        <div class="creator-tracking-tab-strip" role="tablist">
          {#each creatorTrackingTabs as tab (tab.id)}
            {@const tabLabel = getCreatorTrackingTabLabel(tab)}
            <div class:active={tab.id === activeCreatorTrackingTabId} class="creator-tracking-tab">
              <button
                type="button"
                role="tab"
                aria-selected={tab.id === activeCreatorTrackingTabId}
                title={tab.creator}
                onclick={() => activateCreatorTrackingTab(tab.id)}
              >
                <span class="creator-tracking-tab-label">
                  {#each splitCreatorTrackingTabLabel(tabLabel) as segment}
                    <span class:full-width={segment.isFullWidth}>{segment.text}</span>
                  {/each}
                </span>
              </button>
              <button type="button" class="creator-tracking-tab-close" title="タブを閉じる" aria-label={`${tabLabel}を閉じる`} onclick={(event) => closeCreatorTrackingTab(event, tab.id)}><X size={13} /></button>
            </div>
          {/each}
          <button type="button" class="creator-tracking-tab-add" title="Creatorsから作者を追加" aria-label="Creatorsから作者を追加" onclick={openCreatorTrackingPicker}><Plus size={16} /></button>
          <button type="button" class="creator-tracking-tab-add" title="Creator Trackingを新規作成" aria-label="Creator Trackingを新規作成" onclick={openCreatorTrackingNewDialog}><UserPlus size={16} /></button>
        </div>
      </nav>
    {/if}

    {#if activeView === 'userGuide'}
      <section class="user-guide-content" aria-label="GalleryBrowser User Guide" onscroll={updateUserGuideSectionFromScroll}>
        <div class="user-guide-page">
          <section id="user-guide-overview" class="user-guide-section user-guide-hero-section">
            <div class="user-guide-hero">
              <div class="user-guide-hero-icon"><BookOpenText size={32} /></div>
              <div>
                <span class="user-guide-eyebrow">GALLERYBROWSER HANDBOOK</span>
                <h1>作品・作者・ファイルを<br />ひとつの流れで管理する</h1>
                <p>GalleryBrowserは、ローカルの作品ファイルを中心に、属性、タグ管理、ファイル操作、作者情報、課金記録、アナリティクス、自由なメモまでも一元管理するためのアプリです。</p>
              </div>
            </div>

            <div class="user-guide-purpose-grid">
              <article><span><FolderOpen size={20} /></span><strong>整理する</strong><p>ExplorerとDB管理機能を使い、実ファイルと登録情報を揃えて管理します。</p></article>
              <article><span><Search size={20} /></span><strong>見つける</strong><p>区分と属性を組み合わせ、目的の作品や作者へすばやく到達します。</p></article>
              <article><span><ChartNoAxesCombined size={20} /></span><strong>振り返る</strong><p>Creator TrackingとUser Metricsで、保有状況や評価、活動記録を可視化します。</p></article>
            </div>

            <div class="user-guide-note accent">
              <Sparkles size={19} />
              <div><strong>最初に覚える3つの単位</strong><p><b>区分</b>はライブラリの大分類、<b>属性</b>はCreator・Title・Character・Tagなどの絞り込み情報、<b>gid</b>は作品ファイルを一意に識別するIDです。</p></div>
            </div>
          </section>

          <section id="user-guide-firstSteps" class="user-guide-section">
            <header class="user-guide-section-heading"><span>02</span><div><h2>初期設定と基本の流れ</h2><p>フォルダを登録してから作品を探せるようになるまで</p></div></header>
            <ol class="user-guide-steps">
              <li><span>1</span><div><strong>本体DBの保存場所を決める</strong><p><b>Settings ＞ Files ＞ データベース</b>で本体DBとキャッシュDBの保存先を確認・設定します。特に本体DBは重要な登録情報を保存するため、遅くとも最初の走査より前に保存場所を決めることを推奨します。</p></div></li>
              <li><span>2</span><div><strong>区分と対象フォルダを登録</strong><p><b>Settings ＞ Files ＞ 区分別の設定</b>で区分を作り、対象ファイルの拡張子と対象ディレクトリを設定します。</p></div></li>
              <li><span>3</span><div><strong>ファイルを走査</strong><p><b>Settings ＞ Files ＞ データベース</b>の「SQLiteDBの手動更新」から対象区分を選び、更新を実行します。</p></div></li>
              <li><span>4</span><div><strong>Galleryで内容を確認</strong><p>左のGallery配下から区分を開き、作品カード、Creator、Title、Character、Tagが意図どおり表示されるか確認します。</p></div></li>
              <li><span>5</span><div><strong>属性と作者情報を整備</strong><p>作品カードの右クリックメニューからフィルターやTagを登録し、必要な作者はCreator Trackingへ追加します。</p></div></li>
              <li><span>6</span><div><strong>バックアップ方針を決める</strong><p>必要ならpCloudバックアップを設定します。キャッシュは再生成できますが、本体DBは定期的にスナップショットを保存してください。</p></div></li>
            </ol>
            <div class="user-guide-note warning"><TriangleAlert size={19} /><div><strong>走査前の確認</strong><p>対象ディレクトリと拡張子が広すぎると、意図しないファイルまで登録されます。最初は小さなフォルダで確認することを推奨します。</p></div></div>
          </section>

          <section id="user-guide-gallery" class="user-guide-section">
            <header class="user-guide-section-heading"><span>03</span><div><h2>Gallery</h2><p>属性で絞り込み、作品を評価・整理するメインビュー</p></div></header>
            <div class="user-guide-feature-grid">
              <article><h3>属性フィルタ</h3><p>Rating、Creator、Title、Character、Tagを選んで表示作品を絞り込みます。Expandで候補を展開し、Collapseの右クリックで展開状態をピン留めできます。</p></article>
              <article><h3>Filters Sort</h3><p>属性ボタンの並びをRating、Files、Abcで変更します。複数の条件を選ぶと選択順が優先順位になり、右クリックで条件を解除します。</p></article>
              <article><h3>Thumbnail Sort</h3><p>作品カードをRating、Pics、Access date、Filepathで並べ替えます。複数条件、昇順・降順、右クリック解除の操作はFilters Sortと共通です。</p></article>
              <article><h3>Utilities</h3><p>検索、Bookmark、付箋、選択CreatorのTracking表示、登録済みストレージをExplorerで開く操作をまとめています。</p></article>
            </div>
            <div class="user-guide-operation-list">
              <div><span>01</span><p><strong>カードを右クリック</strong>すると、逆引きフィルタ、フィルターの登録と解除、Tagの登録と解除、ファイル削除などを実行できます。</p></div>
              <div><span>02</span><p><strong>逆引きフィルタ</strong>は選択作品のTitleまたはTitle＋Characterから、Galleryの選択状態を組み直します。</p></div>
              <div><span>03</span><p><strong>Reset</strong>はCreator・Title・Character・Tagの選択をまとめて解除します。</p></div>
              <div><span>04</span><p><strong>カードは複数選択に対応</strong>しています。Ctrl＋クリックで個別に追加・解除し、Shift＋クリックで基準カードから範囲選択できます。複数作品への属性・Tag操作にも利用できます。</p></div>
              <div><span>05</span><p>作品を1件選択して付箋を作ると、その作品に紐づく付箋になります。作品が表示対象外になると付箋も非表示になります。</p></div>
            </div>
          </section>

          <section id="user-guide-explorer" class="user-guide-section">
            <header class="user-guide-section-heading"><span>04</span><div><h2>Explorer</h2><p>タブ・分割表示に対応したローカルファイル操作</p></div></header>
            <div class="user-guide-two-column">
              <div>
                <h3>表示と移動</h3>
                <ul><li>＋ボタンでタブを追加し、右クリックメニューから登録済み候補を開けます。</li><li>開いているタブを左側メインパネルのExplorerへドラッグ＆ドロップすると、そのフォルダをクイックアクセスとして登録できます。</li><li>分割表示では左右それぞれにフォルダを表示し、フォーカス中のペインへ操作を行います。</li><li>アドレス欄のドライブ名から、アクセス可能な別ドライブへ切り替えられます。</li><li>表示枚数モードやスクロール状態はBookmarkへ保存できます。</li></ul>
              </div>
              <div>
                <h3>右クリックメニュー</h3>
                <ul><li><b>DB管理機能</b>：gid発行、作者フォルダ化などを実行します。</li><li><b>zipに変換</b>：選択したRARを書庫構造とファイル名を保ってZIPへ変換します。</li><li><b>Galleryへ移動</b>／<b>Creator Trackingへ移動</b>：作者フォルダから対応画面へ移動します。</li><li>登録した起動プログラムを使い、拡張子に応じた外部アプリで開けます。</li></ul>
              </div>
            </div>
            <div class="user-guide-note"><Hash size={19} /><div><strong>gid発行について</strong><p>発行桁数と対象拡張子はSettingsで管理します。発行後は対象フォルダを走査し、DB登録とサムネイルキャッシュ作成が続けて行われます。</p></div></div>
            <div class="user-guide-note accent"><Archive size={19} /><div><strong>RARをZIPへ変換する理由</strong><p>RARのままでは書庫からサムネイルを取得できなかったり、書庫内の画像枚数を集計できない場合があります。ZIPへ変換することで、Galleryのサムネイル表示と画像枚数の集計を安定させます。</p></div></div>
          </section>

          <section id="user-guide-organize" class="user-guide-section">
            <header class="user-guide-section-heading"><span>05</span><div><h2>Filters・Tags</h2><p>Galleryで使用する属性とTagの定義・割り当て</p></div></header>
            <div class="user-guide-feature-grid compact">
              <article><h3>フィルタエディタ</h3><p>Category、Title、Characterの標準名・別名を管理し、Galleryの各区分へマッピングします。</p></article>
              <article><h3>Import / Export</h3><p>一覧ファイルを使って定義をまとめて確認・更新できます。Import前にIDと更新列の対応を確認してください。</p></article>
              <article><h3>Tagエディタ</h3><p>Tagの追加、削除、並び替えと、各区分で使用できるTagのマッピングを設定します。</p></article>
              <article><h3>作品への登録</h3><p>Galleryの作品カードから、Title・Character・Tagを登録または解除します。新規項目は各登録画面からエディタへ移動して追加できます。</p></article>
            </div>
          </section>

          <section id="user-guide-creators" class="user-guide-section">
            <header class="user-guide-section-heading"><span>06</span><div><h2>Creators・Creator Tracking</h2><p>作者単位の集計と継続的なフォローアップ</p></div></header>
            <div class="user-guide-two-column">
              <div><h3>Creators</h3><ul><li>Creators配下の区分を選ぶと、作者カードを一覧表示します。</li><li>Rating、Site、Core title、Core tags、作品傾向などで絞り込めます。</li><li>総評価、最終確認日、フォロー日数、課金額などを最大3条件で複合ソートできます。</li><li>作者カードの右クリックからCreator Trackingを開きます。</li></ul></div>
              <div><h3>Creator Tracking</h3><ul><li>作者基本情報、作品の傾向、活動場所、ストレージ、課金・購入履歴を作者ごとに記録します。</li><li>複数作者をタブで開き、切り替え時やアプリ終了時に自動保存します。</li><li>更新アイコンは、その作者のGallery用途フォルダを走査して最新情報を反映します。</li><li>SUMMARYではファイル数・画像枚数・評価・課金・構成比・書庫履歴を確認できます。</li></ul></div>
            </div>
            <div class="user-guide-note"><Layers3 size={19} /><div><strong>Core title・Core tagsとは</strong><p>作者ごとにTitle／Tag別のファイル数を集計し、その作者の総ファイル数に対して30%以上を占めるTitleをCore title、TagをCore tagsとして扱います。作者の中心的な作品傾向を素早く把握するための指標です。</p></div></div>
            <div class="user-guide-note accent"><CalendarCheck size={19} /><div><strong>フォローアップ</strong><p>活動場所のフォローアップをONにして日数を設定すると、最終確認日からの経過日数に応じてCreatorsのWarning／Alertフィルタを利用できます。</p></div></div>
          </section>

          <section id="user-guide-bookmarks" class="user-guide-section">
            <header class="user-guide-section-heading"><span>07</span><div><h2>Bookmark・Sticky Notes・Board</h2><p>作業状態を保存し、画面をまたいでメモを管理</p></div></header>
            <div class="user-guide-card-stack">
              <article><span><Bookmark size={21} /></span><div><h3>Bookmark</h3><p>Galleryのフィルタやソート、Explorerのタブと分割、Creators／Creator Trackingのタブなど、現在のビューを名前とサムネイル付きで保存します。復元できない要素がある場合は、可能な範囲を再現して内容を通知します。</p></div></article>
              <article><span><StickyNote size={21} /></span><div><h3>Sticky Notes</h3><p>画面ごとの付箋です。ドラッグで移動、端のドラッグでサイズ変更、色変更、プレーンテキスト／Markdown切替ができます。画面や区分、フォルダ、作者に紐づいて保存されます。</p></div></article>
              <article><span><LayoutGrid size={21} /></span><div><h3>Board</h3><p>アプリ内とBookmarkに保存された付箋を一覧管理します。本文、色、表示モード、削除の変更は元の付箋にも反映されます。</p></div></article>
            </div>
          </section>

          <section id="user-guide-metrics" class="user-guide-section">
            <header class="user-guide-section-heading"><span>08</span><div><h2>User Metrics</h2><p>区分ごとの保有状況・評価・作者傾向を横断集計</p></div></header>
            <p class="user-guide-lead">区分切替トグルで集計対象を選び、ファイル数、画像枚数、評価、作者の作品傾向、サイト、課金額、フォロー日数をダッシュボード形式で確認します。</p>
            <div class="user-guide-metric-list">
              <div><strong>ランキング</strong><span>Creator・Title・Character・Tagを同じ指標で比較</span></div>
              <div><strong>推移</strong><span>ファイル数、画像枚数、課金額の時間変化を確認</span></div>
              <div><strong>関連性</strong><span>評価、ファイル規模、特定Tagなど複数軸の関係を可視化</span></div>
              <div><strong>再集計</strong><span>右上の更新ボタンで現在のDBから最新の指標を生成</span></div>
            </div>
          </section>

          <section id="user-guide-settings" class="user-guide-section">
            <header class="user-guide-section-heading"><span>09</span><div><h2>Settings</h2><p>設定は目的別の4カテゴリに分類されています</p></div></header>
            <div class="user-guide-settings-map">
              <article><span><Palette size={19} /></span><div><h3>Appearance</h3><p>テーマ、アクセントカラー、表示言語など、アプリ全体の見た目を設定します。</p></div></article>
              <article><span><Wrench size={19} /></span><div><h3>Behavior</h3><p>Creator Trackingの既定値、新規タブ候補、マウスジェスチャ、キーボードショートカットを設定します。</p></div></article>
              <article><span><Folder size={19} /></span><div><h3>Files</h3><p>区分と走査対象、gid、サムネイルキャッシュ、本体DB・キャッシュDB・バックアップを管理します。</p></div></article>
              <article><span><Code2 size={19} /></span><div><h3>Advanced</h3><p>起動プログラム、WinRAR、FFmpeg、検索エンジンなど外部機能との連携を設定します。</p></div></article>
            </div>
          </section>

          <section id="user-guide-data" class="user-guide-section">
            <header class="user-guide-section-heading"><span>10</span><div><h2>DB・キャッシュ・バックアップ</h2><p>失いたくないデータと再生成できるデータを分けて管理</p></div></header>
            <div class="user-guide-data-table">
              <div class="head"><span>データ</span><span>主な内容</span><span>扱い</span></div>
              <div><strong>本体DB</strong><span>作品、属性、Creator Tracking、Bookmark、付箋など</span><em>定期バックアップ推奨</em></div>
              <div><strong>キャッシュDB</strong><span>高速表示のための計算・参照キャッシュ</span><em>再生成可能</em></div>
              <div><strong>サムネイル</strong><span>Gallery／Explorerで使用する縮小画像</span><em>再構築可能</em></div>
              <div><strong>JSON設定</strong><span>アプリUIや外部プログラムの設定</span><em>環境ごとに保持</em></div>
            </div>
            <div class="user-guide-note warning"><Archive size={19} /><div><strong>本体DBの移動・結合</strong><p>移動や外部DB結合の前にはバックアップを作成します。pCloudは任意機能で、アイドル時の自動バックアップ頻度と保持世代数を設定できます。</p></div></div>
            <div class="user-guide-pcloud-setup">
              <div class="user-guide-pcloud-heading"><CloudUpload size={20} /><div><strong>pCloud連携の準備</strong><span>pCloud DevelopersのMy AppsからGalleryBrowserへ接続するまで</span></div></div>
              <ol>
                <li><span>1</span><p>pCloud Developersへログインし、<b>My Apps</b>を開きます。アプリ作成が一時的に利用できない場合は、pCloudサポートへMy Appsの利用またはアプリ作成を依頼します。</p></li>
                <li><span>2</span><p>利用可能になったMy Appsでアプリを作成し、発行された<b>OAuth Client ID</b>を控えます。</p></li>
                <li><span>3</span><p>GalleryBrowserの<b>Settings ＞ Files ＞ データベース ＞ pCloud バックアップ</b>でデータ保存リージョン、pCloud内の保存先、OAuth Client IDを入力して設定を保存します。</p></li>
                <li><span>4</span><p>画面に表示される<b>Redirect URI</b>をpCloud側のアプリ設定へ登録し、pCloud側でも保存します。</p></li>
                <li><span>5</span><p>GalleryBrowserで<b>OAuth連携</b>を押し、ブラウザでアクセスを許可します。完了表示を確認したらブラウザを閉じ、<b>接続確認</b>を実行します。</p></li>
                <li><span>6</span><p>バックアップ機能をONにし、確認頻度、実行間隔、保持件数、アイドル判定時間を設定して保存します。必要に応じて「今すぐバックアップ」で初回スナップショットを確認します。</p></li>
              </ol>
            </div>
          </section>

          <section id="user-guide-shortcuts" class="user-guide-section">
            <header class="user-guide-section-heading"><span>11</span><div><h2>キーボードショートカット</h2><p>初期設定。Settingsから割り当てを変更できます</p></div></header>
            <div class="user-guide-shortcut-grid">
              <div><kbd>Ctrl</kbd><i>＋</i><kbd>F</kbd><span>Gallery／Explorerの検索</span></div>
              <div><kbd>Ctrl</kbd><i>＋</i><kbd>Tab</kbd><span>次のタブへ移動</span></div>
              <div><kbd>Ctrl</kbd><i>＋</i><kbd>Shift</kbd><i>＋</i><kbd>Tab</kbd><span>前のタブへ移動</span></div>
              <div><kbd>Alt</kbd><i>＋</i><kbd>↑</kbd><span>Explorerで親フォルダへ</span></div>
              <div><kbd>Ctrl</kbd><i>＋</i><kbd>N</kbd><span>新しいフォルダを作成</span></div>
              <div><kbd>F2</kbd><span>選択項目の名前を変更</span></div>
              <div><kbd>Delete</kbd><span>選択項目を削除</span></div>
              <div><kbd>Ctrl</kbd><i>＋</i><kbd>C / X / V</kbd><span>コピー／切り取り／貼り付け</span></div>
            </div>
            <p class="user-guide-footnote">ExplorerとCreator Trackingのタブ切替は同じショートカットを使用します。入力欄にフォーカスがある場合は、文字編集の操作が優先されることがあります。</p>
          </section>

          <section id="user-guide-troubleshooting" class="user-guide-section">
            <header class="user-guide-section-heading"><span>12</span><div><h2>困ったときは</h2><p>まず確認する場所と安全な切り分け方</p></div></header>
            <div class="user-guide-troubleshooting-list">
              <details open><summary>Galleryに作品が表示されない</summary><p>区分の対象ディレクトリと拡張子、フィルタ選択、検索文字列を確認します。Reset後も表示されなければ、データベースの手動更新を実行してください。</p></details>
              <details><summary>ファイル操作後に表示が古い</summary><p>各画面の更新ボタンを使用します。作者フォルダの内容はCreator Tracking右上の更新から作者単位で走査できます。</p></details>
              <details><summary>Bookmarkを完全に復元できない</summary><p>保存後に削除・無効化されたフィルタ、存在しなくなったフォルダ、閉じられた作者データは復元できません。表示される確認内容をもとに現在の設定を確認してください。</p></details>
              <details><summary>サムネイルが表示されない／古い</summary><p>Settings ＞ Files ＞ サムネイルキャッシュで保存先と対象ディレクトリを確認し、必要な範囲だけ再構築します。</p></details>
              <details><summary>DBを安全に保ちたい</summary><p>本体DBの場所を確認し、クラウド同期による直接ロックを避けます。pCloudのスナップショットまたは別媒体への定期コピーを利用してください。</p></details>
            </div>
            <div class="user-guide-critical-notice"><OctagonAlert size={28} /><div><strong>本運用へ広げる前に</strong><span>設定や機能を変更した後は、必ず小さな対象範囲で結果を確認してから本運用へ広げてください。</span></div></div>
          </section>

          <section id="user-guide-acknowledgements" class="user-guide-section user-guide-acknowledgements">
            <header class="user-guide-section-heading"><span>13</span><div><h2>謝辞</h2><p>GalleryBrowserの発想と開発を支えたソフトウェア・技術へ</p></div></header>
            <div class="user-guide-acknowledgement-lead">
              <Sparkles size={24} />
              <p>GalleryBrowserは、優れた先行ソフトウェアから得た着想と、公開されたソース、そしてAIを活用した開発環境が結びついて生まれました。</p>
            </div>
            <div class="user-guide-acknowledgement-list">
              <article><span>01</span><div><h3>ZipPla</h3><p>このアプリを作るきっかけとなったのは、書庫と画像を快適に扱えるZipPlaです。作品ファイルを中心に閲覧・整理する体験の出発点になりました。</p></div></article>
              <article><span>02</span><div><h3>公開されたZipPlaのソース</h3><p>ZipPlaのソースをforkし、Git上に公開してくださった方がいたことで、動作や設計を学び、新しいアプリとして発展させるための大切な手がかりを得られました。</p></div></article>
              <article><span>03</span><div><h3>FenrirFS</h3><p>ファイルを実体の場所だけに縛らず整理するFenrirFSのエイリアス管理から、GalleryBrowserの属性・別名管理の考え方に大きな影響を受けています。</p></div></article>
              <article><span>04</span><div><h3>Codex</h3><p>Codexによって、構想をコードへ落とし込み、実際に動くアプリとして継続的に開発することが可能になりました。</p></div></article>
            </div>
            <div class="user-guide-thanks"><BookOpenText size={20} /><span>これらのソフトウェア、公開活動、開発技術に深く感謝します。</span></div>
          </section>
        </div>
      </section>
    {:else if activeView === 'settings'}
      <section class="content">
        {#if settingsSection === 'programs'}
          <div class="section-title">
            <div>
              <h1>起動プログラム</h1>
              <p>ZipPla と同様に、クリック起動と右クリックメニューへの表示を個別に設定します</p>
            </div>
          </div>

          <div class="program-settings-layout">
            <section class="program-rule-table" aria-label="登録済み起動プログラム">
              <div class="program-rule-head" aria-hidden="true">
                <span>表示名</span>
                <span>起動オプション</span>
                <span>複数</span>
                <span>シングルクリックで起動</span>
                <span>ダブルクリックで起動</span>
                <span>右クリックメニュー</span>
              </div>
              {#if externalAppRules.length === 0}
                <div class="empty compact">起動プログラムはまだ登録されていません。</div>
              {:else}
                {#each externalAppRules as rule}
                  <article
                    class:program-rule-selected={programId === rule.id}
                    class="program-rule-row"
                    draggable="true"
                    ondragstart={(event) => startProgramRuleDrag(event, rule)}
                    ondragend={() => (draggedProgramRule = null)}
                    ondragover={(event) => event.preventDefault()}
                    ondrop={(event) => reorderProgramRule(event, rule)}
                  >
                    <button class="program-rule-main" onclick={() => editProgramRule(rule)}>
                      <strong>{rule.name}</strong>
                      <span title={rule.launchOptions}>{rule.launchOptions}</span>
                      <span class:program-check={rule.allowMultiple}>{rule.allowMultiple ? '✓' : ''}</span>
                      <span>{removeExtensionDots(rule.clickExtensions) || '-'}</span>
                      <span>{removeExtensionDots(rule.doubleClickExtensions) || '-'}</span>
                      <span>{rule.contextMenuExtensions || '-'}</span>
                    </button>
                  </article>
                {/each}
              {/if}
            </section>

            <div class="program-table-actions">
              <button onclick={createProgramRule}>追加</button>
              <button class="quiet-button" disabled={programId === null} onclick={() => {
                const rule = externalAppRules.find((item) => item.id === programId);
                if (rule) deleteProgramRule(rule);
                createProgramRule();
              }}>削除</button>
            </div>

            <section class:program-editor-editing={programId !== null} class="settings-panel program-editor">
              <div class="program-editor-heading">
                <h2>{programId === null ? '起動プログラムを追加' : '既存の起動プログラムを編集中'}</h2>
                <span>{programId === null ? '追加する設定を入力してください' : `編集中: ${programName}`}</span>
              </div>
              <div class="field-grid program-editor-fields">
                <label>
                  <span>表示名</span>
                  <input bind:value={programName} placeholder="Zipビューア" />
                </label>
                <label>
                  <span>実行ファイル</span>
                  <span class="program-executable-input">
                    <input bind:value={programExecutablePath} placeholder="C:\Program Files\App\App.exe" />
                    <button type="button" title="実行ファイルを選択" onclick={() => postHostMessage({ type: 'settings.externalApps.pickExecutable' })}>
                      <FolderOpen size={17} />
                    </button>
                  </span>
                </label>
                <label>
                  <span>起動オプション</span>
                  <input bind:value={programLaunchOptions} placeholder={'"{path}"'} />
                </label>
                <label>
                  <span>シングルクリックで起動</span>
                  <input bind:value={programClickExtensions} placeholder="zip, rar" />
                </label>
                <label>
                  <span>ダブルクリックで起動</span>
                  <input bind:value={programDoubleClickExtensions} placeholder="zip, rar" />
                </label>
                <label>
                  <span>右クリックメニュー</span>
                  <input bind:value={programContextMenuExtensions} placeholder="zip, rar" />
                </label>
                <label class="program-multiple-toggle">
                  <input type="checkbox" bind:checked={programAllowMultiple} />
                  <span>複数起動を許可する</span>
                </label>
              </div>
              <div class="settings-actions">
                <button onclick={saveProgramRule}>{programId === null ? '追加' : '更新'}</button>
              </div>
            </section>
          </div>
        {:else if settingsSection === 'theme'}
          <div class="theme-settings-page">
            <section class="settings-panel theme-settings-editor">
              <div class="theme-settings-heading">
                <div>
                  <h2>テーマとアクセントカラー</h2>
                  <p>編集内容はExpectedで確認できます。保存するまではアプリ全体へ反映されません。</p>
                </div>
              </div>

              <div class="theme-settings-form">
                <label class="theme-mode-field">
                  <span>テーマ</span>
                  <select bind:value={themeSettingsDraft.theme}>
                    <option value="light">ライト（実験機能）</option>
                    <option value="dark">ダーク</option>
                  </select>
                </label>

                <div class="theme-accent-heading">
                  <div>
                    <strong>アクセントカラー</strong>
                    <span>選択状態や主要操作に使用する2色を設定します</span>
                  </div>
                  <button type="button" class="theme-recommend-button" title="現在のテーマに合う配色をランダムに提案" onclick={recommendThemeColors}>
                    <Sparkles size={17} />
                    レコメンド
                  </button>
                </div>

                <div class="theme-accent-fields">
                  <label>
                    <span>メインカラー</span>
                    <span class="theme-color-control">
                      <input type="color" bind:value={themeSettingsDraft.mainColor} aria-label="メインカラーを選択" />
                      <code>{themeSettingsDraft.mainColor.toUpperCase()}</code>
                    </span>
                    <small>現在のライム色に相当する選択・強調色</small>
                  </label>
                  <label>
                    <span>サブカラー</span>
                    <span class="theme-color-control">
                      <input type="color" bind:value={themeSettingsDraft.subColor} aria-label="サブカラーを選択" />
                      <code>{themeSettingsDraft.subColor.toUpperCase()}</code>
                    </span>
                    <small>現在のオレンジ色に相当する補助・操作色</small>
                  </label>
                </div>
              </div>

              <div class="settings-actions theme-settings-actions">
                <button type="button" class="theme-save-button" onclick={requestThemeSettingsSave}>保存</button>
                <button type="button" class="quiet-button" onclick={resetThemeSettingsDraft}>リセット</button>
                <button type="button" class="quiet-button" onclick={applyDefaultThemeSettingsDraft}>初期値</button>
              </div>
            </section>

            <div class="theme-preview-grid">
              <section class="theme-preview-panel">
                <header>
                  <div><strong>Current</strong><span>現在アプリに適用中</span></div>
                  <small>{appliedThemeSettings.theme === 'light' ? 'ライト' : 'ダーク'}</small>
                </header>
                <div
                  class="theme-preview"
                  class:theme-preview-light={appliedThemeSettings.theme === 'light'}
                  style={getThemeCssVariables(appliedThemeSettings)}
                >
                  <aside><span class="theme-preview-logo"></span><b>Gallery</b><span>Explorer</span><span>Creators</span></aside>
                  <div class="theme-preview-workspace">
                    <div class="theme-preview-toolbar"><span></span><i></i><i class="sub"></i></div>
                    <div class="theme-preview-content">
                      <article><span></span><strong>Selected card</strong><small>Accent preview</small></article>
                      <article><span></span><strong>Gallery item</strong><small>Modern surface</small></article>
                    </div>
                  </div>
                </div>
              </section>

              <section class="theme-preview-panel expected">
                <header>
                  <div><strong>Expected</strong><span>保存後の表示イメージ</span></div>
                  <small>{themeSettingsDraft.theme === 'light' ? 'ライト' : 'ダーク'}</small>
                </header>
                <div
                  class="theme-preview"
                  class:theme-preview-light={themeSettingsDraft.theme === 'light'}
                  style={getThemeCssVariables(themeSettingsDraft)}
                >
                  <aside><span class="theme-preview-logo"></span><b>Gallery</b><span>Explorer</span><span>Creators</span></aside>
                  <div class="theme-preview-workspace">
                    <div class="theme-preview-toolbar"><span></span><i></i><i class="sub"></i></div>
                    <div class="theme-preview-content">
                      <article><span></span><strong>Selected card</strong><small>Accent preview</small></article>
                      <article><span></span><strong>Gallery item</strong><small>Modern surface</small></article>
                    </div>
                  </div>
                </div>
              </section>
            </div>
          </div>
        {:else if settingsSection === 'language'}
          <div class="language-settings-page">
            <section class="settings-panel language-settings-card">
              <header class="language-settings-heading">
                <span class="language-settings-icon"><Languages size={20} /></span>
                <div>
                  <h2>言語設定</h2>
                  <p>アプリのシステムUIで使用する言語を設定します</p>
                </div>
              </header>

              <label class="language-settings-field">
                <span>表示言語</span>
                <select data-i18n-skip value={appLanguage} onchange={changeAppLanguage}>
                  <option value="ja">日本語</option>
                  <option value="en">English (Experimental)</option>
                  <option value="zh-CN">简体中文（实验性）</option>
                  <option value="zh-TW">繁體中文（實驗性）</option>
                </select>
              </label>

              <div class="language-settings-note">
                <Languages size={18} />
                <p>言語を切り替えると、システムUIへすぐに反映されます。作品名・Creator・Title・Character・Tagなどの登録データは翻訳されません。</p>
              </div>
            </section>
          </div>
        {:else if settingsSection === 'calendar'}
          <div class="calendar-settings-page">
            <section class="settings-panel calendar-settings-card">
              <header class="language-settings-heading">
                <span class="language-settings-icon"><CalendarCheck size={20} /></span>
                <div>
                  <h2>Calendar</h2>
                  <p>サブスク更新予定を表示するカレンダーの基本動作を設定します</p>
                </div>
              </header>

              <label class="language-settings-field">
                <span>週の開始曜日</span>
                <select bind:value={calendarWeekStartDraft}>
                  <option value={0}>日曜日</option>
                  <option value={1}>月曜日</option>
                  <option value={2}>火曜日</option>
                  <option value={3}>水曜日</option>
                  <option value={4}>木曜日</option>
                  <option value={5}>金曜日</option>
                  <option value={6}>土曜日</option>
                </select>
              </label>

              <div class="calendar-settings-note">
                <CalendarCheck size={18} />
                <p>週の開始曜日はMonthビューと2 Weeksビューの両方に反映されます。</p>
              </div>

              <div class="settings-actions">
                <button type="button" class="theme-save-button" onclick={saveCalendarSettings}>保存</button>
              </div>
            </section>

            <section class="settings-panel calendar-settings-card google-calendar-settings-card">
              <header class="google-calendar-settings-heading">
                <span class="language-settings-icon"><CloudUpload size={20} /></span>
                <div>
                  <h2>Google Calendar同期</h2>
                  <p>GalleryBrowserのサブスク更新予定をGoogle Calendarへ一方向で自動同期します</p>
                </div>
                <span class:connected={googleCalendarSyncFeatureEnabled && googleCalendarHasRefreshToken} class="google-calendar-connection-badge">
                  {googleCalendarSyncFeatureEnabled ? (googleCalendarHasRefreshToken ? '連携済み' : '未連携') : '無効'}
                </span>
              </header>

              <div class="google-calendar-feature-toggle-row">
                <div>
                  <strong>自動同期</strong>
                  <span>起動後・Creator Trackingの保存後・15分ごとの確認時に差分を同期します</span>
                </div>
                <button
                  type="button"
                  class:active={googleCalendarAutoSyncEnabled}
                  class="google-calendar-enable-toggle"
                  aria-pressed={googleCalendarAutoSyncEnabled}
                  disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled}
                  onclick={() => googleCalendarAutoSyncEnabled = !googleCalendarAutoSyncEnabled}>
                  <span></span>{googleCalendarAutoSyncEnabled ? 'ON' : 'OFF'}
                </button>
              </div>

              <div class="google-calendar-settings-grid">
                <label>
                  <span>OAuth Client ID</span>
                  <input bind:value={googleCalendarClientId} placeholder="Google CloudのデスクトップアプリClient ID" disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled} />
                </label>
                <label>
                  <span>OAuth Client Secret</span>
                  <input type="password" bind:value={googleCalendarClientSecretDraft} placeholder={googleCalendarHasClientSecret ? '登録済み（空欄なら維持）' : 'デスクトップアプリのClient Secret'} autocomplete="new-password" disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled} />
                </label>
                <label>
                  <span>同期先Calendar ID</span>
                  <input bind:value={googleCalendarId} placeholder="primary またはCalendar ID" disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled} />
                </label>
              </div>

              {#if googleCalendarRedirectUri}
                <div class="google-calendar-redirect-uri">
                  <span>OAuth Redirect URI（デスクトップアプリ用）</span>
                  <code>{googleCalendarRedirectUri}</code>
                </div>
              {/if}

              <div class="google-calendar-settings-note">
                {#if !googleCalendarSyncFeatureEnabled}
                  <p><strong>このビルドではGoogle Calendar同期機能は無効です。</strong></p>
                {/if}
                <strong>Google Cloudでの準備</strong>
                <ol>
                  <li>Google Calendar APIを有効化します。</li>
                  <li>OAuth同意画面を設定し、OAuthクライアントを「デスクトップアプリ」で作成します。</li>
                  <li>Client IDとClient Secretを入力して保存後、OAuth連携を実行します。</li>
                </ol>
                <p>Google側の一般予定は取り込みません。GalleryBrowserの識別情報が付いたサブスク予定だけを更新・削除します。</p>
              </div>

              {#if googleCalendarLastSyncedAt || googleCalendarLastSyncError || googleCalendarStatus}
                <div class:error={Boolean(googleCalendarLastSyncError)} class="google-calendar-sync-summary">
                  {#if googleCalendarStatus}<strong>{googleCalendarStatus}</strong>{/if}
                  {#if googleCalendarLastSyncedAt}<span>最終同期: {formatModifiedAt(googleCalendarLastSyncedAt)}</span>{/if}
                  {#if googleCalendarLastSyncError}<span>直近のエラー: {googleCalendarLastSyncError}</span>{/if}
                </div>
              {/if}

              <div class="google-calendar-actions">
                <button type="button" class="primary-button" onclick={saveCalendarSettings} disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled}>設定を保存</button>
                <button type="button" class="primary-button" onclick={connectGoogleCalendar} disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled || !googleCalendarClientId.trim()}>OAuth連携</button>
                <button type="button" class="quiet-button" onclick={testGoogleCalendarConnection} disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled || !googleCalendarHasRefreshToken}>接続確認</button>
                <button type="button" class="primary-button" onclick={syncGoogleCalendar} disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled || !googleCalendarHasRefreshToken}>今すぐ同期</button>
                <button type="button" class="danger-button" onclick={disconnectGoogleCalendar} disabled={googleCalendarBusy || !googleCalendarSyncFeatureEnabled || !googleCalendarHasRefreshToken}>連携解除</button>
              </div>
            </section>
          </div>
        {:else if settingsSection === 'creatorTracking'}
          <div class="creator-tracking-settings-page">
            <div class="creator-tracking-settings-grid">
              <section class="settings-panel creator-tracking-settings-card creator-tracking-settings-evaluation">
                <header>
                  <span class="creator-tracking-settings-icon"><Star size={18} /></span>
                  <div>
                    <h2>作品の傾向</h2>
                    <p>レーダーチャートの指標名、重み、総合評価の計算方法</p>
                  </div>
                </header>

                <div class="creator-tracking-settings-metric-head" aria-hidden="true">
                  <span>指標</span><span>表示名</span><span>重み</span>
                </div>
                <div class="creator-tracking-settings-metrics">
                  {#each creatorTrackingSettingsDraft.metrics as metric, index (metric.key)}
                    <label class="creator-tracking-settings-metric-row">
                      <span>{index + 1}</span>
                      <input bind:value={metric.label} aria-label={`${index + 1}番目の指標名`} onchange={saveCreatorTrackingSettings} />
                      <span class="creator-tracking-settings-number-with-unit">
                        <input type="number" min="0" max="100" step="1" bind:value={metric.weightPercent} aria-label={`${metric.label}の重み`} onchange={saveCreatorTrackingSettings} />
                        <span>%</span>
                      </span>
                    </label>
                  {/each}
                </div>
                <div class:creator-tracking-settings-weight-invalid={creatorTrackingSettingsWeightTotal !== 100} class="creator-tracking-settings-weight-total">
                  <span>重みの合計</span><strong>{creatorTrackingSettingsWeightTotal}%</strong>
                </div>

                <div class="creator-tracking-settings-inline-fields">
                  <label>
                    <span>評価倍率</span>
                    <input type="number" min="0.1" max="5" step="0.05" bind:value={creatorTrackingSettingsDraft.scoreMultiplier} />
                    <small>4段階評価を5点満点へ換算</small>
                  </label>
                  <label>
                    <span>表示する小数桁</span>
                    <select bind:value={creatorTrackingSettingsDraft.scoreDecimalPlaces}>
                      <option value={0}>整数</option>
                      <option value={1}>小数第1位</option>
                      <option value={2}>小数第2位</option>
                    </select>
                    <small>指定桁より下は切り捨て</small>
                  </label>
                </div>
              </section>

              <section class="settings-panel creator-tracking-settings-card creator-tracking-settings-activity-place-card">
                <header class="creator-tracking-settings-card-actions">
                  <div class="creator-tracking-settings-panel-title">
                    <span class="creator-tracking-settings-icon"><ExternalLink size={18} /></span>
                    <div>
                      <h2>活動場所の候補</h2>
                      <p>名称候補、URL／場所欄の背景文字、Creator Trackingで使うサイトアイコン</p>
                    </div>
                  </div>
                  <button type="button" class="quiet-button" onclick={addCreatorTrackingActivityPlaceSetting}><Plus size={15} /> 候補を追加</button>
                </header>
                <div class="creator-tracking-settings-place-head" aria-hidden="true"><span>名称</span><span>URL／場所の背景文字</span><span>更新</span><span>サイトアイコン</span><span></span></div>
                <div class="creator-tracking-settings-places">
                  {#each creatorTrackingSettingsDraft.activityPlaces as place, index}
                    <div class="creator-tracking-settings-place-row">
                      <input value={place.label} oninput={(event) => updateCreatorTrackingActivityPlaceSetting(index, { label: event.currentTarget.value })} onchange={saveCreatorTrackingSettings} aria-label={`${index + 1}番目の活動場所名`} placeholder="サービス名" />
                      <input value={place.placeholder} oninput={(event) => updateCreatorTrackingActivityPlaceSetting(index, { placeholder: event.currentTarget.value })} onchange={saveCreatorTrackingSettings} aria-label={`${place.label || index + 1}のURL背景文字`} placeholder="https://... または場所名" />
                      <button class="creator-tracking-settings-icon-refresh" class:loading={isCreatorTrackingActivityPlaceIconLoading(index)} type="button" title="サイトアイコンを更新" aria-label={`${place.label || index + 1}のサイトアイコンを更新`} disabled={isCreatorTrackingActivityPlaceIconLoading(index)} onclick={() => fetchCreatorTrackingActivityPlaceIcon(index)}><RefreshCw size={15} /></button>
                      <span class="creator-tracking-settings-icon-preview" title={place.iconDataUri ? `${place.label}のサイトアイコン` : 'アイコン未取得'}>{#if place.iconDataUri}<img src={place.iconDataUri} alt="" />{:else}<ExternalLink size={16} />{/if}</span>
                      <button class="creator-tracking-settings-place-delete" type="button" title="候補を削除" aria-label={`${place.label || index + 1}を削除`} onclick={() => removeCreatorTrackingActivityPlaceSetting(index)}><Trash2 size={15} /></button>
                    </div>
                  {/each}
                </div>
              </section>

              <section class="settings-panel creator-tracking-settings-card creator-tracking-settings-option-card">
                <header>
                  <span class="creator-tracking-settings-icon"><ListFilter size={18} /></span>
                  <div>
                    <h2>ドロップダウンの候補</h2>
                    <p>作者基本情報で使用するフォロー方針の候補を編集します</p>
                  </div>
                </header>
                <div class="creator-tracking-settings-option-columns">
                  <section>
                    <div class="creator-tracking-settings-option-heading">
                      <div><h3>フォロー方針</h3><p>作者基本情報とフォローの概要で使用</p></div>
                      <button type="button" class="quiet-button" onclick={() => addCreatorTrackingStringOption('followPolicyOptions')}><Plus size={14} /> 候補を追加</button>
                    </div>
                    <div class="creator-tracking-settings-option-list">
                      {#each creatorTrackingSettingsDraft.followPolicyOptions as option, index}
                        <div>
                          <span>{index + 1}</span>
                          <input value={option} placeholder="フォロー方針" aria-label={`フォロー方針候補${index + 1}`} oninput={(event) => updateCreatorTrackingStringOption('followPolicyOptions', index, event.currentTarget.value)} onchange={saveCreatorTrackingSettings} />
                          <button type="button" title="候補を削除" aria-label={`フォロー方針候補${index + 1}を削除`} onclick={() => removeCreatorTrackingStringOption('followPolicyOptions', index)}><Trash2 size={14} /></button>
                        </div>
                      {/each}
                    </div>
                  </section>
                </div>
              </section>

              <section class="settings-panel creator-tracking-settings-card">
                <header>
                  <span class="creator-tracking-settings-icon"><ChartNoAxesCombined size={18} /></span>
                  <div>
                    <h2>集計・通貨</h2>
                    <p>SUMMARYの課金集計と為替換算に使用する既定値</p>
                  </div>
                </header>
                <div class="creator-tracking-settings-form-grid">
                  <label>
                    <span>構成比ラベルの最大件数</span>
                    <span class="creator-tracking-settings-number-with-unit">
                      <input type="number" min="1" max="8" step="1" bind:value={creatorTrackingSettingsDraft.compositionLabelLimit} onchange={saveCreatorTrackingCompositionLabelLimit} />
                      <span>件</span>
                    </span>
                    <small>Title／Tagとも上位項目を表示し、残りを「その他」に集約</small>
                  </label>
                  <label>
                    <span>換算通貨</span>
                    <select bind:value={creatorTrackingSettingsDraft.displayCurrency}>
                      <option value="JPY">JPY — 日本円</option>
                      <option value="USD">USD — 米ドル</option>
                      <option value="EUR">EUR — ユーロ</option>
                      <option value="CNY">CNY — 人民元</option>
                      <option value="KRW">KRW — 韓国ウォン</option>
                    </select>
                  </label>
                  <label>
                    <span>直近課金額の集計期間</span>
                    <span class="creator-tracking-settings-number-with-unit">
                      <input type="number" min="1" max="24" step="1" bind:value={creatorTrackingSettingsDraft.recentSpendMonths} />
                      <span>か月</span>
                    </span>
                  </label>
                  <label>
                    <span>為替レート取得元</span>
                    <select bind:value={creatorTrackingSettingsDraft.exchangeRateProvider}>
                      <option value="frankfurter">Frankfurter / ECB</option>
                    </select>
                  </label>
                  <div class="creator-tracking-settings-static-field">
                    <span>為替レートの保存</span>
                    <span class="creator-tracking-settings-static-value">SQLiteDBへ決済年月単位でキャッシュ</span>
                  </div>
                </div>
              </section>

              <section class="settings-panel creator-tracking-settings-card">
                <header>
                  <span class="creator-tracking-settings-icon"><Trophy size={18} /></span>
                  <div>
                    <h2>ランキング・グラフ表示</h2>
                    <p>SUMMARYの順位装飾と推移グラフの初期表示</p>
                  </div>
                </header>
                <div class="creator-tracking-settings-rank-grid">
                  <label class="gold">
                    <Trophy size={16} />
                    <span>ゴールド</span>
                    <span class="creator-tracking-settings-number-with-unit"><input type="number" min="1" max="100" step="1" bind:value={creatorTrackingSettingsDraft.goldRankPercent} /><span>%以内</span></span>
                  </label>
                  <label class="silver">
                    <Trophy size={16} />
                    <span>シルバー</span>
                    <span class="creator-tracking-settings-number-with-unit"><input type="number" min="1" max="100" step="1" bind:value={creatorTrackingSettingsDraft.silverRankPercent} /><span>%以内</span></span>
                  </label>
                  <label class="bronze">
                    <Trophy size={16} />
                    <span>ブロンズ</span>
                    <span class="creator-tracking-settings-number-with-unit"><input type="number" min="1" max="100" step="1" bind:value={creatorTrackingSettingsDraft.bronzeRankPercent} /><span>%以内</span></span>
                  </label>
                </div>
                <div class="creator-tracking-settings-inline-fields creator-tracking-settings-display-fields">
                  <label>
                    <span>時間スケール</span>
                    <select bind:value={creatorTrackingSettingsDraft.archiveScale}>
                      <option value="week">週単位</option>
                      <option value="month">月単位</option>
                      <option value="year">年単位</option>
                    </select>
                  </label>
                </div>
              </section>
            </div>
          </div>
        {:else if settingsSection === 'winrar'}
          <div class="section-title">
            <div>
              <h1>WinRAR設定</h1>
              <p>解凍・圧縮・WinRARで開く操作に使用する専用設定です</p>
            </div>
          </div>

          <section class="settings-panel winrar-settings-panel">
            <div class="field-grid">
              <label class="wide">
                <span>WinRARの実行ファイル</span>
                <span class="program-executable-input">
                  <input bind:value={winRarSettings.executablePath} placeholder="空欄の場合は標準のインストール場所を検出" />
                  <button type="button" title="WinRAR.exe を選択" onclick={() => postHostMessage({ type: 'settings.winrar.pickExecutable' })}>
                    <FolderOpen size={17} />
                  </button>
                </span>
              </label>
              <label class="wide">
                <span>対応させるファイルの拡張子</span>
                <input bind:value={winRarSettings.supportedExtensions} placeholder="zip, rar, 7z" />
              </label>
              <label class="program-multiple-toggle wide">
                <input type="checkbox" bind:checked={winRarSettings.showOpenInContextMenu} />
                <span>右クリックメニューに「WinRARで開く」を表示する</span>
              </label>
            </div>
            <div class="settings-actions">
              <button onclick={saveWinRarSettings}>保存</button>
            </div>
          </section>
        {:else if settingsSection === 'ffmpeg'}
          <div class="section-title">
            <div>
              <h1>FFmpeg設定</h1>
              <p>動画ファイルからサムネイルを生成するための専用設定です。ffprobe.exe が同じフォルダにある場合は、動画中ほどのフレームを優先します</p>
            </div>
          </div>

          <section class="settings-panel winrar-settings-panel">
            <div class="field-grid">
              <label class="wide">
                <span>FFmpegの実行ファイル</span>
                <span class="program-executable-input">
                  <input bind:value={ffmpegSettings.executablePath} placeholder="ffmpeg.exe のフルパス" />
                  <button type="button" title="ffmpeg.exe を選択" onclick={() => postHostMessage({ type: 'settings.ffmpeg.pickExecutable' })}>
                    <FolderOpen size={17} />
                  </button>
                </span>
              </label>
              <label class="wide">
                <span>対応させる動画ファイルの拡張子</span>
                <input bind:value={ffmpegSettings.supportedExtensions} placeholder="mp4, mkv, avi" />
              </label>
            </div>
            <p class:ffmpeg-status-ready={ffmpegAvailable} class="ffmpeg-status">
              {ffmpegAvailable
                ? 'ffmpeg.exe を利用できます。'
                : 'ffmpeg.exe が未設定、または指定場所に見つかりません。動画サムネイルは生成されません。'}
            </p>
            <div class="settings-actions">
              <button onclick={saveFfmpegSettings}>保存</button>
            </div>
          </section>
        {:else if settingsSection === 'searchEngine'}
          <section class="settings-panel search-engine-settings-panel">
            <div class="field-grid">
              <label class="wide">
                <span>標準名検索方法</span>
                <select bind:value={searchEngineSettings.provider}>
                  <option value="google">Google（ブラウザ検索）</option>
                  <option value="brave">Brave Search API（候補取得）</option>
                  <option value="gemini">Gemini API（AI候補生成）</option>
                </select>
              </label>
              <label class="wide">
                <span>Google検索URL</span>
                <input bind:value={searchEngineSettings.googleSearchUrlTemplate} placeholder="https://www.google.com/search?q={query}" />
                <small>{'{query}'} の位置に検索語を入れます</small>
              </label>
              <label class="wide">
                <span>Brave Search APIキー</span>
                <input type="password" bind:value={searchEngineSettings.braveApiKey} autocomplete="off" placeholder="任意。API候補取得の接続用に保存します" />
              </label>
              <label class="wide">
                <span>Gemini APIキー</span>
                <input type="password" bind:value={searchEngineSettings.geminiApiKey} autocomplete="off" placeholder="任意。AI候補取得の接続用に保存します" />
              </label>
            </div>
            <p class="settings-note">Google はブラウザ検索、Brave Search API と Gemini API はフィルタエディタ内へ候補を表示します</p>
            <div class="settings-actions">
              <button onclick={saveSearchEngineSettings}>保存</button>
            </div>
          </section>
        {:else if settingsSection === 'galleryTargets'}
          <div class="gallery-targets-settings-page">
            <section class="settings-panel gallery-section-creation-panel">
              <header>
                <div>
                  <h2>区分の作成</h2>
                  <p>Gallery、Creators、User Metricsで使用する区分を作成します</p>
                </div>
                <button type="button" class="gallery-section-add-button" onclick={addGallerySectionDraft}>
                  <Plus size={16} /> 区分の追加
                </button>
              </header>

              <div class="gallery-section-editor-head" aria-hidden="true">
                <span>区分名</span><span></span>
              </div>
              <div class="gallery-section-editor-list">
                {#each gallerySections as section (section.id)}
                  <div class="gallery-section-editor-row">
                    <input
                      value={section.label}
                      maxlength="80"
                      aria-label="区分名"
                      oninput={(event) => updateGallerySectionLabel(section.id, (event.currentTarget as HTMLInputElement).value)}
                      onchange={(event) => saveGallerySectionLabel(section.id, (event.currentTarget as HTMLInputElement).value)}
                      onkeydown={(event) => {
                        if (event.key === 'Enter') (event.currentTarget as HTMLInputElement).blur();
                      }}
                    />
                    <button
                      type="button"
                      class="icon-danger"
                      disabled={gallerySections.length <= 1}
                      title={gallerySections.length <= 1 ? '区分は1件以上必要です' : '区分を削除'}
                      aria-label={`${translateSystemText('区分を削除', appLanguage)}: ${section.label}`}
                      onclick={() => deleteGallerySection(section.id)}
                    ><Trash2 size={16} /></button>
                  </div>
                {/each}
                {#each gallerySectionDrafts as draft (draft.key)}
                  <div class="gallery-section-editor-row gallery-section-editor-draft">
                    <input
                      value={draft.label}
                      maxlength="80"
                      placeholder="区分名を入力"
                      aria-label="新しい区分名"
                      oninput={(event) => updateGallerySectionDraft(draft.key, (event.currentTarget as HTMLInputElement).value)}
                      onchange={() => createGallerySectionFromDraft(draft.key)}
                      onkeydown={(event) => {
                        if (event.key === 'Enter') createGallerySectionFromDraft(draft.key);
                      }}
                    />
                    <button type="button" class="icon-danger" title="入力行を削除" aria-label="入力行を削除" onclick={() => removeGallerySectionDraft(draft.key)}><Trash2 size={16} /></button>
                  </div>
                {/each}
              </div>
            </section>

            <section class="settings-panel gallery-targets-settings-panel">
            <div class="gallery-target-settings-row gallery-target-category-row">
              <label>
                <span>区分</span>
                <select data-i18n-skip value={galleryTargetCategory} onchange={(event) => selectGalleryTargetCategory((event.currentTarget as HTMLSelectElement).value)}>
                  {#each gallerySections as section}
                    <option value={section.id}>{section.label}</option>
                  {/each}
                </select>
              </label>
            </div>
            <div class="gallery-filter-settings">
              <span>使用フィルタ</span>
              <div>
                <label><input type="checkbox" checked disabled />Rating</label>
                <label><input type="checkbox" checked disabled />Creator</label>
                <label><input type="checkbox" checked={galleryTargetEnabledFilters.includes('title')} onchange={(event) => setGalleryTargetTitleFilterEnabled((event.currentTarget as HTMLInputElement).checked)} />Title</label>
                <label><input type="checkbox" checked disabled />Character</label>
                <label><input type="checkbox" checked disabled />Tag</label>
                <label><input type="checkbox" checked={galleryTargetEnabledFilters.includes('core_title')} onchange={(event) => setGalleryTargetCoreFilterEnabled('core_title', (event.currentTarget as HTMLInputElement).checked)} />Core title</label>
                <label><input type="checkbox" checked={galleryTargetEnabledFilters.includes('core_tags')} onchange={(event) => setGalleryTargetCoreFilterEnabled('core_tags', (event.currentTarget as HTMLInputElement).checked)} />Core tags</label>
              </div>
            </div>
            <div class="gallery-filter-label-settings">
              <span>フィルタ行の表示ラベル</span>
              <p>Galleryの属性行とCreatorsのCore行に表示する名称です。フィルタの内容には影響しません。</p>
              <div>
                <label><span>Creator</span><input maxlength="40" bind:value={galleryTargetCreatorLabel} placeholder="Creator" /></label>
                <label><span>Title</span><input maxlength="40" bind:value={galleryTargetTitleLabel} placeholder="Title" /></label>
                <label><span>Character</span><input maxlength="40" bind:value={galleryTargetCharacterLabel} placeholder="Character" /></label>
                <label><span>Tag</span><input maxlength="40" bind:value={galleryTargetTagLabel} placeholder="Tag" /></label>
                <label><span>Core title</span><input maxlength="40" bind:value={galleryTargetCoreTitleLabel} placeholder="Core title" /></label>
                <label><span>Core tags</span><input maxlength="40" bind:value={galleryTargetCoreTagsLabel} placeholder="Core tags" /></label>
              </div>
            </div>
            <label>
              <span>対象ファイルの拡張子</span>
              <input bind:value={galleryTargetExtensions} placeholder="zip, mp4, mkv（空欄の場合はすべて）" />
            </label>
            <label>
              <span>対象ディレクトリ</span>
              <div class="thumbnail-cache-target-add">
                <input bind:value={galleryTargetDraft} placeholder="D:\Gallery" onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    addGalleryScanTarget();
                  }
                }} />
                <button onclick={addGalleryScanTarget}>追加</button>
              </div>
            </label>

            {#if selectedGalleryScanTargets.length === 0}
              <p class="thumbnail-cache-note">対象未登録の区分は、既存SQLiteの同一区分すべてを表示します</p>
            {:else}
              <div class="thumbnail-cache-target-list">
                {#each selectedGalleryScanTargets as target}
                  <div class="thumbnail-cache-target-row">
                    <code title={target.path}>{target.path}</code>
                    <button class="icon-danger" title="対象から外す" onclick={() => removeGalleryScanTarget(target.path)}><Trash2 size={16} /></button>
                  </div>
                {/each}
              </div>
            {/if}
            <div class="gallery-thumbnail-settings">
              <strong>サムネイル調整</strong>
              <p>基準の中央位置からのトリミング位置と、カードの縦横比を設定します</p>
              <label>
                <span>カードの縦横比</span>
                <select bind:value={galleryTargetCardAspect}>
                  <option value="portrait">縦長</option>
                  <option value="landscape">横長</option>
                </select>
              </label>
            </div>
            <div class="thumbnail-adjustment-fields">
              <label>
                <span>縮尺（%）</span>
                <input type="number" min="100" max="250" step="5" bind:value={thumbnailAdjustmentScale} />
                <small>100% が基準、値を大きくすると拡大</small>
              </label>
              <label>
                <span>横方向（%）</span>
                <input type="number" min="-45" max="45" step="1" bind:value={thumbnailAdjustmentHorizontalOffset} />
                <small>負の値で左、正の値で右へ移動</small>
              </label>
              <label>
                <span>縦方向（%）</span>
                <input type="number" min="-45" max="45" step="1" bind:value={thumbnailAdjustmentVerticalOffset} />
                <small>負の値で上、正の値で下へ移動</small>
              </label>
            </div>
            <p class="thumbnail-cache-note">トリミング領域の大きさに対する移動量です。初期値は縦方向 -10% です。保存後、表示中のカードは新しい位置でサムネイルを作り直します</p>
            <div class="gallery-card-settings">
              <strong>カード調整</strong>
              <label>
                <span>ファイル名の表示行数</span>
                <select bind:value={galleryTargetFileNameLines}>
                  <option value={1}>1行</option>
                  <option value={2}>2行</option>
                  <option value={3}>3行</option>
                  <option value={4}>4行</option>
                </select>
              </label>
            </div>
            <div class="settings-actions">
              <button onclick={saveGallerySectionSettings}>保存</button>
            </div>
            </section>
          </div>
        {:else if settingsSection === 'sqliteDatabase'}
          <div class="section-title">
            <div>
              <h1>データベース</h1>
              <p>Gallery の本体DBとキャッシュDBの保存先、更新処理、クラウドバックアップを管理します</p>
            </div>
          </div>

          <div class="thumbnail-cache-settings-layout">
            {#if sqliteDatabasePath}
              <section class="settings-panel thumbnail-cache-current-root-panel">
                <h2>現在の本体DBの保存先</h2>
                <code title={sqliteDatabasePath}>{sqliteDatabasePath}</code>
              </section>
            {/if}

            <section class="settings-panel thumbnail-cache-targets-panel">
              <h2>本体DBの保存先の変更</h2>
              <div class="thumbnail-cache-target-add">
                <input bind:value={sqliteDatabasePathDraft} placeholder="SQLiteDBの保存先ディレクトリを指定" disabled={sqliteDatabaseBusy} />
                <button onclick={moveSqliteDatabase} disabled={sqliteDatabaseBusy}>適用</button>
              </div>
              <p class="thumbnail-cache-note">パスを貼り付けて指定してください。適用時に、現在の SQLiteDB を新しい保存先へ移動します</p>
            </section>

            <section class="settings-panel sqlite-merge-panel">
              <div>
                <h2>外部DBの結合</h2>
                <p>後から見つかったGallery本体DBを取り込みます。正に選んだDBの値を競合時に優先し、両方を事前にバックアップします</p>
              </div>
              <div class="thumbnail-cache-target-add">
                <input value={sqliteMergeDatabasePath} placeholder="結合するSQLiteDBを選択" readonly disabled={sqliteDatabaseBusy} />
                <button class="secondary-button" onclick={pickSqliteDatabaseForMerge} disabled={sqliteDatabaseBusy}>参照</button>
              </div>
              <fieldset class="sqlite-merge-canonical-options" disabled={sqliteDatabaseBusy}>
                <legend>競合時に正とするDB</legend>
                <label>
                  <input type="radio" bind:group={sqliteMergeCanonical} value="current" />
                  <span>現在使用中のDB（現在の保存先を維持）</span>
                </label>
                <label>
                  <input type="radio" bind:group={sqliteMergeCanonical} value="selected" />
                  <span>選択したDB（結合後はこちらへ切替）</span>
                </label>
              </fieldset>
              <div class="settings-actions">
                <button class="primary-button" onclick={mergeSqliteDatabase} disabled={sqliteDatabaseBusy || !sqliteMergeDatabasePath.trim()}>
                  バックアップして結合
                </button>
              </div>
              <p class="thumbnail-cache-note">作品、Tag、フィルター定義、Creator Tracking、ブックマーク、付箋を外部結合します。結合元DBは削除しません</p>
            </section>

            {#if sqliteCacheDatabasePath}
              <section class="settings-panel thumbnail-cache-current-root-panel">
                <div class="database-panel-heading">
                  <h2>現在のキャッシュDBの保存先</h2>
                  {#if sqliteCacheDatabaseRestartRequired}<span class="database-restart-badge">再起動待ち</span>{/if}
                </div>
                <code title={sqliteCacheDatabasePath}>{sqliteCacheDatabasePath}</code>
                {#if sqliteCacheDatabaseRestartRequired && sqliteConfiguredCacheDatabasePath}
                  <p class="thumbnail-cache-note">次回起動時: <code title={sqliteConfiguredCacheDatabasePath}>{sqliteConfiguredCacheDatabasePath}</code></p>
                {/if}
              </section>
            {/if}

            <section class="settings-panel thumbnail-cache-targets-panel">
              <h2>キャッシュDBの保存先の変更</h2>
              <div class="thumbnail-cache-target-add">
                <input bind:value={sqliteCacheDatabasePathDraft} placeholder="キャッシュDBの保存先ディレクトリを指定" disabled={sqliteDatabaseBusy} />
                <button onclick={moveSqliteCacheDatabase} disabled={sqliteDatabaseBusy}>適用</button>
              </div>
              <p class="thumbnail-cache-note">キャッシュDBを指定先へコピーし、次回起動時から切り替えます。本体DBの保存先には影響しません</p>
            </section>

            <section class="settings-panel pcloud-backup-panel">
              <div class="pcloud-backup-heading">
                <div>
                  <h2>pCloud バックアップ</h2>
                  <p>本体DBの整合性スナップショットをアイドル時に自動保存し、任意の世代から手動で復元できます</p>
                </div>
                <span class:pcloud-connected={pCloudHasAccessToken} class="pcloud-connection-badge">
                  {pCloudHasAccessToken ? '連携済み' : '未連携'}
                </span>
              </div>

              <div class="pcloud-settings-grid">
                <label>
                  <span>データ保存リージョン</span>
                  <select bind:value={pCloudApiHost} disabled={pCloudBusy}>
                    <option value="eapi.pcloud.com">Europe（eapi.pcloud.com）</option>
                    <option value="api.pcloud.com">United States（api.pcloud.com）</option>
                  </select>
                </label>
                <label>
                  <span>pCloud内の保存先</span>
                  <input bind:value={pCloudTargetFolder} placeholder="pCloud内の保存先を入力" disabled={pCloudBusy} />
                </label>
                <label>
                  <span>OAuth Client ID</span>
                  <input bind:value={pCloudClientId} placeholder="pCloud DevelopersのClient ID" disabled={pCloudBusy} />
                </label>
                <label>
                  <span>OAuthアクセストークン（手動登録・任意）</span>
                  <input type="password" bind:value={pCloudAccessTokenDraft} placeholder={pCloudHasAccessToken ? '登録済み（空欄なら維持）' : 'アクセストークンを貼り付け'} autocomplete="new-password" disabled={pCloudBusy} />
                </label>
              </div>

              <div class="pcloud-automation-settings">
                <div class="pcloud-feature-toggle-row">
                  <div>
                    <strong>pCloudバックアップ機能</strong>
                    <span>OFFの場合、自動・手動バックアップを実行しません</span>
                  </div>
                  <button
                    type="button"
                    class="pcloud-enable-toggle"
                    class:active={pCloudAutoBackupEnabled}
                    aria-pressed={pCloudAutoBackupEnabled}
                    disabled={pCloudBusy}
                    onclick={() => pCloudAutoBackupEnabled = !pCloudAutoBackupEnabled}>
                    <span></span>{pCloudAutoBackupEnabled ? 'ON' : 'OFF'}
                  </button>
                </div>
                <div class="pcloud-schedule-grid" class:disabled={!pCloudAutoBackupEnabled}>
                  <label>
                    <span>バックアップの確認頻度（分）</span>
                    <input type="number" min="1" max="1440" step="1" bind:value={pCloudCheckIntervalMinutes} disabled={pCloudBusy || !pCloudAutoBackupEnabled} />
                  </label>
                  <label>
                    <span>バックアップの頻度（日）</span>
                    <input type="number" min="1" max="365" step="1" bind:value={pCloudBackupIntervalDays} disabled={pCloudBusy || !pCloudAutoBackupEnabled} />
                  </label>
                  <label>
                    <span>最大スナップショット数</span>
                    <input type="number" min="1" max="999" step="1" bind:value={pCloudMaximumSnapshots} disabled={pCloudBusy || !pCloudAutoBackupEnabled} />
                  </label>
                  <label>
                    <span>アイドル判定時間（分）</span>
                    <input type="number" min="1" max="1440" step="1" bind:value={pCloudIdleThresholdMinutes} disabled={pCloudBusy || !pCloudAutoBackupEnabled} />
                  </label>
                </div>
              </div>

              {#if pCloudOAuthRedirectUri}
                <div class="pcloud-redirect-uri">
                  <span>pCloudアプリに登録するRedirect URI</span>
                  <code>{pCloudOAuthRedirectUri}</code>
                </div>
              {/if}

              <p class="thumbnail-cache-note">pCloud Developers の My Apps でアプリを作成し、上記Redirect URIを登録してください。OAuth連携ではアカウントのパスワードをGalleryBrowserへ渡しません</p>
              <p class="pcloud-automation-note">有効時は{pCloudCheckIntervalMinutes}分ごとに確認し、前回の成功から{pCloudBackupIntervalDays}日以上経過していて、アプリを{pCloudIdleThresholdMinutes}分以上操作していない場合に実行します。直近{pCloudMaximumSnapshots}件を保持します。</p>

              {#if pCloudAccountEmail || pCloudLastBackupAt}
                <div class="pcloud-backup-summary">
                  {#if pCloudAccountEmail}
                    <div><span>接続アカウント</span><strong>{pCloudAccountEmail}</strong></div>
                    <div><span>使用容量</span><strong>{formatSize(pCloudUsedQuotaBytes)} / {formatSize(pCloudQuotaBytes)}</strong></div>
                  {/if}
                  {#if pCloudLastBackupAt}
                    <div><span>最終バックアップ</span><strong>{formatModifiedAt(pCloudLastBackupAt)}</strong></div>
                    <div><span>ファイル</span><strong title={pCloudLastBackupFileName}>{pCloudLastBackupFileName}</strong></div>
                  {/if}
                </div>
              {/if}

              <div class="pcloud-backup-actions">
                <button class="primary-button" onclick={savePCloudSettings} disabled={pCloudBusy}>設定を保存</button>
                <button class="primary-button" onclick={connectPCloud} disabled={pCloudBusy}>OAuth連携</button>
                <button class="quiet-button" onclick={testPCloudConnection} disabled={pCloudBusy || (!pCloudHasAccessToken && !pCloudAccessTokenDraft.trim())}>接続確認</button>
                <button class="primary-button" onclick={backupToPCloud} disabled={pCloudBusy || !pCloudAutoBackupEnabled || (!pCloudHasAccessToken && !pCloudAccessTokenDraft.trim())}>
                  {pCloudBusy ? '処理中...' : '今すぐバックアップ'}
                </button>
                <button class="quiet-button" onclick={loadPCloudSnapshots} disabled={pCloudBusy || !pCloudHasAccessToken}>スナップショットを更新</button>
                {#if pCloudHasAccessToken}
                  <button class="danger-button" onclick={disconnectPCloud} disabled={pCloudBusy}>連携解除</button>
                {/if}
              </div>
              {#if pCloudSnapshotsLoaded}
                <div class="pcloud-snapshot-panel">
                  <div class="pcloud-snapshot-heading">
                    <strong>復元可能なスナップショット</strong>
                    <span>{pCloudSnapshots.length} / {pCloudMaximumSnapshots}件</span>
                  </div>
                  {#if pCloudSnapshots.length === 0}
                    <p class="pcloud-snapshot-empty">復元可能なスナップショットはありません。</p>
                  {:else}
                    <div class="pcloud-snapshot-list">
                      {#each pCloudSnapshots as snapshot (snapshot.fileId)}
                        <div class="pcloud-snapshot-row">
                          <div class="pcloud-snapshot-date">
                            <strong>{formatModifiedAt(snapshot.createdAt)}</strong>
                            <span title={snapshot.fileName}>{snapshot.fileName}</span>
                          </div>
                          <span class="pcloud-snapshot-size">{formatSize(snapshot.sizeBytes)}</span>
                          <button class="primary-button pcloud-restore-button" onclick={() => restorePCloudSnapshot(snapshot)} disabled={pCloudBusy}>
                            <Download size={14} />
                            復元
                          </button>
                        </div>
                      {/each}
                    </div>
                  {/if}
                </div>
              {/if}
              {#if pCloudStatus}<p class="pcloud-backup-status" aria-live="polite">{pCloudStatus}</p>{/if}
            </section>

            <section class="settings-panel database-schedule-panel">
              <div class="database-schedule-heading">
                <div>
                  <h2>フォルダ走査のスケジュール</h2>
                  <p>曜日・時刻・対象区分を指定して、アプリ起動中にSQLiteDBの更新を自動で開始します</p>
                </div>
                <button type="button" class="primary-button" onclick={addDatabaseScanSchedule} disabled={databaseScanScheduleSaving}>
                  <Plus size={15} />
                  行を追加
                </button>
              </div>

              {#if databaseScanSchedules.length === 0}
                <p class="database-schedule-empty">スケジュールは登録されていません。</p>
              {:else}
                <div class="database-schedule-list">
                  {#each databaseScanSchedules as schedule (schedule.id)}
                    <div class="database-schedule-row">
                      <div class="database-schedule-field database-schedule-weekday-field">
                        <span>曜日</span>
                        <div class="database-schedule-weekdays">
                          {#each databaseScheduleWeekdays as weekday}
                            <button
                              type="button"
                              class:active={schedule.weekdays.includes(weekday.value)}
                              aria-pressed={schedule.weekdays.includes(weekday.value)}
                              onclick={() => toggleDatabaseScanScheduleWeekday(schedule.id, weekday.value)}
                              disabled={databaseScanScheduleSaving}>
                              {weekday.label}
                            </button>
                          {/each}
                        </div>
                      </div>

                      <label class="database-schedule-field database-schedule-time-field">
                        <span>時刻</span>
                        <input
                          type="time"
                          value={schedule.time}
                          onchange={(event) => updateDatabaseScanScheduleTime(schedule.id, event.currentTarget.value)}
                          disabled={databaseScanScheduleSaving} />
                      </label>

                      <div class="database-schedule-field database-schedule-category-field">
                        <span>対象区分（複数可）</span>
                        <details class="database-schedule-category-picker">
                          <summary>{databaseScanScheduleCategorySummary(schedule)}</summary>
                          <div>
                            {#each gallerySections as section}
                              <label>
                                <input
                                  type="checkbox"
                                  checked={schedule.categories.includes(section.id)}
                                  onchange={() => toggleDatabaseScanScheduleCategory(schedule.id, section.id)}
                                  disabled={databaseScanScheduleSaving} />
                                <span data-i18n-skip>{section.label}</span>
                              </label>
                            {/each}
                          </div>
                        </details>
                      </div>

                      <div class="database-schedule-last-run">
                        <span>最終開始</span>
                        <strong>{schedule.lastStartedAt ? formatModifiedAt(schedule.lastStartedAt) : '未実行'}</strong>
                      </div>

                      <button
                        type="button"
                        class="icon-button database-schedule-delete"
                        aria-label="スケジュールを削除"
                        title="スケジュールを削除"
                        onclick={() => removeDatabaseScanSchedule(schedule.id)}
                        disabled={databaseScanScheduleSaving}>
                        <Trash2 size={16} />
                      </button>
                    </div>
                  {/each}
                </div>
              {/if}

              <div class="settings-actions">
                <button type="button" onclick={saveDatabaseScanSchedules} disabled={databaseScanScheduleSaving}>
                  {databaseScanScheduleSaving ? '保存中...' : '設定を保存'}
                </button>
              </div>
              <p class="thumbnail-cache-note">予定時刻にアプリが起動していなかった場合は、次回起動後、その曜日のうちに未実行であれば開始します。手動更新や別のスケジュールと重なった場合は、実行中の走査が完了してから再判定します。</p>
            </section>

            <section class="settings-panel thumbnail-cache-maintenance-panel">
              <div>
                <h2>SQLiteDBのメンテナンス</h2>
                <p>VACUUM と ANALYZE を実行し、空き領域の回収とクエリ統計の更新を行います。通常は定期実行不要です</p>
              </div>
              <button class="primary-button" onclick={maintainSqliteDatabase} disabled={sqliteDatabaseBusy}>メンテナンスを実行</button>
            </section>

            <section class="settings-panel thumbnail-cache-maintenance-panel sqlite-update-panel">
              <div>
                <h2>SQLiteDBの手動更新</h2>
                <p>選択したギャラリー対象を走査し、作品情報を最新化します。既存の評価値は保持します</p>
              </div>
              <div class="sqlite-update-category-list">
                {#each gallerySections as section}
                  <label>
                    <input type="checkbox" value={section.id} bind:group={sqliteDatabaseUpdateCategories} disabled={sqliteDatabaseBusy} />
                    <span data-i18n-skip>{section.label}</span>
                  </label>
                {/each}
              </div>
              <div class="settings-actions">
                <button onclick={updateSqliteDatabase} disabled={sqliteDatabaseBusy}>{sqliteDatabaseBusy ? '処理中...' : '更新を実行'}</button>
                {#if sqliteDatabaseUpdateInProgress}
                  <button class="secondary-button" onclick={cancelSqliteDatabaseUpdate} disabled={sqliteDatabaseCancelRequested}>
                    {sqliteDatabaseCancelRequested ? '中断中...' : '中断'}
                  </button>
                {/if}
              </div>
            </section>

            {#if sqliteDatabaseStatus}
              <p class="thumbnail-cache-status">{sqliteDatabaseStatus}</p>
            {/if}
            {#if sqliteDatabaseProgressLog.length > 0}
              <div class="sqlite-database-progress-log" role="log" aria-live="polite" aria-label="SQLiteDB更新ログ">
                {#each sqliteDatabaseProgressLog as line, index (`${index}:${line}`)}
                  <code>{line}</code>
                {/each}
              </div>
            {/if}
          </div>
        {:else if settingsSection === 'thumbnailCache'}
          <div class="section-title">
            <div>
              <h1>サムネイルキャッシュ</h1>
              <p>対象ディレクトリ配下のフォルダと対応ファイルのサムネイルを保存します。対象未登録時はすべての場所が対象です</p>
            </div>
          </div>

          <div class="thumbnail-cache-settings-layout">
            {#if thumbnailCacheRoot}
              <section class="settings-panel thumbnail-cache-current-root-panel">
                <h2>現在のキャッシュの保存先</h2>
                <code title={thumbnailCacheRoot}>{thumbnailCacheRoot}</code>
              </section>
            {/if}

            <section class="settings-panel thumbnail-cache-targets-panel">
              <h2>キャッシュの保存先の変更</h2>
              <div class="thumbnail-cache-target-add">
                <input bind:value={thumbnailCacheRootDraft} placeholder="キャッシュの保存先を指定" />
                <button onclick={saveThumbnailCacheRoot}>適用</button>
              </div>
              <p class="thumbnail-cache-note">パスを貼り付けて指定してください。適用時に、現在のキャッシュを新しい保存先へ移動します</p>
            </section>

            <section class="settings-panel thumbnail-cache-targets-panel">
              <h2>キャッシュ生成の対象ディレクトリ</h2>
              <div class="thumbnail-cache-target-add">
                <input bind:value={thumbnailCacheTargetDraft} placeholder="D:\Gallery" onkeydown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    addThumbnailCacheTarget();
                  }
                }} />
                <button onclick={addThumbnailCacheTarget}>追加</button>
              </div>
              {#if thumbnailCacheTargets.length === 0}
                <p class="thumbnail-cache-note">対象を登録すると、そのディレクトリ配下だけにキャッシュ生成を限定します</p>
              {:else}
                <div class="thumbnail-cache-target-list">
                  {#each thumbnailCacheTargets as target}
                    <div class="thumbnail-cache-target-row">
                      <code title={target}>{target}</code>
                      <button class="icon-danger" title="対象から外す" onclick={() => removeThumbnailCacheTarget(target)}><Trash2 size={16} /></button>
                    </div>
                  {/each}
                </div>
              {/if}
            </section>

            <section class="settings-panel thumbnail-cache-maintenance-panel">
              <div>
                <h2>キャッシュのメンテナンス</h2>
                <p>元ファイルが削除・更新されたキャッシュと、追跡されていない古いキャッシュを削除します</p>
              </div>
              <button class="primary-button" onclick={maintainThumbnailCache}>メンテナンスを実行</button>
            </section>

            <section class="settings-panel thumbnail-cache-maintenance-panel">
              <div>
                <h2>キャッシュの再構築</h2>
                <p>現在のキャッシュをすべて破棄し、登録済み対象ディレクトリを走査して作り直します</p>
              </div>
              <button class="danger-button" onclick={rebuildThumbnailCache}>再構築を実行</button>
            </section>

            {#if thumbnailCacheStatus}
              <p class="thumbnail-cache-status">{thumbnailCacheStatus}</p>
            {/if}
          </div>
        {:else if settingsSection === 'gid'}
          <div class="section-title">
            <div>
              <h1>gid管理</h1>
              <p>Explorerからgidを発行する対象ファイルと、既存GIDを含む桁数変更を管理します</p>
            </div>
          </div>

          <div class="gid-settings-layout">
            <section class="settings-panel gid-settings-form">
              <label>
                <span>gid発番対象拡張子</span>
                <input bind:value={gidSettings.targetExtensions} placeholder="zip;rar;7z" />
                <small>セミコロン（;）区切り。ピリオドは省略できます。保存時はピリオドなしに統一し、入力した左から順にExplorerの「DB管理機能 &gt; gid発行」メニューへ表示します。</small>
              </label>
              <div class="settings-actions">
                <button class="primary-button" onclick={saveGidSettings}>拡張子設定を保存</button>
              </div>
            </section>

            <section class="settings-panel gid-capacity-reference">
              <div>
                <h2>桁数の目安</h2>
                <p>0～9・A～Zの36文字を使い、先頭0と短い桁数のgidを除外して、その桁数ちょうどの値だけを使用する場合の理論上限です。必要桁数は、実際の発番件数、廃番を再利用するか、用途別に発番空間を分けるかによって変わります。</p>
              </div>
              <div class="gid-capacity-table">
                <div><strong>桁数</strong><strong>扱えるファイル数</strong></div>
                <div><span>4桁</span><span>1,632,960（約1.63×10⁶）</span></div>
                <div><span>5桁</span><span>58,786,560（約5.88×10⁷）</span></div>
                <div><span>6桁（初期値）</span><span>2,116,316,160（約2.12×10⁹）</span></div>
                <div><span>7桁</span><span>76,187,381,760（約7.62×10¹⁰）</span></div>
                <div><span>8桁</span><span>2,742,745,743,360（約2.74×10¹²）</span></div>
              </div>
            </section>

            <section class="settings-panel gid-migration-panel">
              <div>
                <h2>既存GIDの桁数変更</h2>
                <p>全作品へ指定桁数の新しいGIDを割り当て、ファイル名、Tag、フィルター割当、イベント履歴を同時に更新します。実行前に本体DBと旧新GID対応表をbackupsフォルダへ保存します。</p>
              </div>
              <div class="gid-migration-controls">
                <label>
                  <span>桁数変更</span>
                  <select bind:value={gidMigrationTargetDigitCount} disabled={gidMigrationPreviewInProgress || gidMigrationInProgress}>
                    <option value={4}>4桁</option>
                    <option value={5}>5桁</option>
                    <option value={6}>6桁</option>
                    <option value={7}>7桁</option>
                    <option value={8}>8桁</option>
                  </select>
                  <small>現在：{gidSettings.digitCount}桁</small>
                </label>
                <span class="gid-migration-control-arrow">→</span>
                <button class="danger-button" disabled={gidMigrationPreviewInProgress || gidMigrationInProgress} onclick={previewGidMigration}>
                  {gidMigrationPreviewInProgress ? '検証中...' : '移行内容を確認'}
                </button>
              </div>
            </section>
          </div>
        {:else if settingsSection === 'keyboardShortcuts'}
          <div class="section-title">
            <div>
              <h1>キーボードショートカット</h1>
              <p>入力欄を選択して、割り当てたいキーの組み合わせを押してください</p>
            </div>
            <button class="quiet-button" onclick={resetKeyboardShortcutSettings}>初期値に戻す</button>
          </div>

          <div class="keyboard-shortcut-settings-layout">
            <section class="settings-panel keyboard-shortcut-settings-panel">
              <div class="keyboard-shortcut-settings-heading">
                <div class="keyboard-shortcut-settings-icon"><Keyboard size={19} /></div>
                <div><strong>アプリ内ショートカット</strong><span>重複するキーの組み合わせは登録できません</span></div>
              </div>
              <div class="keyboard-shortcut-list-header"><span>操作</span><span>対象</span><span>キー割り当て</span><span></span></div>
              <div class="keyboard-shortcut-list">
                {#each keyboardShortcutDefinitions as definition}
                  <div class="keyboard-shortcut-row">
                    <strong>{definition.label}</strong>
                    <span>{definition.scope}</span>
                    <input
                      aria-label={`${definition.label}のキー割り当て`}
                      value={formatKeyboardShortcut(keyboardShortcutSettings[definition.command])}
                      placeholder="未設定"
                      readonly
                      onkeydown={(event) => captureKeyboardShortcut(event, definition.command)}
                    />
                    <button class="icon-danger" title="割り当てを解除" disabled={!keyboardShortcutSettings[definition.command]} onclick={() => clearKeyboardShortcut(definition.command)}><X size={15} /></button>
                  </div>
                {/each}
              </div>
            </section>
          </div>
        {:else if settingsSection === 'gestures'}
          <div class="section-title">
            <div>
              <h1>マウスジェスチャ</h1>
              <p>右ボタンを押したままのジェスチャと軌跡表示を設定します</p>
            </div>
            <button class="quiet-button" onclick={resetMouseGestureSettings}>初期値に戻す</button>
          </div>

          <div class="gesture-settings-layout">
            <section class="settings-panel gesture-options">
              <label class="gesture-enabled">
                <input type="checkbox" bind:checked={mouseGestureSettings.enabled} onchange={updateMouseGestureSettings} />
                <span>有効</span>
              </label>

              <div class="gesture-config-grid">
                <section class="gesture-add-panel">
                  <span class="field-label">ジェスチャでコマンド追加</span>
                  <select bind:value={newGestureDirection}>
                    <option value="↑">↑ 上</option>
                    <option value="↓">↓ 下</option>
                    <option value="←">← 左</option>
                    <option value="→">→ 右</option>
                  </select>
                  <select bind:value={newGestureCommand}>
                    {#each mouseGestureCommands as command}
                      <option value={command.value}>{command.label}</option>
                    {/each}
                  </select>
                  <button onclick={addMouseGestureBinding}>追加</button>
                </section>

                <section class="gesture-binding-panel">
                  <div class="gesture-binding-header"><span>ジェスチャ</span><span>コマンド</span></div>
                  {#if mouseGestureSettings.bindings.length === 0}
                    <div class="gesture-binding-empty">登録されているジェスチャはありません。</div>
                  {:else}
                    {#each mouseGestureSettings.bindings as binding, index}
                      <div class="gesture-binding-row">
                        <strong>{binding.gesture}</strong>
                        <select value={binding.command} onchange={(event) => updateMouseGestureBinding(index, (event.currentTarget as HTMLSelectElement).value as MouseGestureBinding['command'])}>
                          {#each mouseGestureCommands as command}
                            <option value={command.value}>{command.label}</option>
                          {/each}
                        </select>
                        <button class="icon-danger" title="削除" onclick={() => removeMouseGestureBinding(index)}><Trash2 size={16} /></button>
                      </div>
                    {/each}
                  {/if}
                </section>
              </div>

              <section class="gesture-appearance">
                <span class="field-label">外観</span>
                <div class="gesture-appearance-grid">
                  <div class="gesture-preview-field">
                    <span>線のプレビュー</span>
                    <div class="gesture-preview" style={`--gesture-preview-color: ${mouseGestureSettings.lineColor}; --gesture-preview-width: ${mouseGestureSettings.lineWidth}px;`}>
                      <span></span>
                    </div>
                  </div>
                  <label>
                    <span>線の幅</span>
                    <input type="number" min="1" max="16" bind:value={mouseGestureSettings.lineWidth} onchange={updateMouseGestureSettings} />
                  </label>
                  <label>
                    <span>線の色</span>
                    <input type="color" bind:value={mouseGestureSettings.lineColor} onchange={updateMouseGestureSettings} />
                  </label>
                  <label class="gesture-threshold-field">
                    <span>閾値 (px)</span>
                    <input type="number" min="8" max="600" bind:value={mouseGestureSettings.threshold} onchange={updateMouseGestureSettings} />
                  </label>
                </div>
              </section>
            </section>
          </div>
        {:else}
          <div class="section-title">
            <div>
              <h1>新規タブ候補</h1>
              <p>Explorer の新規タブメニューに表示するフォルダを登録します</p>
            </div>
          </div>

          <div class="settings-layout">
            <div class="new-tab-settings-form">
              <section class="settings-panel">
                <div class="field-grid">
                  <label class="wide">
                    <span>フォルダパス</span>
                    <div class="new-tab-path-input">
                      <input bind:value={newTabCandidatePath} placeholder="D:\Gallery\Folder" readonly={selectedNewTabCandidatePath !== ''} />
                      <button type="button" title="フォルダパスをクリップボードに格納" aria-label="フォルダパスをクリップボードに格納" onclick={copyNewTabCandidatePath}><Copy size={17} /></button>
                    </div>
                  </label>
                  <label class="wide">
                    <span>表示名</span>
                    <input bind:value={newTabCandidateLabel} placeholder="空欄の場合はフォルダ名" />
                  </label>
                </div>
                <div class="settings-actions">
                  <button onclick={saveNewTabCandidate}>登録</button>
                </div>
              </section>
              <section class="settings-panel separator-add-panel">
                <span>セパレータ</span>
                <button onclick={addNewTabSeparator}>セパレータを追加</button>
              </section>
            </div>

            <section class="rule-list">
              {#if newTabCandidates.length === 0}
                <div class="empty compact">新規タブ候補はまだ登録されていません。</div>
              {:else}
                {#each newTabCandidates as candidate}
                  <article
                    class:selected-candidate={selectedNewTabCandidatePath === candidate.path}
                    class:separator-row={candidate.kind === 'separator'}
                    class="rule-row"
                    draggable="true"
                    ondragstart={(event) => startNewTabCandidateDrag(event, candidate)}
                    ondragend={() => (draggedNewTabCandidate = null)}
                    ondragover={(event) => event.preventDefault()}
                    ondrop={(event) => reorderNewTabCandidate(event, candidate)}
                  >
                    {#if candidate.kind === 'separator'}
                      <div class="rule-separator" role="separator"></div>
                    {:else}
                      <button class="rule-main" onclick={() => selectNewTabCandidate(candidate)}>
                        <strong>{candidate.label}</strong>
                        <span>{candidate.path}</span>
                      </button>
                    {/if}
                    <button class="icon-danger" title="Delete" onclick={() => deleteNewTabCandidate(candidate)}>
                      <Trash2 size={17} />
                    </button>
                  </article>
                {/each}
              {/if}
            </section>
          </div>
        {/if}
      </section>
    {:else if activeView === 'bookmarks'}
      <section class="content bookmark-content">
        <div class="bookmark-capture-panel">
          <div class="bookmark-capture-icon"><Bookmark size={24} /></div>
          <div>
            <strong>現在のビューをそのまま保存</strong>
            <span>Gallery、Explorer、Creators、Creator TrackingにあるBookmarkアイコンから保存してください。</span>
          </div>
        </div>

        {#if viewBookmarks.length === 0}
          <div class="bookmark-empty">
            <Bookmark size={38} />
            <h2>Bookmarkはまだありません</h2>
            <p>保存したビューはここから同じフィルターやタブ構成で開けます。</p>
          </div>
        {:else}
          <div class="bookmark-grid">
            {#each viewBookmarks as bookmark (bookmark.id)}
              <article class="bookmark-card">
                <button class="bookmark-card-open" onclick={() => openViewBookmark(bookmark)}>
                  <span class="bookmark-card-preview">
                    {#if bookmark.thumbnailDataUrl}
                      <img src={bookmark.thumbnailDataUrl} alt="" />
                    {:else}
                      <span class="bookmark-card-icon">
                        {#if bookmark.viewType === 'library'}
                          <Grid3X3 size={28} />
                        {:else if bookmark.viewType === 'explorer'}
                          <FolderOpen size={28} />
                        {:else if bookmark.viewType === 'creators'}
                          <UserRound size={28} />
                        {:else}
                          <BookOpenText size={28} />
                        {/if}
                      </span>
                    {/if}
                  </span>
                  <span class="bookmark-card-details">
                    <span class="bookmark-card-copy">
                      <strong>{bookmark.name}</strong>
                      <span>{getBookmarkViewLabel(bookmark.viewType)}</span>
                      <small>{describeViewBookmark(bookmark)}</small>
                    </span>
                    <span class="bookmark-card-date">更新 {formatBookmarkDate(bookmark.updatedAt)}</span>
                  </span>
                </button>
                <button class="bookmark-card-delete" title="Bookmarkを削除" onclick={() => (bookmarkDeleteCandidate = bookmark)}><Trash2 size={16} /></button>
              </article>
            {/each}
          </div>
        {/if}
      </section>
    {:else if activeView === 'filters'}
      <section class="content filter-editor-content">
        <div class="filter-editor-view-tabs" role="tablist" aria-label="エディタ視点">
          <button class:active={filterEditorView === 'filter'} role="tab" aria-selected={filterEditorView === 'filter'} onclick={() => setFilterEditorView('filter')}>Manage Filters</button>
          <button class:active={filterEditorView === 'category'} role="tab" aria-selected={filterEditorView === 'category'} onclick={() => setFilterEditorView('category')}>Gallery Mapping</button>
        </div>

        {#if filterEditorView === 'filter'}
          <div class="filter-editor-layout filter-editor-filter-layout">
            <section class="filter-editor-panel filter-editor-catalog" aria-label="フィルタ候補">
              <div class="filter-editor-panel-heading">
                <div>
                  <h2>フィルタアイテム一覧</h2>
                </div>
                <span class="filter-editor-count">{filterEditorAttribute === 'category' ? filterEditorCategoryOptions.length : filterEditorOptions.length}</span>
              </div>
              <div class="filter-editor-attribute-tabs" role="tablist" aria-label="属性">
                <button class:active={filterEditorAttribute === 'category'} role="tab" aria-selected={filterEditorAttribute === 'category'} onclick={() => selectFilterEditorAttribute('category')}>Category</button>
                <button class:active={filterEditorAttribute === 'title'} role="tab" aria-selected={filterEditorAttribute === 'title'} onclick={() => selectFilterEditorAttribute('title')}>Title</button>
                <button class:active={filterEditorAttribute === 'character'} role="tab" aria-selected={filterEditorAttribute === 'character'} onclick={() => selectFilterEditorAttribute('character')}>Character</button>
              </div>
              <div class="filter-editor-catalog-actions" class:has-list-actions={filterEditorAttribute !== 'character'}>
                <button class="filter-editor-new primary-button" onclick={() => filterEditorAttribute === 'category' ? createFilterEditorCategory() : createFilterEditorDefinition(filterEditorAttribute)}><Plus size={16} />Add New Item</button>
                {#if filterEditorAttribute === 'category'}
                  <button class="filter-editor-new primary-button" title="UTF-8（BOMなし）のCSV / TSVで、filter_category_id / Category / New_Category / Add / Merge の5列を読み込みます" onclick={() => postHostMessage({ type: 'filters.editor.category.csv.pick' })}><Download size={16} />Import from List</button>
                  <button class="filter-editor-new primary-button" title="Category一覧をCSVまたはTSVで出力します" onclick={() => postHostMessage({ type: 'filters.editor.category.list.export' })}><Upload size={16} />Export to List</button>
                {:else if filterEditorAttribute === 'title'}
                  <button class="filter-editor-new primary-button" title="UTF-8（BOMなし）のCSV / TSVで、Category / Title / Character の3列を読み込みます" onclick={() => postHostMessage({ type: 'filters.editor.csv.pick' })}><Download size={16} />Import from List</button>
                  <button class="filter-editor-new primary-button" title="Category / Title / Character の一覧をCSVまたはTSVで出力します" onclick={() => postHostMessage({ type: 'filters.editor.list.export' })}><Upload size={16} />Export to List</button>
                {/if}
              </div>
              <label class="filter-editor-search">
                <Search size={16} />
                <input bind:value={filterEditorSearch} placeholder={filterEditorAttribute === 'category' ? 'Categoryを検索' : 'フィルタを検索'} />
              </label>
              {#if filterEditorAttribute !== 'category'}
                <div class="filter-editor-category-filter">
                  <button
                    type="button"
                    class:active={filterEditorCategoryFilters.length > 0}
                    aria-expanded={filterEditorCategoryFilterMenuOpen}
                    onclick={() => filterEditorCategoryFilterMenuOpen = !filterEditorCategoryFilterMenuOpen}
                  >
                    <ListFilter size={16} />
                    Category{filterEditorCategoryFilters.length > 0 ? ` (${filterEditorCategoryFilters.length})` : ''}
                    <ChevronDown size={15} />
                  </button>
                  {#if filterEditorCategoryFilterMenuOpen}
                    <div class="filter-editor-category-filter-menu" role="menu" tabindex="-1" aria-label="Categoryで絞り込み" onpointerdown={(event) => event.stopPropagation()}>
                      <div class="filter-editor-category-filter-menu-heading">
                        <span>Category</span>
                        <button type="button" disabled={filterEditorCategoryFilters.length === 0} onclick={clearFilterEditorCategoryFilters}>解除</button>
                      </div>
                      <div class="filter-editor-category-filter-options">
                        {#each filterEditorCategoryFilterOptions as category}
                          <label>
                            <input
                              type="checkbox"
                              checked={filterEditorCategoryFilters.includes(category)}
                              onchange={() => toggleFilterEditorCategoryFilter(category)}
                            />
                            <span>{category}</span>
                          </label>
                        {/each}
                      </div>
                    </div>
                  {/if}
                </div>
              {/if}
              {#if filterEditorAttribute === 'title'}
                <div class="filter-editor-list-toolbar">
                  <span>並び替え</span>
                  <button
                    type="button"
                    class:active={filterEditorTitleSort === 'characterCount'}
                    title="登録済みCharacter数で並び替え"
                    onclick={toggleFilterEditorTitleCharacterSort}
                  >
                    Character 数
                    {#if filterEditorTitleSort === 'characterCount'}
                      {#if filterEditorTitleSortDirection === 'desc'}<ChevronDown size={14} />{:else}<ChevronUp size={14} />{/if}
                    {/if}
                  </button>
                </div>
              {/if}
              {#if filterEditorAttribute === 'category'}
                <div class="filter-editor-option-list" role="listbox" aria-label="Category一覧">
                  {#if filterEditorCategoryOptions.length === 0}
                    <div class="filter-editor-empty">Categoryがありません</div>
                  {:else}
                    {#each filterEditorCategoryOptions as category}
                      <div
                        class="filter-editor-category-option"
                        role="listitem"
                        draggable="true"
                        ondragstart={(event) => {
                          draggedFilterEditorCategory = category;
                          event.dataTransfer?.setData('text/plain', String(category.id));
                          if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
                        }}
                        ondragend={() => (draggedFilterEditorCategory = null)}
                        ondragover={(event) => event.preventDefault()}
                        ondrop={(event) => reorderFilterEditorCategory(event, category)}
                      >
                        <button class:selected={filterEditorSelectedCategoryId === category.id} role="option" aria-selected={filterEditorSelectedCategoryId === category.id} onclick={() => selectFilterEditorCategory(category)}>
                          <span>{category.name}</span>
                          <small>Title {category.titleCount} / Character {category.characterCount}</small>
                        </button>
                      </div>
                    {/each}
                  {/if}
                </div>
              {:else}
                <div class="filter-editor-option-list" role="listbox" aria-label="既存フィルタ">
                  {#if filterEditorOptions.length === 0}
                    <div class="filter-editor-empty">候補がありません</div>
                  {:else}
                    {#each filterEditorOptions as option}
                      <button class:selected={filterEditorSelectedId === option.id} role="option" aria-selected={filterEditorSelectedId === option.id} onclick={() => selectFilterEditorDefinition(option)}>
                        <span>{option.canonicalName}</span>
                        {#if filterEditorAttribute === 'title'}
                          <small>Character {getTitleCharacterCount(option)}</small>
                        {/if}
                      </button>
                    {/each}
                  {/if}
                </div>
              {/if}
            </section>

            {#if filterEditorAttribute === 'category'}
              <section class="filter-editor-panel filter-editor-definition" aria-label="Category定義">
                <div class="filter-editor-panel-heading">
                  <div>
                    <h2>Category の定義</h2>
                    <span>{filterEditorSelectedCategoryId === null ? '新しいCategory' : '既存Categoryを編集'}</span>
                  </div>
                </div>
                <label>
                  <span>Category名</span>
                  <input bind:value={filterEditorCategoryDraft} placeholder="例：ファンアート" />
                </label>
                <p class="settings-note">Category は Title と Character の候補を揃えるためのマスタです。削除すると、そのCategoryに属するフィルタは未分類になります</p>
                {#if filterEditorCsvReady && filterEditorCsvScope === 'category'}
                  <div class="filter-editor-import-preview">
                    <label class="filter-editor-csv-header">
                      <input type="checkbox" bind:checked={filterEditorCsvHasHeader} onchange={requestFilterEditorCsvPreview} />
                      先頭行はヘッダー
                    </label>
                    <div class="filter-editor-csv-preview">
                      <strong>{filterEditorCsvFileName}</strong><span>{filterEditorCsvTotal}件（先頭20件を表示）</span>
                      <table>
                        <thead><tr><th>行</th><th>ID</th><th>Category</th><th>New_Category</th><th>Add</th><th>Merge</th></tr></thead>
                        <tbody>{#each filterEditorCsvPreview as row}<tr><td>{row.rowNumber}</td><td>{row.filterCategoryId ?? ''}</td><td>{row.categoryName}</td><td>{row.newCategoryName ?? ''}</td><td>{row.add ? 'True' : ''}</td><td>{row.mergeFilterCategoryId ?? ''}</td></tr>{/each}</tbody>
                      </table>
                    </div>
                    <button class="filter-editor-merge-button" onclick={importFilterEditorCsv}>プレビュー内容を登録</button>
                  </div>
                {/if}
                <div class="filter-editor-actions">
                  <button class="primary-button" onclick={saveFilterEditorCategory}>{filterEditorSelectedCategoryId === null ? '登録' : '更新'}</button>
                  <button class="danger-button" disabled={filterEditorSelectedCategoryId === null} onclick={deleteFilterEditorCategory}>削除</button>
                </div>
              </section>
            {:else}
              <div class="filter-editor-definition-column">
              <section class="filter-editor-panel filter-editor-definition" aria-label="フィルタ定義">
                <div class="filter-editor-panel-heading">
                  <div>
                    <h2>{filterEditorAttribute === 'title' ? 'Title' : 'Character'} の定義</h2>
                    <span>{filterEditorSelectedValue || '新しいフィルタ'}</span>
                  </div>
                  <div class="filter-editor-definition-actions">
                    {#if filterEditorAttribute === 'title' && galleryTitleAssignmentReturnPending && galleryTitleAssignment}
                      <button class="quiet-button filter-editor-return-to-title-assignment" onclick={returnToGalleryTitleAssignment}>Title登録に戻る</button>
                    {:else if filterEditorAttribute === 'character' && galleryCharacterAssignmentReturnPending && galleryCharacterAssignment}
                      <button class="quiet-button filter-editor-return-to-title-assignment" onclick={returnToGalleryCharacterAssignment}>Character登録に戻る</button>
                    {/if}
                    <button class="filter-editor-standard-action filter-editor-definition-save" onclick={() => saveFilterEditorDefinition()}>{filterEditorSelectedId === null ? '登録' : '更新'}</button>
                    <button class="danger-button" disabled={filterEditorSelectedId === null} onclick={requestFilterEditorDefinitionDeletion}>削除</button>
                  </div>
                </div>
                <label>
                  <span>表示名</span>
                  <input bind:value={filterEditorCanonicalName} placeholder={filterEditorAttribute === 'title' ? '作品名を入力' : 'キャラクター名を入力'} />
                </label>
                <label>
                  <span>Category</span>
                  <select bind:value={filterEditorCategoryName} onchange={syncFilterEditorParentTitle}>
                    <option value="">未分類</option>
                    {#each filterEditorCategories as category}
                      <option value={category.name}>{category.name}</option>
                    {/each}
                  </select>
                </label>
                {#if filterEditorAttribute === 'character'}
                  <label>
                    <span>Title</span>
                    <select class="filter-editor-select" bind:value={filterEditorParentTitleId}>
                      <option value={null}>未設定</option>
                      {#each filterEditorParentTitleOptions as title}
                        <option value={title.id}>{title.canonicalName}</option>
                      {/each}
                    </select>
                  </label>
                {/if}

                <div class="filter-editor-standard-search">
                  <div>
                    <strong>標準名を検索</strong>
                    <span>Settings の検索エンジン設定に従い、候補を取得します</span>
                  </div>
                  <div class="filter-editor-standard-search-input">
                    <input bind:value={filterEditorStandardNameQuery} placeholder="検索する名称" />
                    <button onclick={openFilterEditorStandardNameSearch}>検索</button>
                  </div>
                  {#if filterEditorStandardNameResults.length > 0}
                    <div class="filter-editor-standard-results">
                      {#each filterEditorStandardNameResults as result}
                        <label class:selected={filterEditorStandardNameSelected === result.name}>
                          <input type="radio" name="standard-name-result" value={result.name} bind:group={filterEditorStandardNameSelected} />
                          <span>{result.name}</span>
                          <small>{result.source}{result.detail ? `: ${result.detail}` : ''}</small>
                        </label>
                      {/each}
                    </div>
                    <button class="filter-editor-standard-transfer" onclick={transferFilterEditorStandardName}>選択した候補を表示名へ転記</button>
                  {/if}
                </div>

                <div class="filter-editor-merge-section">
                <div>
                  <strong>統合候補</strong>
                  <span>同義・表記ゆれの既存フィルタを選択</span>
                </div>
                <input class="filter-editor-merge-search" bind:value={filterEditorMergeSearch} placeholder="候補を検索して手動で選択" disabled={filterEditorSelectedId === null} />
                <div class="filter-editor-merge-list">
                  {#if filterEditorMergeOptions.length === 0}
                    <span class="filter-editor-merge-empty">統合候補がありません</span>
                  {:else}
                    {#each filterEditorMergeOptions as option}
                      <label>
                        <input type="checkbox" checked={filterEditorMergeCandidates.has(option.id)} onchange={() => toggleFilterEditorMergeCandidate(option.id)} disabled={filterEditorSelectedId === null} />
                        <span>{option.canonicalName}</span>
                        <small>{option.itemCount}</small>
                      </label>
                    {/each}
                  {/if}
                </div>
                  <button class="filter-editor-merge-button" disabled={filterEditorSelectedId === null || filterEditorMergeCandidates.size === 0} onclick={mergeFilterEditorDefinitions}>選択したフィルタを統合</button>
                </div>

                {#if filterEditorAttribute === 'title'}
                <div class="filter-editor-categories">
                  <span>表示する区分</span>
                  <div>
                    {#each gallerySections as section}
                      <label>
                        <input
                          type="checkbox"
                          checked={filterEditorSelectedId === null
                            ? filterEditorVisibleCategoriesDraft.includes(section.id)
                            : isFilterEditorVisibleInCategory(selectedFilterEditorDefinition(), section.id)}
                          onchange={() => {
                            const definition = selectedFilterEditorDefinition();
                            if (definition) toggleFilterEditorVisibility(definition, section.id);
                            else toggleFilterEditorDraftVisibility(section.id);
                          }}
                        />
                        <span data-i18n-skip>{section.label}</span>
                      </label>
                    {/each}
                  </div>
                </div>
                {/if}
                {#if filterEditorCsvReady && filterEditorCsvScope === 'combination'}
                  <div class="filter-editor-import-preview">
                    <label class="filter-editor-csv-header">
                      <input type="checkbox" bind:checked={filterEditorCsvHasHeader} onchange={requestFilterEditorCsvPreview} />
                      先頭行はヘッダー
                    </label>
                    <div class="filter-editor-csv-preview">
                      <strong>{filterEditorCsvFileName}</strong><span>{filterEditorCsvTotal}件（先頭20件を表示）</span>
                      <table>
                        <thead><tr><th>行</th><th>Category</th><th>Title</th><th>Character</th></tr></thead>
                        <tbody>{#each filterEditorCsvPreview as row}<tr><td>{row.rowNumber}</td><td>{row.categoryName}</td><td>{row.titleName}</td><td>{row.characterName}</td></tr>{/each}</tbody>
                      </table>
                    </div>
                    {#if filterEditorCsvAnalysis}
                      <section class="filter-editor-import-analysis" aria-label="取り込み前の確認">
                        <div>
                          <strong>取り込み前の確認</strong>
                          <span>Category + Title が既存と一致する行は、Titleと従属Characterを統合します</span>
                        </div>
                        <dl>
                          <button type="button" class:active={filterEditorCsvCaseKey === 'update'} onclick={() => filterEditorCsvCaseKey = 'update'}><dt>更新</dt><dd>{filterEditorCsvAnalysis.updateCount}</dd></button>
                          <button type="button" class:active={filterEditorCsvCaseKey === 'automaticTitleMerge'} onclick={() => filterEditorCsvCaseKey = 'automaticTitleMerge'}><dt>自動Title統合</dt><dd>{filterEditorCsvAnalysis.automaticTitleMergeCount}</dd></button>
                          <button type="button" class:active={filterEditorCsvCaseKey === 'automaticCharacterMove'} onclick={() => filterEditorCsvCaseKey = 'automaticCharacterMove'}><dt>Character移動・統合</dt><dd>{filterEditorCsvAnalysis.automaticCharacterMoveCount}</dd></button>
                          <button type="button" class:active={filterEditorCsvCaseKey === 'explicitMerge'} onclick={() => filterEditorCsvCaseKey = 'explicitMerge'}><dt>明示Merge</dt><dd>{filterEditorCsvAnalysis.explicitMergeCount}</dd></button>
                          <button type="button" class:active={filterEditorCsvCaseKey === 'add'} onclick={() => filterEditorCsvCaseKey = 'add'}><dt>追加</dt><dd>{filterEditorCsvAnalysis.addCount}</dd></button>
                          <button type="button" class:active={filterEditorCsvCaseKey === 'duplicateAddSkip'} onclick={() => filterEditorCsvCaseKey = 'duplicateAddSkip'}><dt>重複スキップ</dt><dd>{filterEditorCsvAnalysis.duplicateAddSkipCount}</dd></button>
                        </dl>
                        <div class="filter-editor-import-analysis-table">
                          <table>
                            <thead><tr><th>行</th><th>処理</th><th>変更前</th><th>変更後</th><th>詳細</th></tr></thead>
                            <tbody>
                              {#each filterEditorCsvAnalysis.cases?.[filterEditorCsvCaseKey] ?? [] as row}
                                <tr>
                                  <td>{row.rowNumber}</td>
                                  <td>{row.action}</td>
                                  <td>{row.sourceCategory} / {row.sourceTitle} / {row.sourceCharacter}</td>
                                  <td>{row.targetCategory} / {row.targetTitle} / {row.targetCharacter}</td>
                                  <td>{row.detail}</td>
                                </tr>
                              {/each}
                            </tbody>
                          </table>
                        </div>
                      </section>
                    {/if}
                    <button class="filter-editor-merge-button" onclick={importFilterEditorCsv}>確認して登録</button>
                  </div>
                {/if}
              </section>
              {#if filterEditorAttribute === 'title' && filterEditorSelectedId !== null}
                <section class="filter-editor-panel filter-editor-registered-characters" aria-label="登録済みCharacter">
                  <div class="filter-editor-panel-heading">
                    <div>
                      <h2>登録済みCharacter</h2>
                      <span>選択中のTitleに従属するCharacter</span>
                    </div>
                    <span class="filter-editor-count">{filterEditorRegisteredCharacters.length}</span>
                  </div>
                  {#if filterEditorRegisteredCharacters.length === 0}
                    <div class="filter-editor-empty">登録済みCharacterはありません</div>
                  {:else}
                    <ul class="filter-editor-registered-character-list">
                      {#each filterEditorRegisteredCharacters as character}
                        <li>
                          <button
                            type="button"
                            title="右クリックでCharacterの定義を開く"
                            oncontextmenu={(event) => openRegisteredCharacterDefinition(event, character)}
                          >{character.canonicalName}</button>
                        </li>
                      {/each}
                    </ul>
                  {/if}
                </section>
              {/if}
              </div>
            {/if}
          </div>
        {:else}
          <div class="filter-editor-category-view" use:clearAllocationSelectionOnBackground>
            <section class="filter-editor-panel filter-editor-category-controls">
              <div class="filter-editor-panel-heading">
                <div>
                  <h2>区分ごとの表示設定</h2>
                  <span>Categoryごとに Title の表示を管理します。Title の設定は従属する Character にも反映されます</span>
                </div>
                <button
                  class="filter-editor-commit primary-button"
                  class:dirty={galleryFilterConfigurationDirty}
                  disabled={galleryFilterCommitInProgress}
                  title="Galleryのフィルタ候補を最新の設定で読み込み直します"
                  onclick={commitGalleryFilterConfiguration}
                >{galleryFilterCommitInProgress ? 'Commit中...' : 'Commit'}</button>
              </div>
              <label class="filter-editor-search">
                <Search size={16} />
                <input bind:value={filterEditorSearch} placeholder="フィルタまたはCategoryを検索" />
              </label>
            </section>

            <section class="filter-editor-panel filter-editor-matrix-panel" aria-label="区分マトリクス">
              <div class="filter-editor-matrix filter-editor-allocation-matrix" style={`--allocation-section-count: ${allocationSections.length};`}>
                <div class="filter-editor-matrix-head">
                  <button onclick={() => toggleAllocationSort('title')}>Title{allocationSortLabel('title')}</button>
                  {#each allocationSections as section}
                    <button data-i18n-skip title={section.label} onclick={() => toggleAllocationSort(section.id)}>{section.label}{allocationSortLabel(section.id)}</button>
                  {/each}
                </div>
                <div class="filter-editor-allocation-scroll" bind:this={allocationScrollElement}>
                  {#if allocationRows.length === 0}
                    <div class="filter-editor-empty">候補がありません</div>
                  {:else}
                    <div class="filter-editor-allocation-virtual-content" style={`height: ${$allocationVirtualizer.getTotalSize()}px;`}>
                      {#each $allocationVirtualizer.getVirtualItems() as virtualItem (virtualItem.key)}
                        {@const item = allocationVirtualItems[virtualItem.index]}
                        <div class="filter-editor-allocation-virtual-item" style={`transform: translateY(${virtualItem.start}px);`}>
                          {#if item.kind === 'category'}
                            <div class="filter-editor-matrix-group filter-editor-allocation-group">
                              <button
                                type="button"
                                class="filter-editor-allocation-toggle"
                                title={isAllocationGroupExpanded(item.category) ? 'Categoryを折りたたむ' : 'Categoryを展開する'}
                                onclick={() => toggleAllocationGroup(item.category)}
                              >
                                {#if isAllocationGroupExpanded(item.category)}<Minus size={14} />{:else}<Plus size={14} />{/if}
                              </button>
                              <span>{item.category}</span>
                            </div>
                          {:else}
                            <div
                              class="filter-editor-matrix-row filter-editor-allocation-row"
                              data-allocation-row-id={item.row.id}
                              use:applyAllocationRowSelection={item.row.id}
                              role="button"
                              tabindex="0"
                              onclick={(event) => selectAllocationRow(event, item.row)}
                              onkeydown={(event) => {
                                if (event.key === 'Enter' || event.key === ' ') {
                                  event.preventDefault();
                                  selectAllocationRow(event as unknown as MouseEvent, item.row);
                                }
                              }}
                            >
                              <button class="filter-editor-allocation-link" oncontextmenu={(event) => openAllocationDefinition(event, item.row)}>{item.row.titleName}</button>
                              {#each allocationSections as section}
                                <label title={`${section.label} に表示`}>
                                  <input type="checkbox" checked={isFilterEditorVisibleInCategory(item.row, section.id)} onclick={(event) => event.stopPropagation()} onchange={(event) => applyAllocationVisibility(item.row, section.id, (event.currentTarget as HTMLInputElement).checked)} />
                                </label>
                              {/each}
                            </div>
                          {/if}
                        </div>
                      {/each}
                    </div>
                  {/if}
                </div>
              </div>
            </section>
          </div>
        {/if}
      </section>
    {:else if activeView === 'tags'}
      <section class="content filter-editor-content tag-management-content" use:clearTagManagementSelectionOnBackground>
        <div class="tag-management-layout">
          <section class="filter-editor-panel tag-management-catalog" aria-label="Tag一覧">
            <div class="filter-editor-panel-heading">
              <div>
                <h2>Tag一覧</h2>
                <span>登録・名称変更・削除・ドラッグで並べ替え</span>
              </div>
              <div class="tag-management-heading-actions">
                {#if galleryTagAssignmentReturnPending && galleryTagAssignment}
                  <button class="quiet-button" onclick={returnToGalleryTagAssignment}>Tag登録に戻る</button>
                {/if}
                <span class="filter-editor-count">{tagManagementListTags.length}</span>
              </div>
            </div>
            <div class="filter-editor-catalog-actions has-list-actions">
              <button class="filter-editor-new primary-button" onclick={createTagManagementDefinition}><Plus size={16} />Add New Item</button>
              <button class="filter-editor-new primary-button" title="tag_id / tag / New_tag / use_flg の4列を読み込みます" onclick={() => postHostMessage({ type: 'tags.manager.csv.pick' })}><Download size={16} />Import from List</button>
              <button class="filter-editor-new primary-button" title="Tag一覧をCSVまたはTSVで出力します" onclick={() => postHostMessage({ type: 'tags.manager.list.export' })}><Upload size={16} />Export to List</button>
            </div>
            <label class="filter-editor-search">
              <Search size={16} />
              <input bind:value={tagManagementSearch} placeholder="Tagを検索" />
            </label>
            <div class="tag-management-definition">
              <label>
                <span>Tag名</span>
                <input bind:value={tagManagementDraft} placeholder="新しいTag名" />
              </label>
              <div class="tag-management-definition-actions">
                <button class="primary-button" onclick={saveTagManagementDefinition}>{tagManagementSelectedId === null ? '登録' : '更新'}</button>
                <button class="danger-button" disabled={tagManagementSelectedId === null} onclick={disableTagManagementDefinition}>削除</button>
              </div>
            </div>
            <div class="filter-editor-option-list tag-management-option-list" role="listbox" aria-label="登録済みTag">
              {#if tagManagementListTags.length === 0}
                <div class="filter-editor-empty">Tagがありません</div>
              {:else}
                {#each tagManagementListTags as tag}
                  <div
                    class:drop-target={tagManagementDropTargetId === tag.id && draggedTagManagementId !== tag.id}
                    class="tag-management-option"
                    role="listitem"
                    draggable="true"
                    ondragstart={(event) => {
                      draggedTagManagementId = tag.id;
                      tagManagementDropTargetId = null;
                      event.dataTransfer?.setData('text/plain', String(tag.id));
                      if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
                    }}
                    ondragend={() => {
                      draggedTagManagementId = null;
                      tagManagementDropTargetId = null;
                    }}
                    ondragover={(event) => {
                      event.preventDefault();
                      tagManagementDropTargetId = tag.id;
                      if (event.dataTransfer) event.dataTransfer.dropEffect = 'move';
                    }}
                    ondrop={(event) => reorderTagManagementDefinition(event, tag)}
                  >
                    <button
                      class:selected={tagManagementSelectedId === tag.id}
                      role="option"
                      aria-selected={tagManagementSelectedId === tag.id}
                      title="ドラッグして表示順を変更"
                      onclick={() => selectTagManagementDefinition(tag)}
                    >
                      <span>{tag.tag}</span>
                      <small>{tag.itemCount}件</small>
                    </button>
                  </div>
                {/each}
              {/if}
            </div>
            {#if tagManagementCsvReady}
              <div class="filter-editor-import-preview tag-management-import-preview">
                <label class="filter-editor-csv-header">
                  <input type="checkbox" bind:checked={tagManagementCsvHasHeader} onchange={requestTagManagementCsvPreview} />
                  先頭行はヘッダー
                </label>
                <div class="filter-editor-csv-preview">
                  <strong>{tagManagementCsvFileName}</strong><span>{tagManagementCsvTotal}件（先頭20件を表示）</span>
                  <table>
                    <thead><tr><th>行</th><th>tag_id</th><th>tag</th><th>New_tag</th><th>use_flg</th></tr></thead>
                    <tbody>{#each tagManagementCsvPreview as row}<tr><td>{row.rowNumber}</td><td>{row.tagId ?? ''}</td><td>{row.tag}</td><td>{row.newTag}</td><td>{row.useFlag ?? ''}</td></tr>{/each}</tbody>
                  </table>
                </div>
                <button class="filter-editor-merge-button" onclick={importTagManagementCsv}>プレビュー内容を登録</button>
              </div>
            {/if}
          </section>

          <section class="filter-editor-panel filter-editor-matrix-panel tag-management-matrix-panel" aria-label="Tag Gallery Mapping">
            <div class="filter-editor-panel-heading tag-management-matrix-heading">
              <div>
                <h2>Gallery Mapping</h2>
                <span>Tagを縦軸、Gallery区分を横軸にして割り当てます。複数行選択にも対応します</span>
              </div>
            </div>
            <div class="filter-editor-allocation-matrix tag-management-matrix" style={`--tag-section-count: ${gallerySections.length};`}>
              <div class="filter-editor-matrix-head tag-management-matrix-head">
                <button onclick={() => toggleTagManagementSort('tag')}>Tag{tagManagementSortLabel('tag')}</button>
                {#each gallerySections as section}
                  <button data-i18n-skip title={section.label} onclick={() => toggleTagManagementSort(section.id)}>{section.label}{tagManagementSortLabel(section.id)}</button>
                {/each}
              </div>
              <div class="tag-management-matrix-scroll">
                {#if tagManagementVisibleTags.length === 0}
                  <div class="filter-editor-empty">Tagがありません</div>
                {:else}
                  {#each tagManagementVisibleTags as tag}
                    <div
                      class:selected={tagManagementSelectedIds.has(tag.id)}
                      class="filter-editor-matrix-row filter-editor-allocation-row tag-management-matrix-row"
                      role="button"
                      tabindex="0"
                      onclick={(event) => selectTagManagementRow(event, tag)}
                      onkeydown={(event) => {
                        if (event.key === 'Enter' || event.key === ' ') {
                          event.preventDefault();
                          selectTagManagementRow(event as unknown as MouseEvent, tag);
                        }
                      }}
                    >
                      <button type="button" tabindex="-1" onclick={(event) => selectTagManagementRow(event, tag)}>{tag.tag}<small>{tag.itemCount}件</small></button>
                      {#each gallerySections as section}
                        <label title={`${section.label} で有効`}>
                          <input
                            type="checkbox"
                            checked={tag.categories.includes(section.id)}
                            onclick={(event) => event.stopPropagation()}
                            onchange={(event) => applyTagManagementMapping(tag, section.id, (event.currentTarget as HTMLInputElement).checked)}
                          />
                        </label>
                      {/each}
                    </div>
                  {/each}
                {/if}
              </div>
            </div>
          </section>
        </div>
      </section>
    {:else if activeView === 'board'}
      <section class="content board-content">
        {#if stickyNoteBoardIsLoading}
          <div class="board-status"><RefreshCw size={22} /> 付箋を読み込んでいます...</div>
        {:else if stickyNoteBoardError}
          <div class="board-status error"><span>{stickyNoteBoardError}</span><button onclick={loadStickyNoteBoard}>再試行</button></div>
        {:else if stickyNoteBoardItems.length === 0}
          <div class="board-empty"><StickyNote size={38} /><h2>付箋はまだありません</h2><p>Gallery、Explorer、Creators、Creator Tracking、User Metricsから付箋を作成できます。</p></div>
        {:else}
          {#each stickyNoteBoardDisplayGroups as group}
            <section class="board-group">
              {#if stickyNoteBoardMode === 'grouped'}
                <header><span>{group.label}</span><small>{group.items.length} Notes</small></header>
              {/if}
              <div class="board-note-grid">
                {#each group.items as item (item.note.id)}
                  {@const note = item.note}
                  {@const noteColor = getStickyNotePaletteEntry(note.colorKey)}
                  <article class="board-note-item">
                    <div class="board-note-location" title={`${getStickyNoteViewLabel(note.viewType)} > ${getStickyNoteBoardContextLabel(note)}`}>
                      <strong>{getStickyNoteViewLabel(note.viewType)}</strong><ChevronRight size={13} /><span data-i18n-skip>{getStickyNoteBoardContextLabel(note)}</span>
                      {#if item.bookmarkCount > 0}<small><Bookmark size={12} />{item.bookmarkCount}</small>{/if}
                    </div>
                    <div
                      class="board-note-card"
                      data-board-note-id={note.id}
                      style={`--sticky-note-color: ${noteColor.color}; --sticky-note-border: ${noteColor.border}; --sticky-note-text: ${noteColor.text};`}
                    >
                      <div class="board-note-actions">
                        <button
                          class:active={note.contentMode === 'markdown'}
                          title={note.contentMode === 'markdown' ? 'プレーンテキストに切り替え' : 'Markdownに切り替え'}
                          onclick={() => toggleStickyNoteBoardContentMode(note.id)}
                        >{#if note.contentMode === 'markdown'}<Code2 size={15} />{:else}<FileText size={15} />{/if}</button>
                        <div class="board-note-palette-anchor">
                          <button class:active={stickyNotePaletteOpenId === note.id} class="sticky-note-palette-toggle" title="付箋の色を変更" onclick={() => toggleStickyNotePalette(note.id)}><Palette size={15} /></button>
                          {#if stickyNotePaletteOpenId === note.id}
                            <div class="sticky-note-palette board-note-palette" role="group" aria-label="付箋の色" onpointerdown={(event) => event.stopPropagation()}>
                              {#each stickyNotePalette as color}
                                <button
                                  class:selected={note.colorKey === color.key}
                                  style={`--sticky-note-swatch: ${color.color}; --sticky-note-swatch-border: ${color.border};`}
                                  title={color.label}
                                  aria-label={color.label}
                                  aria-pressed={note.colorKey === color.key}
                                  onclick={() => setStickyNoteBoardColor(note.id, color.key)}
                                >{#if note.colorKey === color.key}<Check size={13} />{/if}</button>
                              {/each}
                            </div>
                          {/if}
                        </div>
                        <button class="board-note-delete" title="付箋を削除" onclick={() => deleteStickyNoteBoardItem(note.id)}><Trash2 size={15} /></button>
                      </div>
                      {#if note.contentMode === 'plain' || markdownEditingNoteIds.has(note.id)}
                        <textarea
                          value={note.content}
                          aria-label="付箋の本文"
                          placeholder={note.contentMode === 'markdown' ? 'Markdownを入力...' : 'メモを入力...'}
                          spellcheck="false"
                          oninput={(event) => updateStickyNoteBoardItem(note.id, { content: event.currentTarget.value })}
                          onblur={() => { if (note.contentMode === 'markdown') setMarkdownNoteEditing(note.id, false); }}
                        ></textarea>
                      {:else}
                        <div class="sticky-note-markdown board-note-markdown" role="button" tabindex="0" onclick={(event) => handleStickyNoteMarkdownClick(event, note.id)} onkeydown={(event) => { if (event.key === 'Enter') setMarkdownNoteEditing(note.id, true); }}>
                          {#if note.content.trim()}{@html renderStickyNoteMarkdown(note.content)}{:else}<span class="sticky-note-markdown-empty">クリックしてMarkdownを編集...</span>{/if}
                        </div>
                      {/if}
                    </div>
                  </article>
                {/each}
              </div>
            </section>
          {/each}
        {/if}
      </section>
    {:else if activeView === 'calendar'}
      <section class="calendar-content">
        <header class="calendar-content-heading">
          <div>
            <span class="calendar-heading-icon"><CalendarCheck size={22} /></span>
            <div>
              <h1>Subscription Calendar</h1>
              <p>有効なサブスクの更新予定をCreator Trackingから集約します</p>
            </div>
          </div>
          <div class="calendar-controls">
            <button type="button" onclick={() => shiftCalendarMonth(-1)}><ChevronLeft size={16} />前月</button>
            <strong>{getCalendarMonthTitle()}</strong>
            <button type="button" onclick={() => shiftCalendarMonth(1)}>翌月<ChevronRight size={16} /></button>
            <button type="button" onclick={resetCalendarMonthToToday}>Today</button>
            <div class="calendar-view-toggle" role="group" aria-label="Calendar view">
              <button class:active={calendarViewMode === 'month'} onclick={() => (calendarViewMode = 'month')}>Month</button>
              <button class:active={calendarViewMode === 'focus'} onclick={() => (calendarViewMode = 'focus')}>2 Weeks</button>
            </div>
          </div>
        </header>

        {#if calendarIsLoading}
          <div class="calendar-status"><RefreshCw size={22} /> Calendarを読み込んでいます...</div>
        {:else if calendarError}
          <div class="calendar-status error"><span>{calendarError}</span><button onclick={loadCalendarSubscriptions}>再試行</button></div>
        {:else}
          <div class:focus={calendarViewMode === 'focus'} class="calendar-grid">
            {#each getCalendarWeekdayLabels() as label}
              <div class="calendar-weekday">{label}</div>
            {/each}
            {#each (calendarViewMode === 'month' ? getCalendarMonthCells() : getCalendarFocusCells()) as cell}
              <article class:muted={!cell.inMonth && calendarViewMode === 'month'} class:today={cell.isToday} class="calendar-day-cell">
                <div class="calendar-day-head">
                  <span>{cell.day}</span>
                  {#if cell.events.length > 0}<small>{cell.events.length}</small>{/if}
                </div>
                <div class="calendar-day-events">
                  {#each cell.events as event (event.id)}
                    <button
                      class:ending={event.endingPlanned}
                      class:reminder={event.reminder}
                      class="calendar-event-pill"
                      title={`${event.displayName} / ${event.platform}${event.plan ? ` / ${event.plan}` : ''}`}
                      onclick={() => openCalendarEventCreator(event)}
                    >
                      <strong data-i18n-skip>{event.displayName}</strong>
                      <span data-i18n-skip>{event.platform}{event.plan ? ` / ${event.plan}` : ''}</span>
                      {#if formatCalendarEventAmount(event)}<small>{formatCalendarEventAmount(event)}</small>{/if}
                    </button>
                  {/each}
                </div>
              </article>
            {/each}
          </div>
          {#if calendarEvents.length === 0}
            <div class="calendar-empty"><CalendarCheck size={34} /><h2>更新予定はありません</h2><p>Creator Trackingで有効なサブスクの更新予定日を登録するとここに表示されます。</p></div>
          {/if}
        {/if}
      </section>
    {:else if activeView === 'userMetrics'}
      <section class="user-metrics-content">
        <header class="user-metrics-heading">
          <div class="user-metrics-heading-copy">
            <span class="user-metrics-heading-icon"><ChartNoAxesCombined size={22} /></span>
            <div>
              <h1>
                <span data-i18n-skip>{gallerySections.find(section => section.id === userMetricsCategory)?.label ?? userMetricsCategory}</span>
                <em>OVERVIEW</em>
              </h1>
              <p>作品規模・評価・Creator Tracking・課金記録を横断した区分別ダッシュボード</p>
            </div>
          </div>
          <div class="user-metrics-category-tabs" aria-label="集計区分">
            {#each gallerySections as section}
              <button data-i18n-skip class:active={userMetricsCategory === section.id} disabled={userMetricsIsLoading} onclick={() => selectUserMetricsCategory(section.id)}>{section.label}</button>
            {/each}
          </div>
        </header>

        {#if userMetricsIsLoading}
          <div class="user-metrics-loading"><RefreshCw size={22} /> User Metricsを集計しています...</div>
        {:else if userMetricsError}
          <div class="user-metrics-loading error"><span>{userMetricsError}</span><button onclick={() => loadUserMetrics()}>再試行</button></div>
        {:else if userMetricsDashboard}
          <div class="user-metrics-meta">
            <span data-i18n-skip>{gallerySections.find(section => section.id === userMetricsDashboard?.category)?.label ?? userMetricsDashboard.category}</span>
            <small>集計日時 {userMetricsDashboard.generatedAt}</small>
          </div>

          <div class="user-metrics-kpi-grid">
            <article class="user-metrics-kpi lime">
              <span><File size={20} /></span><small>総ファイル数</small>
              <strong>{userMetricsDashboard.totalFiles.toLocaleString('ja-JP')}</strong>
              <em>{userMetricsDashboard.creatorCount.toLocaleString('ja-JP')} Creators</em>
            </article>
            <article class="user-metrics-kpi cyan">
              <span><Grid3X3 size={20} /></span><small>総画像枚数</small>
              <strong>{userMetricsDashboard.totalImages.toLocaleString('ja-JP')}</strong>
              <em>平均 {userMetricsDashboard.totalFiles > 0 ? Math.round(userMetricsDashboard.totalImages / userMetricsDashboard.totalFiles).toLocaleString('ja-JP') : 0} 枚/作品</em>
            </article>
            <article class="user-metrics-kpi amber">
              <span><Star size={20} /></span><small>トータル評価値</small>
              <strong>{userMetricsDashboard.totalRating.toLocaleString('ja-JP')}</strong>
              <em>評価済み {userMetricsDashboard.ratedFiles.toLocaleString('ja-JP')}作品</em>
            </article>
            <article class="user-metrics-kpi violet">
              <span><Trophy size={20} /></span><small>評価カバレッジ</small>
              <strong>{userMetricsDashboard.totalFiles > 0 ? Math.round(userMetricsDashboard.ratedFiles / userMetricsDashboard.totalFiles * 100) : 0}<b>%</b></strong>
              <em>平均 {userMetricsDashboard.ratedFiles > 0 ? (userMetricsDashboard.totalRating / userMetricsDashboard.ratedFiles).toFixed(2) : '0.00'}★</em>
            </article>
            <article class="user-metrics-kpi rose">
              <span><BookOpenText size={20} /></span><small>Title / Character</small>
              <strong>{userMetricsDashboard.titleCount.toLocaleString('ja-JP')}<b> / </b>{userMetricsDashboard.characterCount.toLocaleString('ja-JP')}</strong>
              <em>作品世界の広がり</em>
            </article>
            <article class="user-metrics-kpi blue">
              <span><Tags size={20} /></span><small>有効Tag</small>
              <strong>{userMetricsDashboard.tagCount.toLocaleString('ja-JP')}</strong>
              <em>作品横断の特徴量</em>
            </article>
          </div>

          {#if userMetricsDashboard.insights.length > 0}
            <section class="user-metrics-insights">
              <div class="user-metrics-section-title"><span><ChartNoAxesCombined size={17} /></span><div><h2>INSIGHTS</h2><p>現在のデータから見える特徴</p></div></div>
              <div class="user-metrics-insight-grid">
                {#each userMetricsDashboard.insights as insight}
                  <article class={`user-metrics-insight ${insight.tone}`}>
                    <small>{insight.title}</small><strong>{insight.value}</strong><p>{insight.description}</p>
                  </article>
                {/each}
              </div>
            </section>
          {/if}

          <section class="user-metrics-panel user-metrics-archive-panel">
            <div class="user-metrics-panel-heading">
              <div class="user-metrics-section-title"><span><Archive size={17} /></span><div><h2>ARCHIVE GROWTH</h2><p>総ファイル数と総画像枚数の累積推移・直近24か月</p></div></div>
              <div class="user-metrics-chart-legend"><span class="files">ファイル数</span><span class="images">画像枚数</span></div>
            </div>
            {#if userMetricsDashboard.trends.length === 0}
              <div class="user-metrics-empty">推移データがありません</div>
            {:else}
              <svg class="user-metrics-line-chart" viewBox="0 0 680 220" role="img" aria-label="総ファイル数と総画像枚数の推移">
                <defs>
                  <linearGradient id="userMetricsArea" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#dfff35" stop-opacity=".26"/><stop offset="1" stop-color="#dfff35" stop-opacity="0"/></linearGradient>
                </defs>
                {#each [48, 90, 132, 174] as y}<line x1="34" x2="646" y1={y} y2={y} class="grid" />{/each}
                <polyline class="images" points={getUserMetricsTrendLine(userMetricsDashboard.trends, 'imageCount')} />
                <polyline class="files" points={getUserMetricsTrendLine(userMetricsDashboard.trends, 'fileCount')} />
                {#each userMetricsDashboard.trends as point, index}
                  {#if index % Math.max(1, Math.ceil(userMetricsDashboard.trends.length / 6)) === 0 || index === userMetricsDashboard.trends.length - 1}
                    <text x={getUserMetricsTrendX(index, userMetricsDashboard.trends.length)} y="210" text-anchor="middle">{point.period.slice(2).replace('-', '/')}</text>
                  {/if}
                {/each}
              </svg>
            {/if}
          </section>

          <div class="user-metrics-ranking-grid">
            <section class="user-metrics-panel user-metrics-ranking-panel files">
              <div class="user-metrics-panel-heading"><div><h2>FILE RANKING</h2><p>総ファイル数</p></div><File size={18} /></div>
              <div class="user-metrics-mini-tabs">
                {#each userMetricsEntityOptions as option}<button class:active={userMetricsFileEntity === option.key} onclick={() => userMetricsFileEntity = option.key}>{option.label}</button>{/each}
              </div>
              <div class="user-metrics-ranking-list">
                {#each getUserMetricsRanking(userMetricsDashboard, 'files', userMetricsFileEntity) as item, index}
                  <div class="user-metrics-ranking-row"><b>{index + 1}</b><div><span>{item.label}</span><i style={`--rank-width:${getUserMetricsRankPercent(getUserMetricsRanking(userMetricsDashboard, 'files', userMetricsFileEntity), item, 'files')}%`}></i><small>直近90日 +{item.recentFileCount.toLocaleString('ja-JP')}</small></div><strong>{item.fileCount.toLocaleString('ja-JP')}</strong></div>
                {/each}
              </div>
            </section>

            <section class="user-metrics-panel user-metrics-ranking-panel images">
              <div class="user-metrics-panel-heading"><div><h2>IMAGE RANKING</h2><p>総画像枚数</p></div><Grid3X3 size={18} /></div>
              <div class="user-metrics-mini-tabs">
                {#each userMetricsEntityOptions as option}<button class:active={userMetricsImageEntity === option.key} onclick={() => userMetricsImageEntity = option.key}>{option.label}</button>{/each}
              </div>
              <div class="user-metrics-ranking-list">
                {#each getUserMetricsRanking(userMetricsDashboard, 'images', userMetricsImageEntity) as item, index}
                  <div class="user-metrics-ranking-row"><b>{index + 1}</b><div><span>{item.label}</span><i style={`--rank-width:${getUserMetricsRankPercent(getUserMetricsRanking(userMetricsDashboard, 'images', userMetricsImageEntity), item, 'images')}%`}></i><small>{item.fileCount.toLocaleString('ja-JP')}作品</small></div><strong>{formatUserMetricsCompact(item.imageCount)}</strong></div>
                {/each}
              </div>
            </section>

            <section class="user-metrics-panel user-metrics-ranking-panel rating">
              <div class="user-metrics-panel-heading"><div><h2>RATING RANKING</h2><p>トータル評価値</p></div><Star size={18} /></div>
              <div class="user-metrics-mini-tabs">
                {#each userMetricsEntityOptions as option}<button class:active={userMetricsRatingEntity === option.key} onclick={() => userMetricsRatingEntity = option.key}>{option.label}</button>{/each}
              </div>
              <div class="user-metrics-ranking-list">
                {#each getUserMetricsRanking(userMetricsDashboard, 'rating', userMetricsRatingEntity) as item, index}
                  <div class="user-metrics-ranking-row"><b>{index + 1}</b><div><span>{item.label}</span><i style={`--rank-width:${getUserMetricsRankPercent(getUserMetricsRanking(userMetricsDashboard, 'rating', userMetricsRatingEntity), item, 'rating')}%`}></i><small>平均 {item.averageRating.toFixed(2)}★</small></div><strong>{item.totalRating.toLocaleString('ja-JP')}★</strong></div>
                {/each}
              </div>
            </section>
          </div>

          <div class="user-metrics-tracking-grid">
            <section class="user-metrics-panel user-metrics-tracking-ranking">
              <div class="user-metrics-panel-heading"><div><h2>CREATOR TENDENCY</h2><p>Creator Tracking「作品の傾向」</p></div><Trophy size={18} /></div>
              <select bind:value={userMetricsMetricKey} aria-label="作品の傾向の指標">
                {#each userMetricsDashboard.metricRankings as ranking}<option value={ranking.key}>{getUserMetricsMetricLabel(ranking.key, creatorTrackingSettingsDraft.metrics)}</option>{/each}
              </select>
              <div class="user-metrics-score-list">
                {#each getUserMetricsMetricRanking(userMetricsDashboard, userMetricsMetricKey) as item, index}
                  <div><b>{index + 1}</b><span>{item.creator}</span><i style={`--score-width:${item.score / (userMetricsMetricKey === 'overall' ? 5 : 4) * 100}%`}></i><strong>{item.score.toFixed(userMetricsMetricKey === 'overall' ? 1 : 0)}</strong></div>
                {/each}
              </div>
            </section>

            <section class="user-metrics-panel user-metrics-site-ranking">
              <div class="user-metrics-panel-heading"><div><h2>SITE REACH</h2><p>活動サイト別の作者数</p></div><ExternalLink size={18} /></div>
              <div class="user-metrics-site-list">
                {#each userMetricsDashboard.siteRankings as item, index}
                  {@const siteIcon = getCreatorTrackingActivityIcon(item.label)}
                  <div><b>{index + 1}</b><span class="user-metrics-site-icon">{#if siteIcon}<img src={siteIcon} alt="" />{:else}<ExternalLink size={15} />{/if}</span><span>{item.label}</span><i style={`--site-width:${item.creatorCount / Math.max(1, userMetricsDashboard.siteRankings[0]?.creatorCount ?? 1) * 100}%`}></i><strong>{item.creatorCount}</strong></div>
                {/each}
              </div>
            </section>

            <section class="user-metrics-panel user-metrics-follow-ranking">
              <div class="user-metrics-panel-heading"><div><h2>FOLLOW LONGEVITY</h2><p>フォローしている日数</p></div><Footprints size={18} /></div>
              <div class="user-metrics-ranking-list compact">
                {#each userMetricsDashboard.followDayRankings as item, index}
                  <div class="user-metrics-ranking-row"><b>{index + 1}</b><div><span>{item.label}</span><i style={`--rank-width:${getUserMetricsRankPercent(userMetricsDashboard.followDayRankings, item, 'trackingDays')}%`}></i><small>{item.fileCount.toLocaleString('ja-JP')}作品</small></div><strong>{item.trackingDays.toLocaleString('ja-JP')}日</strong></div>
                {/each}
              </div>
            </section>
          </div>

          <div class="user-metrics-analysis-grid">
            <section class="user-metrics-panel user-metrics-spend-panel">
              <div class="user-metrics-panel-heading"><div><h2>SPEND HISTORY</h2><p>月別課金額と累計課金額（保存済み為替レートで{userMetricsDashboard.displayCurrency}換算）</p></div><Coins size={18} /></div>
              {#if userMetricsDashboard.trends.length > 0}
                {@const monthlySpendMax = Math.max(1, ...userMetricsDashboard.trends.map(point => point.spend))}
                <svg class="user-metrics-spend-chart" viewBox="0 0 680 220" role="img" aria-label="課金額の推移">
                  {#each [48, 90, 132, 174] as y}<line x1="34" x2="646" y1={y} y2={y} class="grid" />{/each}
                  {#each userMetricsDashboard.trends as point, index}
                    <rect x={getUserMetricsTrendX(index, userMetricsDashboard.trends.length) - 6} y={192 - point.spend / monthlySpendMax * 142} width="12" height={point.spend / monthlySpendMax * 142} rx="3" />
                  {/each}
                  <polyline points={getUserMetricsTrendLine(userMetricsDashboard.trends, 'cumulativeSpend')} />
                  {#each userMetricsDashboard.trends as point, index}
                    {#if index % Math.max(1, Math.ceil(userMetricsDashboard.trends.length / 6)) === 0 || index === userMetricsDashboard.trends.length - 1}<text x={getUserMetricsTrendX(index, userMetricsDashboard.trends.length)} y="210" text-anchor="middle">{point.period.slice(2).replace('-', '/')}</text>{/if}
                  {/each}
                </svg>
              {:else}<div class="user-metrics-empty">課金履歴がありません</div>{/if}
              <div class="user-metrics-spend-summary"><span>累計 <strong>{formatUserMetricsCurrency(userMetricsDashboard.trends.at(-1)?.cumulativeSpend ?? 0, userMetricsDashboard.displayCurrency)}</strong></span><span>Creator別1位 <strong>{userMetricsDashboard.spendRankings[0]?.label ?? '-'}</strong></span></div>
            </section>

            <section class="user-metrics-panel user-metrics-bubble-panel">
              <div class="user-metrics-panel-heading"><div><h2>TITLE VALUE MAP</h2><p>横軸: 合計評価値 / 縦軸:「{userMetricsDashboard.targetTag}」作品数 / 大きさ: ファイル数</p></div><ChartNoAxesCombined size={18} /></div>
              {#if userMetricsDashboard.titleBubbles.length > 0}
                <svg class="user-metrics-bubble-chart" viewBox="0 0 660 270" role="img" aria-label="Title別バブルチャート">
                  <line x1="44" x2="632" y1="242" y2="242" class="axis"/><line x1="44" x2="44" y1="38" y2="242" class="axis"/>
                  {#each userMetricsDashboard.titleBubbles as item, index}
                    <g>
                      <circle cx={getUserMetricsBubbleX(item, userMetricsDashboard.titleBubbles)} cy={getUserMetricsBubbleY(item, userMetricsDashboard.titleBubbles)} r={getUserMetricsBubbleRadius(item, userMetricsDashboard.titleBubbles)} class:highlight={index < 4}><title>{item.title}: 評価{item.totalRating} / {userMetricsDashboard.targetTag}{item.targetTagFileCount}件 / {item.fileCount}作品</title></circle>
                      {#if index < 10}<text x={getUserMetricsBubbleX(item, userMetricsDashboard.titleBubbles)} y={getUserMetricsBubbleY(item, userMetricsDashboard.titleBubbles) + 3} text-anchor="middle">{item.title.length > 8 ? `${item.title.slice(0, 7)}…` : item.title}</text>{/if}
                    </g>
                  {/each}
                  <text x="620" y="260" text-anchor="end">合計評価値 →</text><text x="12" y="28">{userMetricsDashboard.targetTag} ↑</text>
                </svg>
              {:else}<div class="user-metrics-empty">Titleデータがありません</div>{/if}
            </section>
          </div>
        {:else}
          <div class="user-metrics-loading">集計対象を選択してください</div>
        {/if}
      </section>
    {:else if activeView === 'creatorTracking'}
      <section class="creator-tracking-content">
        {#if creatorTrackingIsLoading}
          <div class="empty">Creator Trackingを読み込んでいます...</div>
        {:else if creatorTrackingError}
          <div class="empty">{creatorTrackingError}</div>
        {:else if creatorTracking}
          <div class="creator-tracking-shell">
            <section class="creator-tracking-hero">
              <div class="creator-tracking-avatar">
                {#if creatorTrackingSummary && galleryThumbnails[creatorTrackingSummary.id]}
                  <img src={galleryThumbnails[creatorTrackingSummary.id]} alt="" />
                {:else}
                  <UserRound size={42} />
                {/if}
              </div>
              <div class="creator-tracking-identity">
                <h1>{formatCreatorTrackingHeading(creatorTracking)}</h1>
                <div class="creator-tracking-site-icons" aria-label="活動場所">
                  {#each creatorTracking.activityLinks as link}
                    {@const siteIcon = getCreatorTrackingActivityIcon(link.label)}
                    {#if siteIcon}<img class:follow-up={link.followUpEnabled} src={siteIcon} alt={link.label} title={link.followUpEnabled ? `${link.label}（フォローアップ対象）` : link.label} />{/if}
                  {/each}
                </div>
                <div class="creator-tracking-badges">
                  <span class="activity">{creatorTracking.activityStatus || '不明'}</span>
                  <span>{creatorTracking.trackingStatus || '未設定'}</span>
                </div>
              </div>
              {#if creatorTrackingSummary}
                {@const checkAge = getCreatorTrackingCheckAge(creatorTracking.lastActivityOn, creatorTracking.activityLinks)}
                <div class="creator-tracking-summary-stats">
                  <div class:warning={checkAge.level === 'warning'} class:danger={checkAge.level === 'danger'}>
                    <span>Since last check</span>
                    <strong>{checkAge.days === null ? '-' : `${checkAge.days.toLocaleString('en-US')} Days`}</strong>
                  </div>
                  <div><span>Last update</span><strong>{formatGalleryDate(creatorTrackingSummary.lastUpdatedTime)}</strong></div>
                </div>
              {/if}
            </section>

            <div class="creator-tracking-scroll-region">
              {#if creatorTrackingDashboard}
              {@const archiveSeries = getCreatorTrackingArchiveSeries(creatorTrackingDashboard, creatorTrackingArchiveScale)}
              {@const titleComposition = creatorTrackingSummary?.titleComposition ?? []}
              {@const tagComposition = creatorTrackingSummary?.tagComposition ?? []}
              {@const titleCompositionSegments = getCreatorTrackingCompositionSegments(titleComposition)}
              {@const tagCompositionSegments = getCreatorTrackingCompositionSegments(tagComposition)}
              <section class="creator-tracking-dashboard">
                <header class="creator-tracking-dashboard-header">
                  <div class="creator-tracking-panel-title">
                    <div class="creator-tracking-panel-icon"><ChartNoAxesCombined size={18} /></div>
                    <div><h2>SUMMARY</h2><p>{getGallerySectionLabel(creatorTrackingDashboard.category)}内の集計と推移</p></div>
                  </div>
                </header>

                <div class="creator-tracking-dashboard-layout">
                  <div class="creator-tracking-kpis">
                    <article class="creator-tracking-kpi">
                      <div class="creator-tracking-kpi-icon files"><File size={19} /></div>
                      <div class="creator-tracking-kpi-body">
                        <span>総ファイル数</span>
                        <strong>{creatorTrackingDashboard.fileCount.toLocaleString('ja-JP')}</strong>
                        <small class:gold={getCreatorTrackingRankTier(creatorTrackingDashboard.fileRank, creatorTrackingDashboard.creatorCount) === 'gold'} class:silver={getCreatorTrackingRankTier(creatorTrackingDashboard.fileRank, creatorTrackingDashboard.creatorCount) === 'silver'} class:bronze={getCreatorTrackingRankTier(creatorTrackingDashboard.fileRank, creatorTrackingDashboard.creatorCount) === 'bronze'}>
                          {#if getCreatorTrackingRankTier(creatorTrackingDashboard.fileRank, creatorTrackingDashboard.creatorCount)}<Trophy size={13} />{/if}{formatCreatorTrackingRank(creatorTrackingDashboard.fileRank, creatorTrackingDashboard.creatorCount)}
                        </small>
                      </div>
                    </article>
                    <article class="creator-tracking-kpi">
                      <div class="creator-tracking-kpi-icon images"><Grid3X3 size={19} /></div>
                      <div class="creator-tracking-kpi-body">
                        <span>総枚数</span>
                        <strong>{creatorTrackingDashboard.totalImageCount.toLocaleString('ja-JP')}</strong>
                        <small class:gold={getCreatorTrackingRankTier(creatorTrackingDashboard.totalImageCountRank, creatorTrackingDashboard.creatorCount) === 'gold'} class:silver={getCreatorTrackingRankTier(creatorTrackingDashboard.totalImageCountRank, creatorTrackingDashboard.creatorCount) === 'silver'} class:bronze={getCreatorTrackingRankTier(creatorTrackingDashboard.totalImageCountRank, creatorTrackingDashboard.creatorCount) === 'bronze'}>
                          {#if getCreatorTrackingRankTier(creatorTrackingDashboard.totalImageCountRank, creatorTrackingDashboard.creatorCount)}<Trophy size={13} />{/if}{formatCreatorTrackingRank(creatorTrackingDashboard.totalImageCountRank, creatorTrackingDashboard.creatorCount)}
                        </small>
                      </div>
                    </article>
                    <article class="creator-tracking-kpi">
                      <div class="creator-tracking-kpi-icon rating creator-tracking-kpi-rating-stars"><Star size={12} /><Star size={12} /><Star size={12} /></div>
                      <div class="creator-tracking-kpi-body">
                        <span>トータル評価値</span>
                        <strong>{creatorTrackingDashboard.totalRating.toLocaleString('ja-JP')}</strong>
                        <small class:gold={getCreatorTrackingRankTier(creatorTrackingDashboard.totalRatingRank, creatorTrackingDashboard.creatorCount) === 'gold'} class:silver={getCreatorTrackingRankTier(creatorTrackingDashboard.totalRatingRank, creatorTrackingDashboard.creatorCount) === 'silver'} class:bronze={getCreatorTrackingRankTier(creatorTrackingDashboard.totalRatingRank, creatorTrackingDashboard.creatorCount) === 'bronze'}>
                          {#if getCreatorTrackingRankTier(creatorTrackingDashboard.totalRatingRank, creatorTrackingDashboard.creatorCount)}<Trophy size={13} />{/if}{formatCreatorTrackingRank(creatorTrackingDashboard.totalRatingRank, creatorTrackingDashboard.creatorCount)}
                        </small>
                      </div>
                    </article>
                    <article class="creator-tracking-kpi">
                      <div class="creator-tracking-kpi-icon tracking"><Footprints size={19} /></div>
                      <div class="creator-tracking-kpi-body">
                        <span>フォローしている日数</span>
                        <strong>{creatorTrackingDashboard.trackingDays.toLocaleString('ja-JP')}<em>日</em></strong>
                        <small class:gold={getCreatorTrackingRankTier(creatorTrackingDashboard.trackingDaysRank, creatorTrackingDashboard.creatorCount) === 'gold'} class:silver={getCreatorTrackingRankTier(creatorTrackingDashboard.trackingDaysRank, creatorTrackingDashboard.creatorCount) === 'silver'} class:bronze={getCreatorTrackingRankTier(creatorTrackingDashboard.trackingDaysRank, creatorTrackingDashboard.creatorCount) === 'bronze'}>
                          {#if getCreatorTrackingRankTier(creatorTrackingDashboard.trackingDaysRank, creatorTrackingDashboard.creatorCount)}<Trophy size={13} />{/if}{formatCreatorTrackingRank(creatorTrackingDashboard.trackingDaysRank, creatorTrackingDashboard.creatorCount)}
                        </small>
                      </div>
                    </article>
                    <article class="creator-tracking-kpi">
                      <div class="creator-tracking-kpi-icon spend"><Coins size={19} /></div>
                      <div class="creator-tracking-kpi-body">
                        <span>総課金額</span>
                        <strong>{formatCreatorTrackingSpendAmount(creatorTrackingDashboard.totalSpend, creatorTrackingDashboard.currency)}<em>{creatorTrackingDashboard.currency}</em></strong>
                        <small class:gold={getCreatorTrackingRankTier(creatorTrackingDashboard.totalSpendRank, creatorTrackingDashboard.creatorCount) === 'gold'} class:silver={getCreatorTrackingRankTier(creatorTrackingDashboard.totalSpendRank, creatorTrackingDashboard.creatorCount) === 'silver'} class:bronze={getCreatorTrackingRankTier(creatorTrackingDashboard.totalSpendRank, creatorTrackingDashboard.creatorCount) === 'bronze'}>
                          {#if getCreatorTrackingRankTier(creatorTrackingDashboard.totalSpendRank, creatorTrackingDashboard.creatorCount)}<Trophy size={13} />{/if}{formatCreatorTrackingRank(creatorTrackingDashboard.totalSpendRank, creatorTrackingDashboard.creatorCount)}
                        </small>
                        {#if creatorTrackingDashboard.hasMissingExchangeRates}<em class="creator-tracking-exchange-warning">一部レート未取得</em>{/if}
                      </div>
                    </article>
                    <article class="creator-tracking-kpi">
                      <div class="creator-tracking-kpi-icon recent"><Coins size={19} /></div>
                      <div class="creator-tracking-kpi-body">
                        <span>直近3か月の課金額</span>
                        <strong>{formatCreatorTrackingSpendAmount(creatorTrackingDashboard.recentThreeMonthSpend, creatorTrackingDashboard.currency)}<em>{creatorTrackingDashboard.currency}</em></strong>
                        <small class:gold={getCreatorTrackingRankTier(creatorTrackingDashboard.recentThreeMonthSpendRank, creatorTrackingDashboard.creatorCount) === 'gold'} class:silver={getCreatorTrackingRankTier(creatorTrackingDashboard.recentThreeMonthSpendRank, creatorTrackingDashboard.creatorCount) === 'silver'} class:bronze={getCreatorTrackingRankTier(creatorTrackingDashboard.recentThreeMonthSpendRank, creatorTrackingDashboard.creatorCount) === 'bronze'}>
                          {#if getCreatorTrackingRankTier(creatorTrackingDashboard.recentThreeMonthSpendRank, creatorTrackingDashboard.creatorCount)}<Trophy size={13} />{/if}{formatCreatorTrackingRank(creatorTrackingDashboard.recentThreeMonthSpendRank, creatorTrackingDashboard.creatorCount)}
                        </small>
                      </div>
                    </article>
                  </div>

                  <div class="creator-tracking-composition-charts">
                    <span class="creator-tracking-composition-title">Content Composition</span>
                    <article class="creator-tracking-composition-card">
                      {#if titleComposition.length === 0}
                        <div class="creator-tracking-composition-empty">Titleデータなし</div>
                      {:else}
                        <div class="creator-tracking-composition-stage">
                          <div
                            class="creator-tracking-composition-pie"
                            role="img"
                            aria-label={getCreatorTrackingCompositionAriaLabel('Title数の構成比', titleComposition)}
                          >
                            <svg viewBox="0 0 100 100" aria-hidden="true">
                              <circle class="creator-tracking-composition-track" cx="50" cy="50" r="37" pathLength="100" />
                              {#each titleCompositionSegments as segment}
                                <circle
                                  class="creator-tracking-composition-segment"
                                  cx="50"
                                  cy="50"
                                  r="37"
                                  pathLength="100"
                                  stroke={getCreatorTrackingCompositionColor(segment.slice, segment.index)}
                                  stroke-dasharray={`${segment.lengthPercent} ${100 - segment.lengthPercent}`}
                                  stroke-dashoffset={-segment.startPercent}
                                />
                              {/each}
                            </svg>
                            <strong>TITLE</strong>
                          </div>
                          {#each titleComposition as slice, index}
                            {@const labelPosition = getCreatorTrackingCompositionLabelPosition(titleComposition, index)}
                            <div
                              class="creator-tracking-composition-label"
                              class:overlaps-arc={labelPosition.overlapsArc}
                              title={`${slice.label}: ${slice.count.toLocaleString('ja-JP')}件 (${formatCreatorTrackingCompositionPercentage(slice, titleComposition)})`}
                              style={`left: ${labelPosition.x}%; top: ${labelPosition.y}%; color: ${getCreatorTrackingCompositionColor(slice, index)}`}
                            >
                              <span class:core={slice.isCore}>{slice.label}</span>
                              <em>{formatCreatorTrackingCompositionPercentage(slice, titleComposition)}</em>
                            </div>
                          {/each}
                        </div>
                      {/if}
                    </article>

                    <article class="creator-tracking-composition-card">
                      {#if tagComposition.length === 0}
                        <div class="creator-tracking-composition-empty">Tagデータなし</div>
                      {:else}
                        <div class="creator-tracking-composition-stage">
                          <div
                            class="creator-tracking-composition-pie"
                            role="img"
                            aria-label={getCreatorTrackingCompositionAriaLabel('Tag数の構成比', tagComposition)}
                          >
                            <svg viewBox="0 0 100 100" aria-hidden="true">
                              <circle class="creator-tracking-composition-track" cx="50" cy="50" r="37" pathLength="100" />
                              {#each tagCompositionSegments as segment}
                                <circle
                                  class="creator-tracking-composition-segment"
                                  cx="50"
                                  cy="50"
                                  r="37"
                                  pathLength="100"
                                  stroke={getCreatorTrackingCompositionColor(segment.slice, segment.index)}
                                  stroke-dasharray={`${segment.lengthPercent} ${100 - segment.lengthPercent}`}
                                  stroke-dashoffset={-segment.startPercent}
                                />
                              {/each}
                            </svg>
                            <strong>TAG</strong>
                          </div>
                          {#each tagComposition as slice, index}
                            {@const labelPosition = getCreatorTrackingCompositionLabelPosition(tagComposition, index)}
                            <div
                              class="creator-tracking-composition-label"
                              class:overlaps-arc={labelPosition.overlapsArc}
                              title={`${slice.label}: ${slice.count.toLocaleString('ja-JP')}件 (${formatCreatorTrackingCompositionPercentage(slice, tagComposition)})`}
                              style={`left: ${labelPosition.x}%; top: ${labelPosition.y}%; color: ${getCreatorTrackingCompositionColor(slice, index)}`}
                            >
                              <span class:core={slice.isCore}>{slice.label}</span>
                              <em>{formatCreatorTrackingCompositionPercentage(slice, tagComposition)}</em>
                            </div>
                          {/each}
                        </div>
                      {/if}
                    </article>
                  </div>

                  <div class="creator-tracking-archive-chart">
                    <div class="creator-tracking-archive-chart-header">
                      <div><span>ARCHIVE HISTORY</span><h3>書庫のファイル数・画像枚数</h3></div>
                      <div class="creator-tracking-archive-controls">
                        <div><button type="button" class:active={creatorTrackingArchiveScale === 'year'} aria-pressed={creatorTrackingArchiveScale === 'year'} onclick={() => setCreatorTrackingArchiveScale('year')}>年</button><button type="button" class:active={creatorTrackingArchiveScale === 'month'} aria-pressed={creatorTrackingArchiveScale === 'month'} onclick={() => setCreatorTrackingArchiveScale('month')}>月</button><button type="button" class:active={creatorTrackingArchiveScale === 'week'} aria-pressed={creatorTrackingArchiveScale === 'week'} onclick={() => setCreatorTrackingArchiveScale('week')}>週</button></div>
                      </div>
                    </div>
                    <div class="creator-tracking-archive-legend"><span class="files"><i></i>総ファイル数</span><span class="images"><i></i>総枚数</span></div>
                    {#if archiveSeries.length === 0}
                      <div class="creator-tracking-archive-empty">書庫スナップショットを蓄積すると推移を表示します</div>
                    {:else}
                      <div class="creator-tracking-archive-plot">
                        <div class="creator-tracking-archive-scale files">
                          <span>{getCreatorTrackingArchiveFileMaximum(creatorTrackingDashboard, creatorTrackingArchiveScale).toLocaleString('ja-JP')}</span>
                          <span>0</span>
                        </div>
                        <div class="creator-tracking-archive-scroll" bind:this={creatorTrackingArchiveScrollElement}>
                          <div class="creator-tracking-archive-canvas" style={`--archive-count: ${archiveSeries.length}`}>
                            <div class="creator-tracking-archive-grid-lines"><i></i><i></i><i></i></div>
                            <div class="creator-tracking-archive-bars">
                              {#each archiveSeries as point}
                                <div class="creator-tracking-archive-period" title={`${point.label} / ファイル: ${point.fileCount.toLocaleString('ja-JP')} / 画像: ${point.imageCount.toLocaleString('ja-JP')}`}>
                                  <i class="file-bar" style={`height: ${getCreatorTrackingArchiveBarHeight(point.fileCount, creatorTrackingDashboard, creatorTrackingArchiveScale)}%`}></i>
                                  <span>{point.label}</span>
                                </div>
                              {/each}
                            </div>
                            <svg class="creator-tracking-archive-line" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
                              <polyline points={getCreatorTrackingArchiveLinePoints(creatorTrackingDashboard, creatorTrackingArchiveScale)} />
                              {#each archiveSeries as point, index}
                                <circle cx={(index + 0.5) / Math.max(1, archiveSeries.length) * 100} cy={100 - point.imageCount / getCreatorTrackingArchiveImageMaximum(creatorTrackingDashboard, creatorTrackingArchiveScale) * 100} r="1.25" />
                              {/each}
                            </svg>
                          </div>
                        </div>
                        <div class="creator-tracking-archive-scale images">
                          <span>{getCreatorTrackingArchiveImageMaximum(creatorTrackingDashboard, creatorTrackingArchiveScale).toLocaleString('ja-JP')}</span>
                          <span>0</span>
                        </div>
                      </div>
                    {/if}
                  </div>
                </div>
              </section>
              {/if}

            <div class="creator-tracking-grid">
              <section class="creator-tracking-panel">
                <header>
                  <div class="creator-tracking-panel-icon"><UserRound size={18} /></div>
                  <div><h2>作者基本情報とフォローの概要</h2><p>現在の活動状態と、自分側の確認進捗</p></div>
                </header>
                <div class="creator-tracking-fields two-columns">
                  <label class="creator-tracking-field">
                    <span>表示名</span>
                    <input bind:value={creatorTracking.displayName} oninput={markCreatorTrackingDirty} placeholder={creatorTracking.creator} />
                  </label>
                  <label class="creator-tracking-field">
                    <span>別名義</span>
                    <input bind:value={creatorTracking.alternateName} oninput={markCreatorTrackingDirty} placeholder="別名義を入力" />
                  </label>
                  <label class="creator-tracking-field">
                    <span>活動状況</span>
                    <select bind:value={creatorTracking.activityStatus} onchange={markCreatorTrackingDirty}>
                      <option>不明</option>
                      <option>活動中</option>
                      <option>更新低下</option>
                      <option>休止中</option>
                      <option>活動終了</option>
                    </select>
                  </label>
                  <label class="creator-tracking-field">
                    <span>フォロー方針</span>
                    <select data-i18n-skip bind:value={creatorTracking.trackingStatus} onchange={markCreatorTrackingDirty}>
                      {#if creatorTracking.trackingStatus && !creatorTrackingSettingsDraft.followPolicyOptions.includes(creatorTracking.trackingStatus)}<option value={creatorTracking.trackingStatus}>{creatorTracking.trackingStatus}</option>{/if}
                      {#each creatorTrackingSettingsDraft.followPolicyOptions as option}<option value={option}>{option}</option>{/each}
                    </select>
                  </label>
                  <label class="creator-tracking-field">
                    <span>起票日</span>
                    <input type="date" bind:value={creatorTracking.lastCheckedOn} oninput={markCreatorTrackingDirty} />
                  </label>
                  <label class="creator-tracking-field">
                    <span>最終確認日</span>
                    <span class="creator-tracking-date-with-action">
                      <input type="date" bind:value={creatorTracking.lastActivityOn} oninput={markCreatorTrackingDirty} />
                      <button type="button" class="creator-tracking-date-today" title="最終確認日に今日の日付を設定" aria-label="最終確認日に今日の日付を設定" onclick={setCreatorTrackingLastCheckToToday}>
                        <CalendarCheck size={17} />
                      </button>
                    </span>
                  </label>
                  <label class="creator-tracking-field full-width">
                    <span>User's memo</span>
                    <textarea rows="3" bind:value={creatorTracking.activitySummary} oninput={markCreatorTrackingDirty} placeholder="作者に関するメモを入力"></textarea>
                  </label>
                </div>
              </section>

              <section class="creator-tracking-panel">
                <header>
                  <div class="creator-tracking-panel-icon"><Star size={18} /></div>
                  <div><h2>作品の傾向</h2><p>作品傾向に対する自分の評価</p></div>
                </header>
                <div class="creator-tracking-rating" aria-label={`総合評価 ${creatorTracking.personalRating.toFixed(1)} / 5`}>
                  <div class="creator-tracking-stars" aria-hidden="true">
                    {#each [0, 1, 2, 3, 4] as starIndex}
                      <span class="creator-tracking-star-meter">
                        <span class="creator-tracking-star-base">★</span>
                        <span class="creator-tracking-star-fill" style={`width: ${getCreatorTrackingStarFill(starIndex)}%`}>★</span>
                      </span>
                    {/each}
                  </div>
                  <strong>{creatorTracking.personalRating.toFixed(1)} <span>/ 5</span></strong>
                </div>
                <div class="creator-tracking-evaluation-layout">
                  <div class="creator-tracking-radar-card">
                    <svg class="creator-tracking-radar" viewBox="0 0 360 270" role="img" aria-label="6指標のレーダーチャート">
                      <title>Creator Trackingの6指標評価</title>
                      {#each [1, 2, 3, 4] as level}
                        <polygon class="creator-tracking-radar-grid" points={getCreatorTrackingRadarPolygon(level)}></polygon>
                      {/each}
                      {#each creatorTrackingSettingsDraft.metrics as metric, index}
                        {@const axisPoint = getCreatorTrackingRadarPoint(index, 4)}
                        {@const labelPoint = getCreatorTrackingRadarPoint(index, 4, 111)}
                        <line class="creator-tracking-radar-axis" x1="180" y1="137" x2={axisPoint.x} y2={axisPoint.y}></line>
                        <text
                          class="creator-tracking-radar-label"
                          x={labelPoint.x}
                          y={labelPoint.y + (index === 0 ? -3 : index === 3 ? 10 : 4)}
                          text-anchor={getCreatorTrackingRadarLabelAnchor(labelPoint.x)}>{metric.label}</text>
                      {/each}
                      <polygon class="creator-tracking-radar-value" points={getCreatorTrackingRadarValuePolygon(creatorTracking.evaluationMetrics)}></polygon>
                      {#each creatorTrackingSettingsDraft.metrics as metric, index}
                        {@const valuePoint = getCreatorTrackingRadarPoint(index, creatorTracking.evaluationMetrics[metric.key])}
                        <circle class="creator-tracking-radar-point" cx={valuePoint.x} cy={valuePoint.y} r="4"></circle>
                      {/each}
                    </svg>
                  </div>
                  <div class="creator-tracking-metric-controls">
                    {#each creatorTrackingSettingsDraft.metrics as metric}
                      <div class="creator-tracking-metric-row">
                        <div><span>{metric.label}</span></div>
                        <div class="creator-tracking-metric-values" aria-label={`${metric.label}の評価`}>
                          {#each [1, 2, 3, 4] as value}
                            <button
                              type="button"
                              class:active={creatorTracking.evaluationMetrics[metric.key] === value}
                              aria-pressed={creatorTracking.evaluationMetrics[metric.key] === value}
                              onclick={() => setCreatorTrackingMetric(metric.key, value)}>{value}</button>
                          {/each}
                        </div>
                      </div>
                    {/each}
                  </div>
                </div>
              </section>

              <section class="creator-tracking-panel full-span">
                <header class="creator-tracking-panel-actions">
                  <div class="creator-tracking-panel-title">
                    <div class="creator-tracking-panel-icon"><ExternalLink size={18} /></div>
                    <div><h2>活動場所</h2><p>Pixiv、支援サイト、Discordなどの確認先</p></div>
                  </div>
                  <button onclick={addCreatorTrackingActivityLink}><Plus size={15} /> 活動場所を追加</button>
                </header>
                {#if creatorTracking.activityLinks.length === 0}
                  <button class="creator-tracking-link-empty" onclick={addCreatorTrackingActivityLink}>活動場所を登録する</button>
                {:else}
                  <div class="creator-tracking-links">
                    <div class="creator-tracking-link-head"><span></span><span>名称</span><span></span><span>URL / 場所</span><span>フォローアップ</span><span>日数</span><span>メモ</span><span></span></div>
                    {#each creatorTracking.activityLinks as link, index}
                      {@const siteIcon = getCreatorTrackingActivityIcon(link.label)}
                      <div
                        class:dragging={draggedCreatorTrackingActivityLinkIndex === index}
                        class="creator-tracking-link-row"
                        role="group"
                        ondragover={(event) => { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'; }}
                        ondrop={(event) => reorderCreatorTrackingActivityLink(event, index)}
                      >
                        <button type="button" class="creator-tracking-link-drag-handle" draggable="true" title="ドラッグして活動場所を並べ替え" aria-label={`${link.label || index + 1}をドラッグして並べ替え`} ondragstart={(event) => startCreatorTrackingActivityLinkDrag(event, index)} ondragend={() => draggedCreatorTrackingActivityLinkIndex = null}>
                          {#if siteIcon}<img src={siteIcon} alt={link.label} />{:else}<ExternalLink size={16} />{/if}
                        </button>
                        <select aria-label={`活動場所${index + 1}の名称`} value={link.label} onchange={(event) => updateCreatorTrackingActivityLink(index, { label: event.currentTarget.value })}>
                          {#if link.label && !creatorTrackingSettingsDraft.activityPlaces.some(place => place.label === link.label)}<option value={link.label}>{link.label}</option>{/if}
                          {#each creatorTrackingSettingsDraft.activityPlaces as place}<option value={place.label}>{place.label}</option>{/each}
                        </select>
                        <button class="creator-tracking-link-open" title="既定のブラウザで開く" disabled={!/^https?:\/\//i.test(link.url.trim())} onclick={() => openCreatorTrackingActivityLink(link.url)}><Link size={16} /></button>
                        <input aria-label={`活動場所${index + 1}のURL`} value={link.url} oninput={(event) => updateCreatorTrackingActivityLink(index, { url: event.currentTarget.value })} placeholder={getCreatorTrackingActivityPlaceholder(link.label)} />
                        <button class="creator-tracking-follow-toggle" class:active={link.followUpEnabled} aria-pressed={link.followUpEnabled} onclick={() => updateCreatorTrackingActivityLink(index, { followUpEnabled: !link.followUpEnabled })}><span></span>{link.followUpEnabled ? 'ON' : 'OFF'}</button>
                        <input aria-label={`活動場所${index + 1}のフォローアップ日数`} type="number" min="1" max="99" step="1" value={link.followUpDays} disabled={!link.followUpEnabled} oninput={(event) => updateCreatorTrackingActivityLink(index, { followUpDays: Math.max(1, Math.min(99, Math.round(Number(event.currentTarget.value) || 1))) })} />
                        <input aria-label={`活動場所${index + 1}のメモ`} value={link.note} oninput={(event) => updateCreatorTrackingActivityLink(index, { note: event.currentTarget.value })} placeholder="用途・確認頻度" />
                        <button class="creator-tracking-remove-link" title="活動場所を削除" onclick={() => removeCreatorTrackingActivityLink(index)}><Trash2 size={15} /></button>
                      </div>
                    {/each}
                  </div>
                {/if}
              </section>

              <section class="creator-tracking-panel full-span">
                <header class="creator-tracking-panel-actions">
                  <div class="creator-tracking-panel-title">
                    <div class="creator-tracking-panel-icon"><Folder size={18} /></div>
                    <div><h2>ストレージ</h2><p>Gallery・Stockroom・Temporaryの保管場所</p></div>
                  </div>
                  <div class="creator-tracking-storage-actions">
                    <button
                      class:active={canOpenCreatorTrackingStorageSplit()}
                      title={canOpenCreatorTrackingStorageSplit() ? 'GalleryとStockroomをExplorerで分割表示' : 'GalleryとStockroomの行が両方あると使用できます'}
                      aria-label="GalleryとStockroomをExplorerで分割表示"
                      disabled={!canOpenCreatorTrackingStorageSplit()}
                      onclick={openCreatorTrackingStorageSplit}
                    ><Columns2 size={17} /></button>
                    <button onclick={addCreatorTrackingStorageLocation}><Plus size={15} /> 活動場所を追加</button>
                  </div>
                </header>
                <div class="creator-tracking-storage-list">
                  <div class="creator-tracking-storage-head"><span>用途</span><span></span><span>フォルダパス</span><span></span></div>
                  {#each creatorTracking.storageLocations as location (location.id)}
                    <div class="creator-tracking-storage-row">
                      <select aria-label="ストレージの用途" value={location.usage} onchange={(event) => updateCreatorTrackingStorageLocation(location.id, { usage: event.currentTarget.value as CreatorTrackingStorageUsage })}>
                        {#each getCreatorTrackingStorageUsageOptions(location.id) as usage}
                          <option value={usage}>{usage}</option>
                        {/each}
                      </select>
                      <button class="creator-tracking-storage-open" title={`${location.usage}をExplorerで開く`} disabled={!location.path.trim()} onclick={() => openCreatorTrackingStorageLocation(location)}><FolderOpen size={17} /></button>
                      <input aria-label={`${location.usage}のフォルダパス`} value={location.path} placeholder="D:\Gallery" oninput={(event) => updateCreatorTrackingStorageLocation(location.id, { path: event.currentTarget.value })} />
                      <button
                        class="creator-tracking-storage-delete"
                        class:protected={location.usage !== 'Temporary'}
                        title={location.usage === 'Temporary' ? '行を削除' : `${location.usage}は必須用途のため削除できません`}
                        aria-disabled={location.usage !== 'Temporary'}
                        onclick={() => removeCreatorTrackingStorageLocation(location)}
                      ><Trash2 size={16} /></button>
                    </div>
                  {/each}
                </div>
              </section>

              <section class="creator-tracking-panel full-span">
                <header class="creator-tracking-panel-actions">
                  <div class="creator-tracking-panel-title">
                    <div class="creator-tracking-panel-icon"><Archive size={18} /></div>
                    <div><h2>課金・購入記録</h2><p>サブスクと買い切り購入を履歴単位で管理します</p></div>
                  </div>
                  <div class="creator-tracking-billing-actions">
                    <div class="filter-editor-view-tabs creator-tracking-billing-tabs" role="tablist" aria-label="課金履歴の種類">
                      <button class:active={creatorTrackingBillingView === 'subscriptions'} role="tab" aria-selected={creatorTrackingBillingView === 'subscriptions'} onclick={() => creatorTrackingBillingView = 'subscriptions'}>サブスク</button>
                      <button class:active={creatorTrackingBillingView === 'purchases'} role="tab" aria-selected={creatorTrackingBillingView === 'purchases'} onclick={() => creatorTrackingBillingView = 'purchases'}>購入</button>
                    </div>
                    {#if creatorTrackingBillingView === 'subscriptions'}
                      <button class="creator-tracking-billing-add" onclick={addCreatorTrackingSubscription}><Plus size={15} /> サブスク歴を追加</button>
                    {:else}
                      <button class="creator-tracking-billing-add" onclick={addCreatorTrackingPurchase}><Plus size={15} /> 購入歴を追加</button>
                    {/if}
                  </div>
                </header>

                {#if creatorTrackingBillingView === 'subscriptions'}
                  {#if creatorTracking.subscriptionHistory.length === 0}
                    <button class="creator-tracking-history-empty" onclick={addCreatorTrackingSubscription}>サブスク歴を追加してください</button>
                  {:else}
                    <div class="creator-tracking-history-table">
                      <div class="creator-tracking-history-head creator-tracking-subscription-grid">
                        <span></span><span>課金プラットフォーム</span><span>対象プラン（任意）</span><span>通貨</span><span>支払い額</span><span>支払い頻度</span><span>開始日</span><span>更新予定日</span><span>Wishlist</span><span>有効</span><span>終了予定</span><span class="creator-tracking-alert-heading"><AlarmClock size={13} />アラート</span><span></span>
                      </div>
                      {#each creatorTracking.subscriptionHistory as subscription, index (subscription.id)}
                        {@const platformIcon = getCreatorTrackingActivityIcon(subscription.platform)}
                        <div
                          class:dragging={draggedCreatorTrackingBillingRow?.view === 'subscriptions' && draggedCreatorTrackingBillingRow.index === index}
                          class="creator-tracking-history-row creator-tracking-subscription-grid"
                          role="group"
                          ondragover={(event) => { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'; }}
                          ondrop={(event) => reorderCreatorTrackingBillingRow(event, 'subscriptions', index)}
                        >
                          <button type="button" class="creator-tracking-billing-site-icon" draggable="true" title="ドラッグしてサブスク歴を並べ替え" aria-label={`${subscription.platform || index + 1}をドラッグして並べ替え`} ondragstart={(event) => startCreatorTrackingBillingRowDrag(event, 'subscriptions', index)} ondragend={finishCreatorTrackingBillingRowDrag}>{#if platformIcon}<img src={platformIcon} alt={subscription.platform} />{:else}<GripVertical size={16} />{/if}</button>
                          <select aria-label={`サブスク${index + 1}の課金プラットフォーム`} value={subscription.platform} onchange={(event) => updateCreatorTrackingSubscription(index, { platform: event.currentTarget.value })}>
                            <option value="">未設定</option>
                            {#if subscription.platform && !creatorTrackingSettingsDraft.activityPlaces.some((place) => place.label === subscription.platform)}
                              <option value={subscription.platform}>{subscription.platform}</option>
                            {/if}
                            {#each creatorTrackingSettingsDraft.activityPlaces as place}
                              <option value={place.label}>{place.label}</option>
                            {/each}
                          </select>
                          <input aria-label={`サブスク${index + 1}の対象プラン`} value={subscription.plan} oninput={(event) => updateCreatorTrackingSubscription(index, { plan: event.currentTarget.value })} placeholder="プラン名" />
                          <select aria-label={`サブスク${index + 1}の通貨`} value={subscription.currency} onchange={(event) => updateCreatorTrackingSubscription(index, { currency: event.currentTarget.value })}>
                            <option>JPY</option><option>USD</option><option>EUR</option><option>CNY</option><option>KRW</option>
                          </select>
                          <input aria-label={`サブスク${index + 1}の支払い額`} type="number" min="0" step="1" value={subscription.amount} oninput={(event) => updateCreatorTrackingSubscription(index, { amount: Number(event.currentTarget.value) || 0 })} />
                          <select aria-label={`サブスク${index + 1}の支払い頻度`} value={subscription.billingFrequency} onchange={(event) => updateCreatorTrackingSubscriptionSchedule(index, 'billingFrequency', event.currentTarget.value)}>
                            <option value="monthly">毎月</option><option value="quarterly">3か月ごと</option><option value="semiannual">6か月ごと</option><option value="annual">毎年</option>
                          </select>
                          <input aria-label={`サブスク${index + 1}の開始日`} type="date" value={subscription.startedOn} disabled={subscription.wishlist} onchange={(event) => updateCreatorTrackingSubscriptionSchedule(index, 'startedOn', event.currentTarget.value)} />
                          <input aria-label={`サブスク${index + 1}の更新予定日`} type="date" value={subscription.renewalOn} disabled={subscription.wishlist} oninput={(event) => updateCreatorTrackingSubscription(index, { renewalOn: event.currentTarget.value })} />
                          <button class="creator-tracking-history-toggle" class:active={subscription.wishlist} aria-label={`サブスク${index + 1}のWishlist`} aria-pressed={subscription.wishlist} onclick={() => toggleCreatorTrackingSubscriptionWishlist(index)}><span></span>{subscription.wishlist ? 'ON' : 'OFF'}</button>
                          <button class="creator-tracking-history-toggle" class:active={subscription.isActive} aria-label={`サブスク${index + 1}の有効状態`} aria-pressed={subscription.isActive} onclick={() => toggleCreatorTrackingSubscriptionActive(index)}><span></span>{subscription.isActive ? 'ON' : 'OFF'}</button>
                          <button class="creator-tracking-history-toggle" class:active={subscription.endingPlanned} aria-pressed={subscription.endingPlanned} disabled={subscription.wishlist} onclick={() => updateCreatorTrackingSubscription(index, { endingPlanned: !subscription.endingPlanned })}><span></span>{subscription.endingPlanned ? 'ON' : 'OFF'}</button>
                          <button class="creator-tracking-history-toggle" class:active={subscription.reminder} aria-label={`サブスク${index + 1}のアラート`} aria-pressed={subscription.reminder} disabled={subscription.wishlist} onclick={() => updateCreatorTrackingSubscription(index, { reminder: !subscription.reminder })}><span></span>{subscription.reminder ? 'ON' : 'OFF'}</button>
                          <button class="creator-tracking-history-delete" title="サブスク歴を削除" onclick={() => removeCreatorTrackingSubscription(index)}><Trash2 size={15} /></button>
                        </div>
                      {/each}
                    </div>
                  {/if}
                {:else}
                  {#if creatorTracking.purchaseHistory.length === 0}
                    <button class="creator-tracking-history-empty" onclick={addCreatorTrackingPurchase}>購入歴を追加してください</button>
                  {:else}
                    <div class="creator-tracking-history-table">
                      <div class="creator-tracking-history-head creator-tracking-purchase-grid">
                        <span></span><span>購入プラットフォーム</span><span>商品名</span><span>通貨</span><span>金額</span><span>購入日</span><span>Wishlist</span><span></span>
                      </div>
                      {#each creatorTracking.purchaseHistory as purchase, index (purchase.id)}
                        {@const platformIcon = getCreatorTrackingActivityIcon(purchase.platform)}
                        <div
                          class:dragging={draggedCreatorTrackingBillingRow?.view === 'purchases' && draggedCreatorTrackingBillingRow.index === index}
                          class="creator-tracking-history-row creator-tracking-purchase-grid"
                          role="group"
                          ondragover={(event) => { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'; }}
                          ondrop={(event) => reorderCreatorTrackingBillingRow(event, 'purchases', index)}
                        >
                          <button type="button" class="creator-tracking-billing-site-icon" draggable="true" title="ドラッグして購入歴を並べ替え" aria-label={`${purchase.platform || index + 1}をドラッグして並べ替え`} ondragstart={(event) => startCreatorTrackingBillingRowDrag(event, 'purchases', index)} ondragend={finishCreatorTrackingBillingRowDrag}>{#if platformIcon}<img src={platformIcon} alt={purchase.platform} />{:else}<GripVertical size={16} />{/if}</button>
                          <select aria-label={`購入${index + 1}のプラットフォーム`} value={purchase.platform} onchange={(event) => updateCreatorTrackingPurchase(index, { platform: event.currentTarget.value })}>
                            <option value="">未設定</option>
                            {#if purchase.platform && !creatorTrackingSettingsDraft.activityPlaces.some((place) => place.label === purchase.platform)}
                              <option value={purchase.platform}>{purchase.platform}</option>
                            {/if}
                            {#each creatorTrackingSettingsDraft.activityPlaces as place}
                              <option value={place.label}>{place.label}</option>
                            {/each}
                          </select>
                          <input aria-label={`購入${index + 1}の商品名`} value={purchase.productName} oninput={(event) => updateCreatorTrackingPurchase(index, { productName: event.currentTarget.value })} placeholder="商品名" />
                          <select aria-label={`購入${index + 1}の通貨`} value={purchase.currency} onchange={(event) => updateCreatorTrackingPurchase(index, { currency: event.currentTarget.value })}>
                            <option>JPY</option><option>USD</option><option>EUR</option><option>CNY</option><option>KRW</option>
                          </select>
                          <input aria-label={`購入${index + 1}の金額`} type="number" min="0" step="1" value={purchase.amount} oninput={(event) => updateCreatorTrackingPurchase(index, { amount: Number(event.currentTarget.value) || 0 })} />
                          <input aria-label={`購入${index + 1}の購入日`} type="date" value={purchase.purchasedOn} disabled={purchase.wishlist} oninput={(event) => updateCreatorTrackingPurchase(index, { purchasedOn: event.currentTarget.value })} />
                          <button class="creator-tracking-history-toggle" class:active={purchase.wishlist} aria-label={`購入${index + 1}のWishlist`} aria-pressed={purchase.wishlist} onclick={() => toggleCreatorTrackingPurchaseWishlist(index)}><span></span>{purchase.wishlist ? 'ON' : 'OFF'}</button>
                          <button class="creator-tracking-history-delete" title="購入歴を削除" onclick={() => removeCreatorTrackingPurchase(index)}><Trash2 size={15} /></button>
                        </div>
                      {/each}
                    </div>
                  {/if}
                {/if}
              </section>
            </div>

              <footer class="creator-tracking-footer">
                <span>{creatorTracking.updatedAt ? `最終保存: ${creatorTracking.updatedAt}` : 'まだ保存されていません'}</span>
              </footer>
            </div>
          </div>
        {/if}
      </section>
    {:else if activeView === 'creators'}
      <section class="gallery-content gallery-creator-summary-content">
        <header class="gallery-filters gallery-creator-summary-filters">
          <div class="gallery-filter-row gallery-creator-summary-rating-row">
            <strong>Rating</strong>
            <div class="gallery-filter-buttons">
              {#each galleryCreatorRatingBuckets as rating}
                <button
                  class:active={galleryCreatorSummaryRatings.includes(rating.value)}
                  class:gallery-rating-premium={rating.value === '10plus'}
                  onclick={(event) => setGalleryCreatorSummaryRatingFilter(rating.value, event)}
                >{rating.label} {getGalleryCreatorRatingBucketCount(rating.value)}</button>
              {/each}
            </div>
            <div class="gallery-sort-groups">
              <div class="gallery-sorts gallery-creator-summary-sorts">
                <span>Sort</span>
                {#each galleryCreatorSummarySortDefinitions as sort}
                  {@const priority = galleryCreatorSummarySortPriority(galleryCreatorSummarySorts, sort.key)}
                  <button
                    class:gallery-filter-sort-priority-1={priority === 1}
                    class:gallery-filter-sort-priority-2={priority === 2}
                    class:gallery-filter-sort-priority-3={priority === 3}
                    aria-pressed={priority > 0}
                    title={sort.key === 'rating'
                      ? '左クリックで降順ソートに追加、右クリックで解除'
                      : '左クリックで追加・昇降順切替、右クリックで解除'}
                    onclick={() => toggleGalleryCreatorSummarySort(sort.key)}
                    oncontextmenu={(event) => removeGalleryCreatorSummarySort(event, sort.key)}
                  >{sort.label} {galleryCreatorSummarySortArrow(galleryCreatorSummarySorts, sort.key)}</button>
                {/each}
              </div>
            </div>
            <div class="gallery-filter-actions gallery-filter-reset-action">
              <button aria-label="Creatorsのフィルターをすべて解除" onclick={clearGalleryCreatorSummaryFilters}>Reset</button>
            </div>
          </div>

          <div class="gallery-filter-row gallery-creator-summary-site-row">
            <strong>Site</strong>
            <div class="gallery-filter-buttons gallery-creator-summary-site-buttons">
              {#each creatorTrackingSettingsDraft.activityPlaces as place}
                {#if place.label.trim()}
                  {@const siteCount = getGalleryCreatorSummarySiteCount(place.label)}
                  <button
                    class:active={galleryCreatorSummarySites.includes(place.label)}
                    title={`${place.label} ${siteCount} Creators`}
                    aria-label={`${place.label}で絞り込み（${siteCount} Creators）`}
                    onclick={(event) => setGalleryCreatorSummarySiteFilter(place.label, event)}
                  >
                    {#if place.iconDataUri}<img src={place.iconDataUri} alt="" />{:else}<ExternalLink size={16} />{/if}
                    <span>{siteCount}</span>
                  </button>
                {/if}
              {/each}
            </div>
          </div>

          {#if isGalleryFilterEnabled('core_title', galleryCreatorSummarySection)}
          <div class="gallery-filter-row">
            <strong>{getGalleryFilterLabel('core_title', galleryCreatorSummarySection)}</strong>
            <div class="gallery-filter-control">
              <div class:gallery-filter-buttons-collapsed={!galleryCreatorSummaryCoreTitlesExpanded} class="gallery-filter-buttons gallery-filter-buttons-expandable" onwheel={scrollGalleryFilterRows}>
                {#each galleryCreatorSummaryCoreTitleOptions as option}
                  <button
                    class:active={galleryCreatorSummaryCoreTitles.includes(option.value)}
                    onclick={(event) => setGalleryCreatorSummaryCoreTitleFilter(option.value, event)}
                  >{option.value} {option.count}</button>
                {/each}
              </div>
              <div class="gallery-filter-actions gallery-filter-expand-action">
                <button
                  class:gallery-filter-collapse={galleryCreatorSummaryCoreTitlesExpanded}
                  onclick={() => galleryCreatorSummaryCoreTitlesExpanded = !galleryCreatorSummaryCoreTitlesExpanded}
                >{galleryCreatorSummaryCoreTitlesExpanded ? 'Collapse' : 'Expand'}</button>
              </div>
            </div>
          </div>
          {/if}

          {#if isGalleryFilterEnabled('core_tags', galleryCreatorSummarySection)}
          <div class="gallery-filter-row">
            <strong>{getGalleryFilterLabel('core_tags', galleryCreatorSummarySection)}</strong>
            <div class="gallery-filter-control">
              <div class:gallery-filter-buttons-collapsed={!galleryCreatorSummaryCoreTagsExpanded} class="gallery-filter-buttons gallery-filter-buttons-expandable" onwheel={scrollGalleryFilterRows}>
                {#each galleryCreatorSummaryCoreTagOptions as option}
                  <button
                    class:active={galleryCreatorSummaryCoreTags.includes(option.value)}
                    onclick={(event) => setGalleryCreatorSummaryCoreTagFilter(option.value, event)}
                  >{option.value} {option.count}</button>
                {/each}
              </div>
              <div class="gallery-filter-actions gallery-filter-expand-action">
                <button
                  class:gallery-filter-collapse={galleryCreatorSummaryCoreTagsExpanded}
                  onclick={() => galleryCreatorSummaryCoreTagsExpanded = !galleryCreatorSummaryCoreTagsExpanded}
                >{galleryCreatorSummaryCoreTagsExpanded ? 'Collapse' : 'Expand'}</button>
              </div>
            </div>
          </div>
          {/if}

          {#if galleryCreatorSummaryMoreExpanded}
            <div class="gallery-filter-row gallery-creator-summary-more-row">
              <strong>{translateSystemText('総合評価', appLanguage)}</strong>
              <div class="gallery-filter-buttons">
                {#each [5, 4, 3, 2, 1] as score}
                  <button
                    class:active={galleryCreatorSummaryOverallRatings.includes(score)}
                    onclick={(event) => setGalleryCreatorSummaryOverallRatingFilter(score, event)}
                  >★{score} {getGalleryCreatorSummaryOverallRatingCount(score)}</button>
                {/each}
              </div>
            </div>
            {#each creatorTrackingSettingsDraft.metrics as metric}
              <div class="gallery-filter-row gallery-creator-summary-more-row">
                <strong title={metric.label}>{metric.label}</strong>
                <div class="gallery-filter-buttons">
                  {#each [4, 3, 2, 1] as score}
                    <button
                      class:active={galleryCreatorSummaryMetricScores[metric.key].includes(score)}
                      onclick={(event) => setGalleryCreatorSummaryMetricScoreFilter(metric.key, score, event)}
                    >
                      <span class="gallery-creator-summary-score-icon" aria-hidden="true">{score}</span>
                      <span>{getGalleryCreatorSummaryMetricScoreCount(metric.key, score)}</span>
                    </button>
                  {/each}
                </div>
              </div>
            {/each}
          {/if}

          <div class="gallery-filter-row gallery-creator-summary-search-row">
            <strong>Utilities</strong>
            <div class="gallery-creator-summary-search-control">
              <label class="gallery-creator-summary-search">
                <Search size={16} />
                <input bind:this={galleryCreatorSummarySearchInputElement} bind:value={galleryCreatorSummaryQuery} placeholder="Creatorを検索" />
              </label>
              <button class="gallery-creator-summary-refresh" title="SQLiteDBから再集計" disabled={galleryCreatorSummaryIsLoading} onclick={() => loadGalleryCreatorSummaries(true)}>
                <RefreshCw size={16} />
              </button>
              <button class="gallery-creator-summary-refresh sticky-note-launch-button" title="Creatorsに付箋を追加" onclick={createStickyNoteFromToolbar}>
                <StickyNote size={16} />
              </button>
              <button class="gallery-creator-summary-refresh view-bookmark-button" title="現在のCreatorsをBookmark" onclick={captureViewBookmarkFromToolbar}>
                <Bookmark size={16} />
              </button>
              <button
                class:active={galleryCreatorSummaryReminderFilter === 'warning'}
                class="gallery-creator-summary-refresh gallery-creator-summary-reminder warning"
                title="フォローアップ警告中のCreatorに絞り込む"
                aria-label="フォローアップ警告中のCreatorに絞り込む"
                aria-pressed={galleryCreatorSummaryReminderFilter === 'warning'}
                onclick={() => toggleGalleryCreatorSummaryReminderFilter('warning')}>
                <TriangleAlert size={16} />
              </button>
              <button
                class:active={galleryCreatorSummaryReminderFilter === 'alert'}
                class="gallery-creator-summary-refresh gallery-creator-summary-reminder alert"
                title="フォローアップアラート中のCreatorに絞り込む"
                aria-label="フォローアップアラート中のCreatorに絞り込む"
                aria-pressed={galleryCreatorSummaryReminderFilter === 'alert'}
                onclick={() => toggleGalleryCreatorSummaryReminderFilter('alert')}>
                <OctagonAlert size={16} />
              </button>
              <button
                class:active={galleryCreatorSummaryMoreExpanded}
                class="gallery-creator-summary-more-toggle"
                aria-expanded={galleryCreatorSummaryMoreExpanded}
                onclick={() => galleryCreatorSummaryMoreExpanded = !galleryCreatorSummaryMoreExpanded}
              >More {#if galleryCreatorSummaryMoreExpanded}<ChevronUp size={15} />{:else}<ChevronDown size={15} />{/if}</button>
            </div>
          </div>
        </header>

        {#if galleryCreatorSummaryError}
          <div class="empty">{galleryCreatorSummaryError}</div>
        {:else if galleryCreatorSummaryIsLoading && galleryCreatorSummaries.length === 0}
          <div class="empty">Creatorsを集計しています...</div>
        {:else if visibleGalleryCreatorSummaries.length === 0}
          <div class="empty">条件に一致するCreatorがありません。</div>
        {:else}
          <div
            class:gallery-work-grid-landscape={galleryCreatorSummaryCardAspect === 'landscape'}
            class="gallery-work-grid gallery-creator-summary-grid"
            style={galleryCreatorSummaryGridStyle}
          >
            {#each visibleGalleryCreatorSummaries as item (item.id)}
              <article
                class:gallery-work-card-landscape={galleryCreatorSummaryCardAspect === 'landscape'}
                class="gallery-work-card gallery-creator-summary-card"
                title="左クリックでGalleryをCreator絞り込み表示 / 右クリックでCreator Trackingを開く"
                oncontextmenu={(event) => openCreatorTracking(event, item)}
              >
                <button class="gallery-work-open" use:observeGalleryCreatorSummaryThumbnail={item} onclick={() => openGalleryCreatorSummary(item)}>
                  <div class="gallery-work-thumb gallery-creator-summary-thumb">
                    {#if galleryThumbnails[item.id]}
                      <img src={galleryThumbnails[item.id]} alt="" loading="lazy" />
                    {:else}
                      <UserRound size={38} />
                    {/if}
                    <span class="gallery-creator-summary-rating"><span>★{item.totalRating.toLocaleString('ja-JP')}</span></span>
                  </div>
                  <div class="gallery-work-body gallery-creator-summary-body">
                    <h2 title={item.creator}>{item.creator}</h2>
                    <p class="gallery-work-meta">Files {item.fileCount.toLocaleString('ja-JP')} / Avg. {item.averageImageCount.toLocaleString('ja-JP')}p</p>
                    <p class="gallery-creator-summary-updated">{formatGalleryDate(item.trackingLastActivityOn)}</p>
                    <p class="gallery-creator-summary-core" title={item.coreTitles.join(' / ')}>{item.coreTitles.join(' / ') || '-'}</p>
                    <p class="gallery-creator-summary-core gallery-creator-summary-core-tags" title={item.coreTags.join(' / ')}>{item.coreTags.join(' / ') || '-'}</p>
                  </div>
                </button>
              </article>
            {/each}
          </div>
        {/if}
      </section>
    {:else if activeView === 'explorer'}
      <section class="content explorer-content">
        <div class="explorer-tabs" aria-label="フォルダタブ">
          {#each explorerTabs as tab}
            <div
              class:tab-active={tab.id === activeExplorerTabId}
              class:tab-split={tab.id === explorerSplit?.rightTabId}
              class:tab-focused={tab.id === (splitFocusedPane === 'right' ? explorerSplit?.rightTabId : activeExplorerTabId)}
              class="explorer-tab"
              role="group"
              draggable="true"
              ondragstart={(event) => {
                draggedExplorerTab = tab;
                event.dataTransfer?.setData('text/plain', tab.path);
                if (event.dataTransfer) {
                  event.dataTransfer.effectAllowed = 'move';
                }
              }}
              ondragend={() => (draggedExplorerTab = null)}
              ondragover={(event) => event.preventDefault()}
              ondrop={(event) => reorderExplorerTab(event, tab)}
              onauxclick={(event) => closeExplorerTabWithMiddleClick(event, tab)}
              oncontextmenu={(event) => openExplorerTabContextMenu(event, tab)}
            >
              <button class="explorer-tab-label" title={tab.path} onclick={() => selectExplorerTab(tab)}>
                {tab.label}
              </button>
              <button class="explorer-tab-close" title="タブを閉じる" onclick={() => closeExplorerTab(tab)}>
                <X size={14} />
              </button>
            </div>
          {/each}
          <div class="explorer-tab-add-menu" role="presentation" onpointerdown={(event) => event.stopPropagation()}>
            <button
              class="explorer-tab-add"
              title="左クリックでタブを複製 / 右クリックで候補を表示"
              onclick={duplicateActiveExplorerTab}
              onpointerdown={handleNewExplorerTabPointerDown}
              oncontextmenu={openNewExplorerTabMenu}
            >
              <Plus size={17} />
            </button>
            {#if explorerNewTabMenuOpen}
              <div class="new-tab-candidate-menu" role="menu" aria-label="新規タブ候補">
                {#if newTabCandidates.length === 0}
                  <div class="new-tab-candidate-empty">候補が登録されていません</div>
                {:else}
                  {#each newTabCandidates as candidate}
                    {#if candidate.kind === 'separator'}
                      <div class="new-tab-menu-separator" role="separator"></div>
                    {:else}
                      <button role="menuitem" title={candidate.path} onclick={() => openNewExplorerTabFromCandidate(candidate)}>
                        <FolderOpen size={16} />
                        <span>{candidate.label}</span>
                      </button>
                    {/if}
                  {/each}
                {/if}
              </div>
            {/if}
          </div>
          {#if explorerSplit}
            <button class="explorer-tab-add" title="分割表示を終了" onclick={exitExplorerSplit}>
              <Columns2 size={17} />
            </button>
          {/if}
        </div>

        {#if explorerSplit}
          <div class="explorer-split-workspace" aria-label="分割表示">
            <section
              class:split-pane-active={splitFocusedPane === 'left'}
              class="split-pane-left explorer-split-pane"
              role="group"
              aria-label="左側の分割ペイン"
              onpointerdown={(event) => {
                focusSplitPane('left');
                beginExplorerGesture(event);
              }}
              onpointermove={updateExplorerGesture}
              onpointerup={finishExplorerGesture}
              oncontextmenu={(event) => openExplorerBlankContextMenu(event, 'left')}
            >
              <header class="split-pane-heading">
                <div>
                  <strong>{splitLeftTab?.label ?? explorerPath}</strong>
                  <span title={explorerPath}>{explorerPath}</span>
                </div>
              </header>
              {#if explorerIsLoading}
                <div class="split-pane-empty">フォルダを読み込んでいます...</div>
              {:else if filteredExplorerEntries.length === 0}
                <div class="split-pane-empty">このフォルダには表示する項目がありません。</div>
              {:else}
                <div class="split-grid-pane" bind:this={explorerSplitLeftPaneElement}>
                  <div class="explorer-grid" style={explorerGridStyle} aria-label="左側のサムネイル一覧">
                    {#each filteredExplorerEntries as entry}
                      <button
                        class="explorer-tile"
                        class:explorer-folder-tile={entry.isDirectory}
                        class:selected={selectedPaths.includes(entry.path)}
                        class:drop-target={explorerDropTargetPath === entry.path}
                        draggable="true"
                        onpointerdown={(event) => prioritizeExplorerContextMenu(event, entry, 'left')}
                        ondragstart={(event) => startExplorerEntryDrag(event, entry, 'left')}
                        ondragend={() => { draggedExplorerEntries = null; clearExplorerDropTarget(); }}
                        ondragover={(event) => updateExplorerDropTarget(event, entry)}
                        ondragleave={clearExplorerDropTarget}
                        ondrop={(event) => dropExplorerEntries(event, entry, 'left')}
                        onclick={(event) => selectExplorerEntry(event, entry)}
                        ondblclick={(event) => openExplorerEntry(entry, event.ctrlKey || event.metaKey)}
                        oncontextmenu={(event) => openExplorerContextMenu(event, entry, 'left')}
                        onkeydown={(event) => onExplorerKeydown(event, entry)}
                      >
                        <span class="explorer-preview" use:observeExplorerThumbnail={{ entry, pane: 'left' }}>
                          {#if explorerThumbnails[entry.path]}
                            <img src={explorerThumbnails[entry.path]} alt="" loading="lazy" />
                          {:else if entry.isDirectory}
                            <Folder size={42} />
                          {:else if entry.extension === '.zip' || entry.extension === '.cbz'}
                            <Archive size={42} />
                          {:else}
                            <File size={42} />
                          {/if}
                        </span>
                        <span class="explorer-tile-name">{getEntryDisplayName(entry)}</span>
                      </button>
                    {/each}
                  </div>
                </div>
              {/if}
            </section>

            <section
              class:split-pane-active={splitFocusedPane === 'right'}
              class="split-pane-right explorer-split-pane"
              role="group"
              aria-label="右側の分割ペイン"
              onpointerdown={beginSplitGesture}
              onpointermove={updateSplitGesture}
              onpointerup={finishSplitGesture}
              oncontextmenu={(event) => openExplorerBlankContextMenu(event, 'right')}
            >
              <header class="split-pane-heading">
                <div>
                  <strong>{splitRightTab?.label ?? explorerSplit.rightPath}</strong>
                  <span title={explorerSplit.rightPath}>{explorerSplit.rightPath}</span>
                </div>
                <button title="親フォルダ" onclick={navigateSplitParent} disabled={!explorerSplit.rightParentPath}>
                  <ArrowUp size={16} />
                </button>
              </header>
              {#if explorerSplit.rightIsLoading}
                <div class="split-pane-empty">フォルダを読み込んでいます...</div>
              {:else if splitRightEntries.length === 0}
                <div class="split-pane-empty">このフォルダには表示する項目がありません。</div>
              {:else}
                <div class="split-grid-pane" bind:this={explorerSplitRightPaneElement}>
                  <div class="explorer-grid" style={explorerGridStyle} aria-label="右側のサムネイル一覧">
                    {#each splitRightEntries as entry}
                      <button
                        class="explorer-tile"
                        class:explorer-folder-tile={entry.isDirectory}
                        class:selected={explorerSplit.rightSelectedPaths.includes(entry.path)}
                        class:drop-target={explorerDropTargetPath === entry.path}
                        draggable="true"
                        onpointerdown={(event) => prioritizeExplorerContextMenu(event, entry, 'right')}
                        ondragstart={(event) => startExplorerEntryDrag(event, entry, 'right')}
                        ondragend={() => { draggedExplorerEntries = null; clearExplorerDropTarget(); }}
                        ondragover={(event) => updateExplorerDropTarget(event, entry)}
                        ondragleave={clearExplorerDropTarget}
                        ondrop={(event) => dropExplorerEntries(event, entry, 'right')}
                        onclick={(event) => selectSplitEntry(event, entry)}
                        ondblclick={() => openSplitEntry(entry)}
                        oncontextmenu={(event) => openExplorerContextMenu(event, entry, 'right')}
                      >
                        <span class="explorer-preview" use:observeExplorerThumbnail={{ entry, pane: 'right' }}>
                          {#if explorerThumbnails[entry.path]}
                            <img src={explorerThumbnails[entry.path]} alt="" loading="lazy" />
                          {:else if entry.isDirectory}
                            <Folder size={42} />
                          {:else if entry.extension === '.zip' || entry.extension === '.cbz'}
                            <Archive size={42} />
                          {:else}
                            <File size={42} />
                          {/if}
                        </span>
                        <span class="explorer-tile-name">{getEntryDisplayName(entry)}</span>
                      </button>
                    {/each}
                  </div>
                </div>
              {/if}
            </section>
          </div>
        {:else}
        {#if explorerIsTruncated}
          <div class="explorer-notice">最初の 2,000 件を表示しています。</div>
        {/if}
        {#if explorerIsLoading}
          <div class="empty">フォルダを読み込んでいます...</div>
        {:else if !explorerPath}
          <div class="empty">開くフォルダを指定してください。</div>
        {:else if filteredExplorerEntries.length === 0}
          <div
            class="explorer-workspace explorer-workspace-empty"
            role="group"
            onpointerdown={beginExplorerGesture}
            onpointermove={updateExplorerGesture}
            onpointerup={finishExplorerGesture}
            oncontextmenu={(event) => openExplorerBlankContextMenu(event, 'left')}
          >
            <div class="empty">このフォルダには表示する項目がありません。</div>
          </div>
        {:else}
          <div
              class="explorer-workspace"
              role="group"
              onwheel={handleExplorerCardZoom}
              onpointerdown={beginExplorerGesture}
              onpointermove={updateExplorerGesture}
              onpointerup={finishExplorerGesture}
              oncontextmenu={(event) => openExplorerBlankContextMenu(event, 'left')}
            >
              <div class="explorer-grid-pane" bind:this={explorerGridPaneElement} onscroll={saveExplorerTabScroll}>
                <div class="explorer-grid" style={explorerGridStyle} aria-label="サムネイル一覧">
                  {#each filteredExplorerEntries as entry}
                    <button
                      class="explorer-tile"
                      class:explorer-folder-tile={entry.isDirectory}
                      class:selected={selectedPaths.includes(entry.path)}
                      class:drop-target={explorerDropTargetPath === entry.path}
                      draggable="true"
                      onpointerdown={(event) => prioritizeExplorerContextMenu(event, entry, 'left')}
                      ondragstart={(event) => startExplorerEntryDrag(event, entry, 'left')}
                      ondragend={() => { draggedExplorerEntries = null; clearExplorerDropTarget(); }}
                      ondragover={(event) => updateExplorerDropTarget(event, entry)}
                      ondragleave={clearExplorerDropTarget}
                      ondrop={(event) => dropExplorerEntries(event, entry, 'left')}
                      onclick={(event) => selectExplorerEntry(event, entry, 'grid')}
                      ondblclick={(event) => openExplorerEntry(entry, event.ctrlKey || event.metaKey)}
                      oncontextmenu={(event) => openExplorerContextMenu(event, entry, 'left')}
                      onkeydown={(event) => onExplorerKeydown(event, entry)}
                      data-explorer-path={entry.path}
                    >
                      <span class="explorer-preview" use:observeExplorerThumbnail={{ entry, pane: 'left' }}>
                        {#if explorerThumbnails[entry.path]}
                          <img src={explorerThumbnails[entry.path]} alt="" loading="lazy" />
                        {:else if entry.isDirectory}
                          <Folder size={42} />
                        {:else if entry.extension === '.zip' || entry.extension === '.cbz'}
                          <Archive size={42} />
                        {:else}
                          <File size={42} />
                        {/if}
                      </span>
                      <span class="explorer-tile-name">{getEntryDisplayName(entry)}</span>
                    </button>
                  {/each}
                </div>
              </div>

              <div
                class="explorer-detail-pane"
                bind:this={explorerDetailPaneElement}
                role="listbox"
                tabindex="0"
                aria-label="詳細一覧"
                onscroll={saveExplorerTabScroll}
                oncontextmenu={openExplorerColumnMenu}
                style={explorerDetailGridStyle}
              >
                <div class="file-row file-header">
                  {#each visibleExplorerDetailColumns as column}
                    {#if column.sort}
                      <button
                        draggable="true"
                        ondragstart={(event) => startExplorerDetailColumnDrag(event, column.id)}
                        ondragend={() => (draggedExplorerDetailColumn = null)}
                        ondragover={(event) => event.preventDefault()}
                        ondrop={(event) => reorderExplorerDetailColumn(event, column.id)}
                        onclick={() => toggleExplorerSort(column.sort)}
                      >
                        {column.label} {#if explorerSort === column.sort}{#if explorerSortDirection === 'asc'}<ChevronUp size={14} />{:else}<ChevronDown size={14} />{/if}{/if}
                      </button>
                    {:else}
                      <span
                        role="columnheader"
                        tabindex="-1"
                        draggable="true"
                        ondragstart={(event) => startExplorerDetailColumnDrag(event, column.id)}
                        ondragend={() => (draggedExplorerDetailColumn = null)}
                        ondragover={(event) => event.preventDefault()}
                        ondrop={(event) => reorderExplorerDetailColumn(event, column.id)}
                      >{column.label}</span>
                    {/if}
                  {/each}
                </div>
              {#each filteredExplorerEntries as entry}
                <button
                  class="file-row"
                  class:selected={selectedPaths.includes(entry.path)}
                  role="option"
                  aria-selected={selectedPaths.includes(entry.path)}
                  draggable="true"
                  onpointerdown={(event) => prioritizeExplorerContextMenu(event, entry, 'left')}
                  ondragstart={(event) => startExplorerEntryDrag(event, entry, 'left')}
                  ondragend={() => { draggedExplorerEntries = null; clearExplorerDropTarget(); }}
                  ondragover={(event) => updateExplorerDropTarget(event, entry)}
                  ondragleave={clearExplorerDropTarget}
                  ondrop={(event) => dropExplorerEntries(event, entry, 'left')}
                  onclick={(event) => selectExplorerEntry(event, entry, 'list')}
                  ondblclick={(event) => openExplorerEntry(entry, event.ctrlKey || event.metaKey)}
                  oncontextmenu={(event) => openExplorerContextMenu(event, entry, 'left')}
                  onkeydown={(event) => onExplorerKeydown(event, entry)}
                  data-explorer-path={entry.path}
                >
                  {#each visibleExplorerDetailColumns as column}
                    {#if column.id === 'icon'}
                      <span class="file-icon" title={entry.isDirectory ? 'フォルダ' : 'ファイル'}>
                        {#if entry.isDirectory}
                          <Folder size={18} />
                        {:else}
                          <File size={18} />
                        {/if}
                      </span>
                    {:else if column.id === 'name'}
                      <span class="file-name">{getEntryDisplayName(entry)}</span>
                    {:else}
                      <span class="detail-value">{getExplorerDetailValue(entry, column.id)}</span>
                    {/if}
                  {/each}
                </button>
              {/each}
              </div>
            </div>
        {/if}
        {/if}
      </section>
    {:else}
      <section class="gallery-content" bind:this={galleryContentElement}>
        <header class="gallery-filters">
          {#if isGalleryFilterEnabled('rating')}
          <div class="gallery-filter-row gallery-filter-row-sorts">
            <strong>Rating</strong>
            <div class="gallery-filter-buttons">
              {#each galleryRatingValues as rating}
                <button
                  class:active={galleryRatingFilters.includes(rating)}
                  class:gallery-rating-premium={rating === 6}
                  onclick={(event) => setGalleryRatingFilter(rating, event)}
                >
                  {formatGalleryRating(rating)} {galleryRatingCounts.get(rating) ?? 0}
                </button>
              {/each}
            </div>
            <div class="gallery-sort-groups">
              <div class="gallery-sorts">
                <span>Filters</span>
                <button
                  class:gallery-filter-sort-priority-1={getGalleryFilterSortPriority(galleryFilterSorts, 'rating') === 1}
                  class:gallery-filter-sort-priority-2={getGalleryFilterSortPriority(galleryFilterSorts, 'rating') === 2}
                  class:gallery-filter-sort-priority-3={getGalleryFilterSortPriority(galleryFilterSorts, 'rating') === 3}
                  aria-pressed={getGalleryFilterSortPriority(galleryFilterSorts, 'rating') > 0}
                  title="左クリックで追加、右クリックで解除"
                  onclick={() => setGalleryFilterSort('rating')}
                  oncontextmenu={(event) => removeGalleryFilterSort(event, 'rating')}
                >Rating ⤵</button>
                <button
                  class:gallery-filter-sort-priority-1={getGalleryFilterSortPriority(galleryFilterSorts, 'files') === 1}
                  class:gallery-filter-sort-priority-2={getGalleryFilterSortPriority(galleryFilterSorts, 'files') === 2}
                  class:gallery-filter-sort-priority-3={getGalleryFilterSortPriority(galleryFilterSorts, 'files') === 3}
                  aria-pressed={getGalleryFilterSortPriority(galleryFilterSorts, 'files') > 0}
                  title="左クリックで追加・昇降順切替、右クリックで解除"
                  onclick={() => setGalleryFilterSort('files')}
                  oncontextmenu={(event) => removeGalleryFilterSort(event, 'files')}
                >Files {getGalleryFilterSortDirection(galleryFilterSorts, 'files') === 'desc' ? '⤵' : '⤴'}</button>
                <button
                  class:gallery-filter-sort-priority-1={getGalleryFilterSortPriority(galleryFilterSorts, 'name') === 1}
                  class:gallery-filter-sort-priority-2={getGalleryFilterSortPriority(galleryFilterSorts, 'name') === 2}
                  class:gallery-filter-sort-priority-3={getGalleryFilterSortPriority(galleryFilterSorts, 'name') === 3}
                  aria-pressed={getGalleryFilterSortPriority(galleryFilterSorts, 'name') > 0}
                  title="左クリックで追加・昇降順切替、右クリックで解除"
                  onclick={() => setGalleryFilterSort('name')}
                  oncontextmenu={(event) => removeGalleryFilterSort(event, 'name')}
                >Abc {getGalleryFilterSortDirection(galleryFilterSorts, 'name') === 'desc' ? '⤵' : '⤴'}</button>
              </div>
              <span class="gallery-sort-divider" aria-hidden="true">|</span>
              <div class="gallery-sorts">
                <span>Thumbnail</span>
                <button
                  class:gallery-thumbnail-sort-priority-1={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'rating') === 1}
                  class:gallery-thumbnail-sort-priority-2={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'rating') === 2}
                  class:gallery-thumbnail-sort-priority-3={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'rating') === 3}
                  class:gallery-thumbnail-sort-priority-4={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'rating') === 4}
                  aria-pressed={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'rating') > 0}
                  title="左クリックで追加、右クリックで解除"
                  onclick={() => setGalleryThumbnailSort('rating')}
                  oncontextmenu={(event) => removeGalleryThumbnailSort(event, 'rating')}
                >Rating ⤵</button>
                <button
                  class:gallery-thumbnail-sort-priority-1={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'images') === 1}
                  class:gallery-thumbnail-sort-priority-2={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'images') === 2}
                  class:gallery-thumbnail-sort-priority-3={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'images') === 3}
                  class:gallery-thumbnail-sort-priority-4={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'images') === 4}
                  aria-pressed={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'images') > 0}
                  title="左クリックで追加・昇降順切替、右クリックで解除"
                  onclick={() => setGalleryThumbnailSort('images')}
                  oncontextmenu={(event) => removeGalleryThumbnailSort(event, 'images')}
                >Pics {getGalleryThumbnailSortDirection(galleryThumbnailSorts, 'images') === 'desc' ? '⤵' : '⤴'}</button>
                <button
                  class:gallery-thumbnail-sort-priority-1={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'accessed') === 1}
                  class:gallery-thumbnail-sort-priority-2={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'accessed') === 2}
                  class:gallery-thumbnail-sort-priority-3={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'accessed') === 3}
                  class:gallery-thumbnail-sort-priority-4={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'accessed') === 4}
                  aria-pressed={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'accessed') > 0}
                  title="左クリックで追加・昇降順切替、右クリックで解除"
                  onclick={() => setGalleryThumbnailSort('accessed')}
                  oncontextmenu={(event) => removeGalleryThumbnailSort(event, 'accessed')}
                >Access date {getGalleryThumbnailSortDirection(galleryThumbnailSorts, 'accessed') === 'desc' ? '⤵' : '⤴'}</button>
                <button
                  class:gallery-thumbnail-sort-priority-1={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'path') === 1}
                  class:gallery-thumbnail-sort-priority-2={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'path') === 2}
                  class:gallery-thumbnail-sort-priority-3={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'path') === 3}
                  class:gallery-thumbnail-sort-priority-4={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'path') === 4}
                  class="gallery-full-path-sort"
                  aria-pressed={getGalleryThumbnailSortPriority(galleryThumbnailSorts, 'path') > 0}
                  title="左クリックで追加・昇降順切替、右クリックで解除"
                  onclick={() => setGalleryThumbnailSort('path')}
                  oncontextmenu={(event) => removeGalleryThumbnailSort(event, 'path')}
                >Filepath {getGalleryThumbnailSortDirection(galleryThumbnailSorts, 'path') === 'desc' ? '⤵' : '⤴'}</button>
              </div>
            </div>
            <div class="gallery-filter-actions gallery-filter-reset-action">
              <button aria-label="Galleryのフィルター選択をすべて解除" onclick={clearGalleryFilters}>Reset</button>
            </div>
          </div>
          {/if}
          {#if isGalleryFilterEnabled('creator')}
          <div class="gallery-filter-row">
            <strong>{getGalleryFilterLabel('creator')}</strong>
            <div class="gallery-filter-control">
              <div bind:this={galleryCreatorFilterButtonsElement} class:gallery-filter-buttons-collapsed={!galleryCreatorFiltersExpanded} class="gallery-filter-buttons gallery-filter-buttons-expandable" onwheel={scrollGalleryFilterRows}>
                {#each getGalleryFilterOptions(galleryCreators, galleryPinnedCreators, galleryPromotedCreators) as option}
                  <button
                    class:active={galleryCreatorFilters.includes(option.value)}
                    class:gallery-filter-pinned={galleryPinnedCreators.includes(option.value)}
                    onclick={(event) => setGalleryCreatorFilter(option.value, event)}
                    oncontextmenu={(event) => toggleGalleryFilterPin(event, 'creator', option.value)}
                  >
                    {#if galleryPinnedCreators.includes(option.value)}<Pin class="gallery-filter-pin-icon" size={12} fill="currentColor" />{/if}
                    {option.label ?? option.value} {option.count}
                  </button>
                {/each}
              </div>
              <div class="gallery-filter-actions gallery-filter-expand-action">
                <button
                  class:gallery-filter-collapse={galleryCreatorFiltersExpanded}
                  class:gallery-filter-collapse-pinned={galleryCollapsePins.creator}
                  class:gallery-filter-expand-highlight={!galleryCreatorFiltersExpanded && galleryCreatorHasHiddenFilters}
                  title={galleryCollapsePins.creator ? '右クリックでCollapseのピンを解除' : '右クリックでCollapseをピン留め'}
                  onclick={() => toggleGalleryFilterExpansion('creator')}
                  oncontextmenu={(event) => toggleGalleryFilterCollapsePin(event, 'creator')}
                >
                  {#if galleryCollapsePins.creator}<Pin class="gallery-filter-collapse-pin-icon" size={12} fill="currentColor" />{/if}
                  {galleryCreatorFiltersExpanded ? 'Collapse' : 'Expand'}
                </button>
              </div>
            </div>
          </div>
          {/if}
          {#if isGalleryFilterEnabled('title')}
          <div class="gallery-filter-row">
            <strong>{getGalleryFilterLabel('title')}</strong>
            <div class="gallery-filter-control">
              <div bind:this={galleryTitleFilterButtonsElement} class:gallery-filter-buttons-collapsed={!galleryTitleFiltersExpanded} class="gallery-filter-buttons gallery-filter-buttons-expandable" onwheel={scrollGalleryFilterRows}>
                {#each getGalleryFilterOptions(galleryTitles, galleryPinnedTitles, galleryPromotedTitles) as option}
                  <button
                    class:active={galleryTitleFilters.includes(option.value)}
                    class:gallery-filter-pinned={galleryPinnedTitles.includes(option.value)}
                    onclick={(event) => setGalleryTitleFilter(option.value, event)}
                    oncontextmenu={(event) => toggleGalleryFilterPin(event, 'title', option.value)}
                  >
                    {#if galleryPinnedTitles.includes(option.value)}<Pin class="gallery-filter-pin-icon" size={12} fill="currentColor" />{/if}
                    {option.label ?? option.value} {option.count}
                  </button>
                {/each}
              </div>
              <div class="gallery-filter-actions gallery-filter-expand-action">
                <button
                  class:gallery-filter-collapse={galleryTitleFiltersExpanded}
                  class:gallery-filter-collapse-pinned={galleryCollapsePins.title}
                  class:gallery-filter-expand-highlight={!galleryTitleFiltersExpanded && galleryTitleHasHiddenFilters}
                  title={galleryCollapsePins.title ? '右クリックでCollapseのピンを解除' : '右クリックでCollapseをピン留め'}
                  onclick={() => toggleGalleryFilterExpansion('title')}
                  oncontextmenu={(event) => toggleGalleryFilterCollapsePin(event, 'title')}
                >
                  {#if galleryCollapsePins.title}<Pin class="gallery-filter-collapse-pin-icon" size={12} fill="currentColor" />{/if}
                  {galleryTitleFiltersExpanded ? 'Collapse' : 'Expand'}
                </button>
              </div>
            </div>
          </div>
          {/if}
          {#if isGalleryFilterEnabled('character') && isGalleryFilterEnabled('title') && galleryTitleFilters.length === 1}
            <div class="gallery-filter-row">
              <strong>{getGalleryFilterLabel('character')}</strong>
              <div class="gallery-filter-control">
                <div bind:this={galleryCharacterFilterButtonsElement} class:gallery-filter-buttons-collapsed={!galleryCharacterFiltersExpanded} class="gallery-filter-buttons gallery-filter-buttons-expandable" onwheel={scrollGalleryFilterRows}>
                  {#each getGalleryFilterOptions(galleryCharacters, [], galleryPromotedCharacters) as option}
                    <button class:active={galleryCharacterFilters.includes(option.value)} onclick={(event) => setGalleryCharacterFilter(option.value, event)}>
                      {option.label ?? option.value} {option.count}
                    </button>
                  {/each}
                </div>
                <div class="gallery-filter-actions gallery-filter-expand-action">
                  <button
                    class:gallery-filter-collapse={galleryCharacterFiltersExpanded}
                    class:gallery-filter-collapse-pinned={galleryCollapsePins.character}
                    class:gallery-filter-expand-highlight={!galleryCharacterFiltersExpanded && galleryCharacterHasHiddenFilters}
                    title={galleryCollapsePins.character ? '右クリックでCollapseのピンを解除' : '右クリックでCollapseをピン留め'}
                    onclick={() => toggleGalleryFilterExpansion('character')}
                    oncontextmenu={(event) => toggleGalleryFilterCollapsePin(event, 'character')}
                  >
                    {#if galleryCollapsePins.character}<Pin class="gallery-filter-collapse-pin-icon" size={12} fill="currentColor" />{/if}
                    {galleryCharacterFiltersExpanded ? 'Collapse' : 'Expand'}
                  </button>
                </div>
              </div>
            </div>
          {/if}
          {#if isGalleryFilterEnabled('tag')}
          <div class="gallery-filter-row">
            <strong>{getGalleryFilterLabel('tag')}</strong>
            <div class="gallery-filter-control">
              <div bind:this={galleryTagFilterButtonsElement} class:gallery-filter-buttons-collapsed={!galleryTagFiltersExpanded} class="gallery-filter-buttons gallery-filter-buttons-expandable" onwheel={scrollGalleryFilterRows}>
                {#each getGalleryFilterOptions(galleryTags, [], galleryPromotedTags) as option}
                  <button class:active={galleryTagFilters.includes(option.value)} onclick={(event) => setGalleryTagFilter(option.value, event)}>
                    {option.label ?? option.value} {option.count}
                  </button>
                {/each}
              </div>
              <div class="gallery-filter-actions gallery-filter-expand-action">
                <button
                  class:gallery-filter-collapse={galleryTagFiltersExpanded}
                  class:gallery-filter-collapse-pinned={galleryCollapsePins.tag}
                  class:gallery-filter-expand-highlight={!galleryTagFiltersExpanded && galleryTagHasHiddenFilters}
                  title={galleryCollapsePins.tag ? '右クリックでCollapseのピンを解除' : '右クリックでCollapseをピン留め'}
                  onclick={() => toggleGalleryFilterExpansion('tag')}
                  oncontextmenu={(event) => toggleGalleryFilterCollapsePin(event, 'tag')}
                >
                  {#if galleryCollapsePins.tag}<Pin class="gallery-filter-collapse-pin-icon" size={12} fill="currentColor" />{/if}
                  {galleryTagFiltersExpanded ? 'Collapse' : 'Expand'}
                </button>
              </div>
            </div>
          </div>
          {/if}
          <div class="gallery-filter-row gallery-search-row">
            <strong>Utilities</strong>
            <div class="gallery-creator-summary-search-control gallery-search-control">
              <label class="gallery-creator-summary-search">
                <Search size={16} />
                <input bind:this={gallerySearchInputElement} bind:value={galleryQuery} placeholder="Galleryを検索" />
              </label>
              <button class="gallery-creator-summary-refresh" title="更新（未実装）" aria-label="Galleryを更新（未実装）">
                <RefreshCw size={16} />
              </button>
              <button class="gallery-creator-summary-refresh sticky-note-launch-button" title="Galleryに付箋を追加" aria-label="Galleryに付箋を追加" onclick={createStickyNoteFromToolbar}>
                <StickyNote size={16} />
              </button>
              <button class="gallery-creator-summary-refresh view-bookmark-button" title="現在のGalleryをBookmark" aria-label="現在のGalleryをBookmark" onclick={captureViewBookmarkFromToolbar}>
                <Bookmark size={16} />
              </button>
              <button
                class="gallery-creator-summary-refresh gallery-creator-tracking-launch"
                title={galleryCreatorFilters.length === 1 ? `${galleryCreatorFilters[0]}のCreator Trackingを開く` : 'Creatorを1件だけ選択すると使用できます'}
                aria-label="選択中CreatorのCreator Trackingを開く"
                disabled={galleryCreatorFilters.length !== 1 || pendingGalleryCreatorTracking !== null}
                onclick={openSelectedGalleryCreatorTracking}
              >
                <UserRound size={16} />
              </button>
              <button
                class="gallery-creator-summary-refresh gallery-creator-storage-launch"
                title={galleryCreatorFilters.length === 1 ? `${galleryCreatorFilters[0]}のGallery・StockroomフォルダをExplorerで開く` : 'Creatorを1件だけ選択すると使用できます'}
                aria-label="選択中CreatorのストレージフォルダをExplorerで開く"
                disabled={galleryCreatorFilters.length !== 1 || pendingGalleryCreatorStorageRequest !== null}
                onclick={openSelectedGalleryCreatorStorage}
              >
                <FolderOpen size={16} />
              </button>
            </div>
          </div>
        </header>

        {#if galleryIsLoading && galleryWorks.length === 0}
          <div class="empty">Galleryを読み込み中...</div>
        {:else if galleryWorks.length === 0}
          <div class="empty">この区分には表示できる作品がありません。</div>
        {:else if visibleGalleryWorks.length === 0}
          <div class="empty">検索条件に一致する作品がありません。</div>
        {:else}
          <div class:gallery-work-grid-landscape={galleryCardAspect === 'landscape'} class="gallery-work-grid" style={galleryGridStyle}>
            {#each visibleGalleryWorks as work (`${work.id}:${galleryThumbnailRevision}`)}
              <article class:selected={selectedGalleryWorkIds.has(work.id)} class:gallery-work-card-landscape={galleryCardAspect === 'landscape'} class="gallery-work-card" oncontextmenu={(event) => openGalleryContextMenu(event, work)}>
                <button
                  class:show-gallery-card-select={selectedGalleryWorkIds.size > 1 && selectedGalleryWorkIds.has(work.id)}
                  class="gallery-card-select"
                  aria-label="選択"
                  aria-pressed={selectedGalleryWorkIds.has(work.id)}
                  onclick={(event) => toggleGalleryWorkSelection(event, work)}
                >
                  <Check size={15} strokeWidth={3} />
                </button>
                <button
                  class="gallery-card-rating"
                  class:gallery-card-rating-lit={galleryRatingLitIds.has(work.id)}
                  class:rating-increase={galleryRatingEffects[work.id] === 'increase'}
                  class:rating-decrease={galleryRatingEffects[work.id] === 'decrease'}
                  aria-label="左クリックで評価を上げ、右クリックで評価を下げる"
                  disabled={pendingGalleryRatingIds.has(work.id)}
                  onclick={(event) => adjustGalleryWorkRating(event, work, 1)}
                  oncontextmenu={(event) => adjustGalleryWorkRating(event, work, -1)}
                >
                  <Star size={20} />
                </button>
                <button class="gallery-work-open" use:observeGalleryThumbnail={work} onclick={(event) => handleGalleryWorkCardClick(event, work)} ondblclick={(event) => handleGalleryWorkCardDoubleClick(event, work)}>
                  <div class="gallery-work-thumb">
                    {#if galleryThumbnails[work.id]}
                      <img src={galleryThumbnails[work.id]} alt="" loading="lazy" />
                    {:else}
                      <Archive size={32} />
                    {/if}
                  </div>
                  <div class="gallery-work-body">
                    <h2>{getGalleryDisplayName(work)}</h2>
                    <p class="gallery-work-meta">
                      {formatGalleryCardRating(work.rating)} /
                      {getGalleryCardAspect(work.category) === 'landscape'
                        ? formatGalleryDuration(work.durationSeconds)
                        : `${work.imageCount.toLocaleString('ja-JP')}枚`} /
                      {formatGalleryDate(getGalleryCardDate(work))}
                    </p>
                    {#if work.creator || work.title || work.character}
                      {@const workAttributes = [work.creator, work.title, work.character].filter(Boolean).join(' / ')}
                      <p class="gallery-work-sub" title={workAttributes}>{workAttributes}</p>
                    {/if}
                  </div>
                </button>
              </article>
            {/each}
          </div>
          {#if galleryWorks.length < galleryTotal}
            <div class="gallery-load-more">
              <button disabled={galleryIsLoading} onclick={() => loadGalleryWorks(true)}>{galleryIsLoading ? '読み込み中...' : 'さらに読み込む'}</button>
            </div>
          {/if}
        {/if}
      </section>
    {/if}
  </section>
</main>

{#if globalSearchOpen}
  <div
    class="modal-backdrop global-search-backdrop"
    role="presentation"
    onclick={(event) => {
      if (event.target === event.currentTarget) globalSearchOpen = false;
    }}
  >
    <form
      class="modal global-search-modal"
      role="search"
      onsubmit={(event) => {
        event.preventDefault();
        executeGlobalSearch();
      }}
    >
      <div class="modal-heading">
        <div>
          <h2>検索</h2>
          <span>通常検索または作者検索を選択します</span>
        </div>
        <button type="button" aria-label="閉じる" onclick={() => globalSearchOpen = false}><X size={18} /></button>
      </div>
      <div class="global-search-mode" role="group" aria-label="検索対象">
        <button type="button" class:active={globalSearchMode === 'normal'} onclick={() => globalSearchMode = 'normal'}><Search size={16} />通常検索</button>
        <button type="button" class:active={globalSearchMode === 'creator'} onclick={() => globalSearchMode = 'creator'}><UserRound size={16} />作者検索</button>
      </div>
      <label class="global-search-field">
        <span>{globalSearchMode === 'creator' ? '作者名' : '検索語'}</span>
        <input bind:this={globalSearchInputElement} bind:value={globalSearchQuery} type="search" placeholder={globalSearchMode === 'creator' ? '作者を検索' : '現在の画面を検索'} />
      </label>
      <p>{globalSearchMode === 'creator'
        ? 'Creatorsを開き、作者名で検索します。'
        : activeView === 'explorer'
          ? '現在フォーカスしているExplorerフォルダを検索します。'
          : activeView === 'creators'
            ? '現在のCreators区分を検索します。'
            : '現在のGallery区分を検索します。'}</p>
      <div class="modal-actions">
        <button type="button" class="quiet-button" onclick={() => globalSearchOpen = false}>キャンセル</button>
        <button type="submit" class="primary-button">検索</button>
      </div>
    </form>
  </div>
{/if}

{#if themeSaveConfirmOpen}
  <div
    class="modal-backdrop"
    role="presentation"
    onclick={(event) => {
      if (event.target === event.currentTarget) themeSaveConfirmOpen = false;
    }}
  >
    <div
      bind:this={themeSaveConfirmElement}
      class="modal theme-save-confirm-modal"
      role="dialog"
      tabindex="-1"
      aria-modal="true"
      aria-labelledby="theme-save-confirm-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') themeSaveConfirmOpen = false;
      }}
    >
      <div class="modal-heading">
        <div>
          <h2 id="theme-save-confirm-title">テーマ設定を適用しますか？</h2>
          <span>アプリ全体の配色が切り替わります</span>
        </div>
        <button type="button" aria-label="閉じる" onclick={() => (themeSaveConfirmOpen = false)}><X size={18} /></button>
      </div>
      <div class="theme-confirm-summary">
        <span><small>テーマ</small><strong>{themeSettingsDraft.theme === 'light' ? 'ライト' : 'ダーク'}</strong></span>
        <span><small>メイン</small><i style={`background: ${themeSettingsDraft.mainColor};`}></i><code>{themeSettingsDraft.mainColor.toUpperCase()}</code></span>
        <span><small>サブ</small><i style={`background: ${themeSettingsDraft.subColor};`}></i><code>{themeSettingsDraft.subColor.toUpperCase()}</code></span>
      </div>
      <p>保存後は次回起動時にも同じテーマが復元されます。</p>
      <div class="modal-actions">
        <button type="button" class="quiet-button" onclick={() => (themeSaveConfirmOpen = false)}>キャンセル</button>
        <button type="button" class="primary-button" onclick={confirmThemeSettingsSave}>保存して適用</button>
      </div>
    </div>
  </div>
{/if}

{#each visibleStickyNotes as note (note.id)}
  {@const noteColor = getStickyNotePaletteEntry(note.colorKey)}
  <article
    class:dragging={stickyNoteInteraction?.id === note.id}
    class="sticky-note-window"
    data-sticky-note-id={note.id}
    style={`left: ${note.x}px; top: ${note.y}px; width: ${note.width}px; height: ${note.height}px; --sticky-note-color: ${noteColor.color}; --sticky-note-border: ${noteColor.border}; --sticky-note-text: ${noteColor.text};`}
    onpointerdown={() => bringStickyNoteToFront(note.id)}
  >
    <div class="sticky-note-titlebar" role="group" aria-label="付箋の操作" onpointerdown={(event) => beginStickyNoteMove(event, note)}>
      <span class="sticky-note-titlebar-label"><StickyNote size={15} /> Sticky Note</span>
      <div class="sticky-note-titlebar-actions" role="group" aria-label="付箋の表示と色" onpointerdown={(event) => event.stopPropagation()}>
        <button
          class:active={note.contentMode === 'markdown'}
          title={note.contentMode === 'markdown' ? 'プレーンテキストに切り替え' : 'Markdownに切り替え'}
          aria-label={note.contentMode === 'markdown' ? 'プレーンテキストに切り替え' : 'Markdownに切り替え'}
          onpointerdown={(event) => event.stopPropagation()}
          onclick={() => toggleStickyNoteContentMode(note.id)}
        >{#if note.contentMode === 'markdown'}<Code2 size={15} />{:else}<FileText size={15} />{/if}</button>
        <button
          class:active={stickyNotePaletteOpenId === note.id}
          class="sticky-note-palette-toggle"
          title="付箋の色を変更"
          aria-label="付箋の色を変更"
          aria-expanded={stickyNotePaletteOpenId === note.id}
          onpointerdown={(event) => event.stopPropagation()}
          onclick={() => toggleStickyNotePalette(note.id)}
        ><Palette size={15} /></button>
        <button
          class="sticky-note-delete"
          title="付箋を破棄"
          aria-label="付箋を破棄"
          onpointerdown={(event) => event.stopPropagation()}
          onclick={() => deleteStickyNote(note.id)}
        ><Trash2 size={15} /></button>
        {#if stickyNotePaletteOpenId === note.id}
          <div class="sticky-note-palette" role="group" aria-label="付箋の色" onpointerdown={(event) => event.stopPropagation()}>
            {#each stickyNotePalette as color}
              <button
                class:selected={note.colorKey === color.key}
                style={`--sticky-note-swatch: ${color.color}; --sticky-note-swatch-border: ${color.border};`}
                title={color.label}
                aria-label={color.label}
                aria-pressed={note.colorKey === color.key}
                onclick={() => setStickyNoteColor(note.id, color.key)}
              >{#if note.colorKey === color.key}<Check size={13} />{/if}</button>
            {/each}
          </div>
        {/if}
      </div>
    </div>
    {#if note.contentMode === 'plain' || markdownEditingNoteIds.has(note.id)}
      <textarea
        value={note.content}
        aria-label="付箋の本文"
        placeholder={note.contentMode === 'markdown' ? 'Markdownを入力...' : 'メモを入力...'}
        spellcheck="false"
        oninput={(event) => updateStickyNoteContent(note.id, event.currentTarget.value)}
        onblur={() => { if (note.contentMode === 'markdown') setMarkdownNoteEditing(note.id, false); }}
      ></textarea>
    {:else}
      <div class="sticky-note-markdown" role="button" tabindex="0" onclick={(event) => handleStickyNoteMarkdownClick(event, note.id)} onkeydown={(event) => { if (event.key === 'Enter') setMarkdownNoteEditing(note.id, true); }}>
        {#if note.content.trim()}{@html renderStickyNoteMarkdown(note.content)}{:else}<span class="sticky-note-markdown-empty">クリックしてMarkdownを編集...</span>{/if}
      </div>
    {/if}
    {#each stickyNoteResizeEdges as edge}
      <span
        class={`sticky-note-resize-handle sticky-note-resize-${edge}`}
        aria-hidden="true"
        onpointerdown={(event) => beginStickyNoteResize(event, note, edge)}
      ></span>
    {/each}
  </article>
{/each}

{#if galleryTagAssignment && activeView === 'library'}
  <div class="modal-backdrop gallery-title-assignment-backdrop" role="presentation">
    <div class:panel-open={galleryTagAssignment.assignedTagsPanelOpen} class="gallery-title-assignment-shell gallery-tag-assignment-shell">
      {#if galleryTagAssignment.assignedTagsPanelOpen}
        <aside class="gallery-title-assignment-current-panel gallery-tag-assignment-current-panel" aria-label="作品に登録済みのTag">
          <div>
            <h2>作品に登録済みのTag</h2>
            <p>選択したすべての作品に共通するTagを表示します</p>
          </div>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-current gallery-tag-assignment-list-current">
            {#if galleryTagAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if galleryTagAssignment.commonAssignedTags.length === 0}
              <span class="gallery-title-assignment-empty">共通するTagはありません</span>
            {:else}
              {#each galleryTagAssignment.commonAssignedTags as option (option.id)}
                <button
                  class:selected={galleryTagAssignment.selectedAssignedTagIds.includes(option.id)}
                  disabled={galleryTagAssignment.isSaving}
                  onclick={() => toggleGalleryAssignedTagSelection(option.id)}
                >
                  <span>{option.tag}</span>
                </button>
              {/each}
            {/if}
          </div>
          <div class="gallery-title-assignment-remove-flow" aria-hidden="true">
            <ChevronDown size={18} />
          </div>
          <button
            class="danger-button gallery-title-assignment-remove"
            disabled={galleryTagAssignment.isLoading || galleryTagAssignment.isSaving || galleryTagAssignment.selectedAssignedTagIds.length === 0}
            onclick={removeSelectedGalleryTags}
          >
            選択したTagを解除
          </button>
        </aside>
      {/if}
      <button
        type="button"
        class="gallery-title-assignment-panel-toggle"
        class:panel-open={galleryTagAssignment.assignedTagsPanelOpen}
        aria-label={galleryTagAssignment.assignedTagsPanelOpen ? '登録済みTagを閉じる' : '登録済みTagを開く'}
        aria-expanded={galleryTagAssignment.assignedTagsPanelOpen}
        onclick={() => {
          if (galleryTagAssignment) {
            galleryTagAssignment = {
              ...galleryTagAssignment,
              assignedTagsPanelOpen: !galleryTagAssignment.assignedTagsPanelOpen
            };
          }
        }}
      >
        {#if galleryTagAssignment.assignedTagsPanelOpen}<ChevronRight size={20} />{:else}<ChevronLeft size={20} />{/if}
      </button>
      <dialog
        open
        class="modal gallery-title-assignment-modal"
        aria-labelledby="gallery-tag-assignment-title"
        onkeydown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            closeGalleryTagAssignment();
          }
        }}
      >
        <div class="modal-heading">
          <h2 id="gallery-tag-assignment-title">Tagの登録と解除</h2>
          <button class="gallery-title-assignment-close" title="閉じる" disabled={galleryTagAssignment.isSaving} onclick={closeGalleryTagAssignment}><X size={18} /></button>
        </div>
        <p class="gallery-title-assignment-summary">
          {galleryTagAssignment.works.length} 件に登録します
        </p>

        <section class="gallery-title-assignment-section">
          <h3 title={getGalleryTagAssignmentScopeLabel(galleryTagAssignment)}>{getGalleryTagAssignmentScopeLabel(galleryTagAssignment)}に登録済みのTag</h3>
          <label class="gallery-title-assignment-filter">
            <Search size={16} />
            <input
              value={galleryTagAssignment.creatorQuery}
              placeholder="Tagを絞り込む"
              aria-label="CreatorとTitleに登録済みのTagを絞り込む"
              disabled={galleryTagAssignment.isLoading || galleryTagAssignment.isSaving}
              oninput={(event) => {
                if (galleryTagAssignment) {
                  galleryTagAssignment = { ...galleryTagAssignment, creatorQuery: event.currentTarget.value };
                }
              }}
            />
          </label>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-creator" aria-label="CreatorとTitleに登録済みのTag">
            {#if galleryTagAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if visibleGalleryTagAssignmentCreatorTitleTags.length === 0}
              <span class="gallery-title-assignment-empty">該当するTagはありません</span>
            {:else}
              {#each visibleGalleryTagAssignmentCreatorTitleTags as option (option.id)}
                <button
                  class:selected={galleryTagAssignment.selectedTagId === option.id}
                  disabled={galleryTagAssignment.isSaving}
                  onclick={() => selectGalleryTagAssignment(option.id)}
                >
                  <span>{option.tag}</span>
                  <small>{option.workCount} 件</small>
                </button>
              {/each}
            {/if}
          </div>
        </section>

        <section class="gallery-title-assignment-section">
          <h3>登録可能なTag</h3>
          <label class="gallery-title-assignment-filter">
            <Search size={16} />
            <input
              value={galleryTagAssignment.query}
              placeholder="Tagを絞り込む"
              aria-label="登録可能なTagを絞り込む"
              disabled={galleryTagAssignment.isLoading || galleryTagAssignment.isSaving}
              oninput={(event) => {
                if (galleryTagAssignment) {
                  galleryTagAssignment = { ...galleryTagAssignment, query: event.currentTarget.value };
                }
              }}
            />
          </label>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-available" aria-label="登録可能なTag">
            {#if galleryTagAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if visibleGalleryTagAssignmentAvailableTags.length === 0}
              <span class="gallery-title-assignment-empty">該当するTagはありません</span>
            {:else}
              {#each visibleGalleryTagAssignmentAvailableTags as option (option.id)}
                <button
                  class:selected={galleryTagAssignment.selectedTagId === option.id}
                  disabled={galleryTagAssignment.isSaving}
                  onclick={() => selectGalleryTagAssignment(option.id)}
                >
                  <span>{option.tag}</span>
                </button>
              {/each}
            {/if}
          </div>
          <button class="primary-button gallery-title-assignment-new" disabled={galleryTagAssignment.isSaving} onclick={openGalleryTagAssignmentNewTag}>
            <Plus size={16} />
            <span>新規Tagの登録</span>
          </button>
        </section>

        <div class="modal-actions gallery-title-assignment-actions">
          <button
            class="primary-button"
            disabled={galleryTagAssignment.isLoading || galleryTagAssignment.isSaving || galleryTagAssignment.selectedTagId === null}
            onclick={applyGalleryTagAssignment}
          >
            {galleryTagAssignment.isSaving ? '登録中...' : '登録'}
          </button>
          <button class="quiet-button gallery-title-assignment-cancel" disabled={galleryTagAssignment.isSaving} onclick={closeGalleryTagAssignment}>キャンセル</button>
        </div>
      </dialog>
    </div>
  </div>
{/if}

{#if galleryTitleAssignment && activeView === 'library'}
  <div class="modal-backdrop gallery-title-assignment-backdrop" role="presentation">
    <div
      class:panel-open={galleryTitleAssignment.assignedTitlesPanelOpen}
      class:character-panel-open={galleryTitleAssignment.characterPanelOpen}
      class="gallery-title-assignment-shell"
    >
      {#if galleryTitleAssignment.assignedTitlesPanelOpen}
        <aside class="gallery-title-assignment-current-panel" aria-label="作品に登録済みのTitle">
          <div>
            <h2>作品に登録済みのTitle</h2>
            <p>選択したすべての作品に共通するTitleを表示します</p>
          </div>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-current">
            {#if galleryTitleAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if galleryTitleAssignment.commonAssignedTitles.length === 0}
              <span class="gallery-title-assignment-empty">共通するTitle属性はありません</span>
            {:else}
              {#each galleryTitleAssignment.commonAssignedTitles as option (option.id)}
                <button
                  class:selected={galleryTitleAssignment.selectedAssignedTitleIds.includes(option.id)}
                  disabled={galleryTitleAssignment.isSaving}
                  onclick={() => toggleGalleryAssignedTitleSelection(option.id)}
                >
                  <span>{option.title}</span>
                  <small>{option.categoryName}</small>
                </button>
              {/each}
            {/if}
          </div>
          <div class="gallery-title-assignment-remove-flow" aria-hidden="true">
            <ChevronDown size={18} />
          </div>
          <button
            class="danger-button gallery-title-assignment-remove"
            disabled={galleryTitleAssignment.isLoading || galleryTitleAssignment.isSaving || galleryTitleAssignment.selectedAssignedTitleIds.length === 0}
            onclick={removeSelectedGalleryTitles}
          >
            選択したTitle属性を解除
          </button>
        </aside>
      {/if}
      <button
        type="button"
        class="gallery-title-assignment-panel-toggle"
        class:panel-open={galleryTitleAssignment.assignedTitlesPanelOpen}
        aria-label={galleryTitleAssignment.assignedTitlesPanelOpen ? '登録済みTitleを閉じる' : '登録済みTitleを開く'}
        aria-expanded={galleryTitleAssignment.assignedTitlesPanelOpen}
        onclick={() => {
          if (galleryTitleAssignment) {
            galleryTitleAssignment = {
              ...galleryTitleAssignment,
              assignedTitlesPanelOpen: !galleryTitleAssignment.assignedTitlesPanelOpen
            };
          }
        }}
      >
        {#if galleryTitleAssignment.assignedTitlesPanelOpen}<ChevronRight size={20} />{:else}<ChevronLeft size={20} />{/if}
      </button>
      <dialog
        open
        class="modal gallery-title-assignment-modal"
        aria-labelledby="gallery-title-assignment-title"
        onkeydown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            closeGalleryTitleAssignment();
          }
        }}
      >
      <div class="modal-heading">
        <h2 id="gallery-title-assignment-title">Title属性の登録</h2>
        <button class="gallery-title-assignment-close" title="閉じる" disabled={galleryTitleAssignment.isSaving} onclick={closeGalleryTitleAssignment}><X size={18} /></button>
      </div>
      <p class="gallery-title-assignment-summary">
        {galleryTitleAssignment.works.length} 件に登録します
      </p>

      <section class="gallery-title-assignment-section">
        <div class="gallery-title-assignment-section-heading">
          <h3>{galleryTitleAssignment.creators[0]}に登録済みのTitle</h3>
          <label class="gallery-title-assignment-filter">
            <Search size={16} />
            <input
              value={galleryTitleAssignment.creatorQuery}
              placeholder="Titleを絞り込む"
              aria-label="Creatorに登録済みのTitleを絞り込む"
              disabled={galleryTitleAssignment.isLoading || galleryTitleAssignment.isSaving}
              oninput={(event) => {
                if (galleryTitleAssignment) {
                  galleryTitleAssignment = { ...galleryTitleAssignment, creatorQuery: event.currentTarget.value };
                }
              }}
            />
          </label>
        </div>
        <div class="gallery-title-assignment-list gallery-title-assignment-list-creator" aria-label="Creatorが持っているTitle">
          {#if galleryTitleAssignment.isLoading}
            <span class="gallery-title-assignment-empty">読み込み中...</span>
          {:else if visibleGalleryTitleAssignmentCreatorTitles.length === 0}
            <span class="gallery-title-assignment-empty">該当するTitleはありません</span>
          {:else}
            {#each visibleGalleryTitleAssignmentCreatorTitles as option (option.id)}
              <button
                class:selected={galleryTitleAssignment.selectedTitleId === option.id}
                disabled={galleryTitleAssignment.isSaving}
                onclick={() => selectGalleryTitleAssignment(option.id)}
              >
                <span>{option.title}</span>
                <small>{option.workCount} 件</small>
              </button>
            {/each}
          {/if}
        </div>
      </section>

      <section class="gallery-title-assignment-section">
        <h3>登録済みのTitle</h3>
        <div class="gallery-title-assignment-filter-row">
          <label class="gallery-title-assignment-category-filter">
            <span>Category</span>
            <select
              value={galleryTitleAssignment.categoryFilter}
              aria-label="CategoryでTitleを絞り込む"
              disabled={galleryTitleAssignment.isLoading || galleryTitleAssignment.isSaving}
              onchange={(event) => {
                if (galleryTitleAssignment) {
                  galleryTitleAssignment = { ...galleryTitleAssignment, categoryFilter: event.currentTarget.value };
                }
              }}
            >
              <option value="">すべて</option>
              {#each galleryTitleAssignmentCategoryOptions as category}
                <option value={category}>{category}</option>
              {/each}
            </select>
          </label>
          <label class="gallery-title-assignment-filter">
            <Search size={16} />
            <input
              value={galleryTitleAssignment.query}
              placeholder="Titleを絞り込む"
              disabled={galleryTitleAssignment.isLoading || galleryTitleAssignment.isSaving}
              oninput={(event) => {
                if (galleryTitleAssignment) {
                  galleryTitleAssignment = { ...galleryTitleAssignment, query: event.currentTarget.value };
                }
              }}
            />
          </label>
        </div>
        <div class="gallery-title-assignment-list gallery-title-assignment-list-available" aria-label="登録済みのTitle">
          {#if galleryTitleAssignment.isLoading}
            <span class="gallery-title-assignment-empty">読み込み中...</span>
          {:else if visibleGalleryTitleAssignmentAvailableTitles.length === 0}
            <span class="gallery-title-assignment-empty">該当するTitleはありません</span>
          {:else}
            {#each visibleGalleryTitleAssignmentAvailableTitles as option (option.id)}
              <button
                class:selected={galleryTitleAssignment.selectedTitleId === option.id}
                disabled={galleryTitleAssignment.isSaving}
                onclick={() => selectGalleryTitleAssignment(option.id)}
              >
                <span>{option.title}</span>
                <small>{option.categoryName}</small>
              </button>
            {/each}
          {/if}
        </div>
        <button class="primary-button gallery-title-assignment-new" disabled={galleryTitleAssignment.isSaving} onclick={openGalleryTitleAssignmentNewTitle}>
          <Plus size={16} />
          <span>新規Titleの追加</span>
        </button>
      </section>

      <div class="modal-actions gallery-title-assignment-actions">
        <button
          class="primary-button"
          disabled={galleryTitleAssignment.isLoading || galleryTitleAssignment.isSaving || galleryTitleAssignment.selectedTitleId === null}
          onclick={applyGalleryTitleAssignment}
        >
          {galleryTitleAssignment.isSaving ? '登録中...' : '登録'}
        </button>
        <button class="quiet-button gallery-title-assignment-cancel" disabled={galleryTitleAssignment.isSaving} onclick={closeGalleryTitleAssignment}>キャンセル</button>
      </div>
      </dialog>
      {#if galleryTitleAssignment.characterPanelOpen}
        <aside class="gallery-title-assignment-character-panel" aria-label="Titleに紐づくCharacter">
          <div>
            <h2>Character属性の登録</h2>
            <p>選択したTitleに紐づくCharacterを同時に登録します</p>
          </div>
          <section class="gallery-title-assignment-section">
            <h3>{galleryTitleAssignment.creators[0] || 'Creator未設定'} / {selectedGalleryTitleAssignmentTitleName || 'Title未選択'}に登録済みのCharacter</h3>
            <label class="gallery-title-assignment-filter">
              <Search size={16} />
              <input
                value={galleryTitleAssignment.characterCreatorQuery}
                placeholder="Characterを絞り込む"
                aria-label="CreatorとTitleに登録済みのCharacterを絞り込む"
                disabled={galleryTitleAssignment.isCharacterLoading || galleryTitleAssignment.isSaving}
                oninput={(event) => {
                  if (galleryTitleAssignment) {
                    galleryTitleAssignment = { ...galleryTitleAssignment, characterCreatorQuery: event.currentTarget.value };
                  }
                }}
              />
            </label>
            <div class="gallery-title-assignment-list gallery-title-assignment-list-creator" aria-label="CreatorとTitleに登録済みのCharacter">
              {#if galleryTitleAssignment.isCharacterLoading}
                <span class="gallery-title-assignment-empty">読み込み中...</span>
              {:else if visibleGalleryTitleAssignmentCreatorTitleCharacters.length === 0}
                <span class="gallery-title-assignment-empty">該当するCharacterはありません</span>
              {:else}
                {#each visibleGalleryTitleAssignmentCreatorTitleCharacters as option (option.id)}
                  <button
                    class:selected={galleryTitleAssignment.selectedCharacterId === option.id}
                    disabled={galleryTitleAssignment.isSaving}
                    onclick={() => selectGalleryTitleAssignmentCharacter(option.id)}
                  >
                    <span>{option.character}</span>
                    <small>{option.workCount} 件</small>
                  </button>
                {/each}
              {/if}
            </div>
          </section>
          <section class="gallery-title-assignment-section">
            <h3>登録可能なCharacter</h3>
            <label class="gallery-title-assignment-filter">
              <Search size={16} />
              <input
                value={galleryTitleAssignment.characterQuery}
                placeholder="Characterを絞り込む"
                aria-label="登録可能なCharacterを絞り込む"
                disabled={galleryTitleAssignment.isCharacterLoading || galleryTitleAssignment.isSaving}
                oninput={(event) => {
                  if (galleryTitleAssignment) {
                    galleryTitleAssignment = { ...galleryTitleAssignment, characterQuery: event.currentTarget.value };
                  }
                }}
              />
            </label>
            <div class="gallery-title-assignment-list gallery-title-assignment-list-available" aria-label="登録可能なCharacter">
              {#if galleryTitleAssignment.isCharacterLoading}
                <span class="gallery-title-assignment-empty">読み込み中...</span>
              {:else if visibleGalleryTitleAssignmentAvailableCharacters.length === 0}
                <span class="gallery-title-assignment-empty">該当するCharacterはありません</span>
              {:else}
                {#each visibleGalleryTitleAssignmentAvailableCharacters as option (option.id)}
                  <button
                    class:selected={galleryTitleAssignment.selectedCharacterId === option.id}
                    disabled={galleryTitleAssignment.isSaving}
                    onclick={() => selectGalleryTitleAssignmentCharacter(option.id)}
                  >
                    <span>{option.character}</span>
                    <small>{option.title}</small>
                  </button>
                {/each}
              {/if}
            </div>
          </section>
        </aside>
      {/if}
    </div>
  </div>
{/if}

{#if galleryCharacterAssignment && activeView === 'library'}
  <div class="modal-backdrop gallery-title-assignment-backdrop" role="presentation">
    <div class:panel-open={galleryCharacterAssignment.assignedCharactersPanelOpen} class="gallery-title-assignment-shell">
      {#if galleryCharacterAssignment.assignedCharactersPanelOpen}
        <aside class="gallery-title-assignment-current-panel" aria-label="作品に登録済みのCharacter">
          <div>
            <h2>作品に登録済みのCharacter</h2>
            <p>選択したすべての作品に共通するCharacterを表示します</p>
          </div>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-current">
            {#if galleryCharacterAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if galleryCharacterAssignment.commonAssignedCharacters.length === 0}
              <span class="gallery-title-assignment-empty">共通するCharacter属性はありません</span>
            {:else}
              {#each galleryCharacterAssignment.commonAssignedCharacters as option (option.id)}
                <button
                  class:selected={galleryCharacterAssignment.selectedAssignedCharacterIds.includes(option.id)}
                  disabled={galleryCharacterAssignment.isSaving}
                  onclick={() => toggleGalleryAssignedCharacterSelection(option.id)}
                >
                  <span>{option.character}</span>
                  <small>{option.title}</small>
                </button>
              {/each}
            {/if}
          </div>
          <div class="gallery-title-assignment-remove-flow" aria-hidden="true">
            <ChevronDown size={18} />
          </div>
          <button
            class="danger-button gallery-title-assignment-remove"
            disabled={galleryCharacterAssignment.isLoading || galleryCharacterAssignment.isSaving || galleryCharacterAssignment.selectedAssignedCharacterIds.length === 0}
            onclick={removeSelectedGalleryCharacters}
          >
            選択したCharacter属性を解除
          </button>
        </aside>
      {/if}
      <button
        type="button"
        class="gallery-title-assignment-panel-toggle"
        class:panel-open={galleryCharacterAssignment.assignedCharactersPanelOpen}
        aria-label={galleryCharacterAssignment.assignedCharactersPanelOpen ? '登録済みCharacterを閉じる' : '登録済みCharacterを開く'}
        aria-expanded={galleryCharacterAssignment.assignedCharactersPanelOpen}
        onclick={() => {
          if (galleryCharacterAssignment) {
            galleryCharacterAssignment = {
              ...galleryCharacterAssignment,
              assignedCharactersPanelOpen: !galleryCharacterAssignment.assignedCharactersPanelOpen
            };
          }
        }}
      >
        {#if galleryCharacterAssignment.assignedCharactersPanelOpen}<ChevronRight size={20} />{:else}<ChevronLeft size={20} />{/if}
      </button>
      <dialog
        open
        class="modal gallery-title-assignment-modal"
        aria-labelledby="gallery-character-assignment-title"
        onkeydown={(event) => {
          if (event.key === 'Escape') {
            event.preventDefault();
            closeGalleryCharacterAssignment();
          }
        }}
      >
        <div class="modal-heading">
          <h2 id="gallery-character-assignment-title">Character属性の登録</h2>
          <button class="gallery-title-assignment-close" title="閉じる" disabled={galleryCharacterAssignment.isSaving} onclick={closeGalleryCharacterAssignment}><X size={18} /></button>
        </div>
        <p class="gallery-title-assignment-summary">
          {galleryCharacterAssignment.works.length} 件に登録します
        </p>

        <section class="gallery-title-assignment-section">
          <h3 title={getGalleryCharacterAssignmentScopeLabel(galleryCharacterAssignment)}>{getGalleryCharacterAssignmentScopeLabel(galleryCharacterAssignment)}に登録済みのCharacter</h3>
          <label class="gallery-title-assignment-filter">
            <Search size={16} />
            <input
              value={galleryCharacterAssignment.creatorQuery}
              placeholder="Characterを絞り込む"
              aria-label="CreatorとTitleに登録済みのCharacterを絞り込む"
              disabled={galleryCharacterAssignment.isLoading || galleryCharacterAssignment.isSaving}
              oninput={(event) => {
                if (galleryCharacterAssignment) {
                  galleryCharacterAssignment = { ...galleryCharacterAssignment, creatorQuery: event.currentTarget.value };
                }
              }}
            />
          </label>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-creator" aria-label="CreatorとTitleに登録済みのCharacter">
            {#if galleryCharacterAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if visibleGalleryCharacterAssignmentCreatorTitleCharacters.length === 0}
              <span class="gallery-title-assignment-empty">該当するCharacterはありません</span>
            {:else}
              {#each visibleGalleryCharacterAssignmentCreatorTitleCharacters as option (option.id)}
                <button
                  class:selected={galleryCharacterAssignment.selectedCharacterId === option.id}
                  disabled={galleryCharacterAssignment.isSaving}
                  onclick={() => selectGalleryCharacterAssignment(option.id)}
                >
                  <span>{option.character}</span>
                  <small>{option.workCount} 件</small>
                </button>
              {/each}
            {/if}
          </div>
        </section>

        <section class="gallery-title-assignment-section">
          <h3>登録可能なCharacter</h3>
          <label class="gallery-title-assignment-filter">
            <Search size={16} />
            <input
              value={galleryCharacterAssignment.query}
              placeholder="Characterを絞り込む"
              aria-label="登録可能なCharacterを絞り込む"
              disabled={galleryCharacterAssignment.isLoading || galleryCharacterAssignment.isSaving}
              oninput={(event) => {
                if (galleryCharacterAssignment) {
                  galleryCharacterAssignment = { ...galleryCharacterAssignment, query: event.currentTarget.value };
                }
              }}
            />
          </label>
          <div class="gallery-title-assignment-list gallery-title-assignment-list-available" aria-label="登録可能なCharacter">
            {#if galleryCharacterAssignment.isLoading}
              <span class="gallery-title-assignment-empty">読み込み中...</span>
            {:else if visibleGalleryCharacterAssignmentAvailableCharacters.length === 0}
              <span class="gallery-title-assignment-empty">該当するCharacterはありません</span>
            {:else}
              {#each visibleGalleryCharacterAssignmentAvailableCharacters as option (option.id)}
                <button
                  class:selected={galleryCharacterAssignment.selectedCharacterId === option.id}
                  disabled={galleryCharacterAssignment.isSaving}
                  onclick={() => selectGalleryCharacterAssignment(option.id)}
                >
                  <span>{option.character}</span>
                  <small>{option.title}</small>
                </button>
              {/each}
            {/if}
          </div>
          <button class="primary-button gallery-title-assignment-new" disabled={galleryCharacterAssignment.isSaving} onclick={openGalleryCharacterAssignmentNewCharacter}>
            <Plus size={16} />
            <span>新規Characterの追加</span>
          </button>
        </section>

        <div class="modal-actions gallery-title-assignment-actions">
          <button
            class="primary-button"
            disabled={galleryCharacterAssignment.isLoading || galleryCharacterAssignment.isSaving || galleryCharacterAssignment.selectedCharacterId === null}
            onclick={applyGalleryCharacterAssignment}
          >
            {galleryCharacterAssignment.isSaving ? '登録中...' : '登録'}
          </button>
          <button class="quiet-button gallery-title-assignment-cancel" disabled={galleryCharacterAssignment.isSaving} onclick={closeGalleryCharacterAssignment}>キャンセル</button>
        </div>
      </dialog>
    </div>
  </div>
{/if}

{#if galleryContextMenu}
  <button
    class="gallery-context-menu-backdrop"
    aria-label="Galleryの操作メニューを閉じる"
    onpointerdown={(event) => {
      if (event.button === 0) {
        event.preventDefault();
        event.stopPropagation();
        closeGalleryContextMenu();
      }
    }}
  ></button>
  <div
    class="gallery-context-menu"
    style={`left: ${galleryContextMenu.x}px; top: ${galleryContextMenu.y}px;`}
    role="menu"
    tabindex="-1"
    aria-label="Galleryの操作"
    oncontextmenu={(event) => event.preventDefault()}
  >
    <div class="gallery-context-menu-heading" aria-hidden="true">作品の操作</div>
    <div class="gallery-context-submenu-root">
      <button role="menuitem" aria-haspopup="menu">
        <Search size={16} />
        <span>逆引きフィルタ</span>
        <ChevronRight class="context-submenu-arrow" size={15} />
      </button>
      <div class="gallery-context-submenu" role="menu" aria-label="逆引きフィルタ">
        <button
          role="menuitem"
          onpointerdown={(event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            applyGalleryReverseCreator();
          }}
        >
          <UserRound size={16} />
          <span>作品のCreator</span>
        </button>
        <button
          role="menuitem"
          onpointerdown={(event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            requestGalleryReverseFilters('title');
          }}
        >
          <BookOpenText size={16} />
          <span>作品のTitle</span>
        </button>
        <button
          role="menuitem"
          onpointerdown={(event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            requestGalleryReverseFilters('character');
          }}
        >
          <ContactRound size={16} />
          <span>作品のCharacter</span>
        </button>
      </div>
    </div>
    <div class="gallery-context-submenu-root">
      <button role="menuitem" aria-haspopup="menu">
        <ListFilter size={16} />
        <span>フィルターの登録と解除</span>
        <ChevronRight class="context-submenu-arrow" size={15} />
      </button>
      <div class="gallery-context-submenu" role="menu" aria-label="フィルターの登録と解除">
        <button
          role="menuitem"
          onpointerdown={(event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            const work = galleryContextTargetWork ?? galleryContextMenu?.work ?? null;
            if (!work) {
              showExplorerToast('Title対象を取得できませんでした。', 'error');
              return;
            }
            openGalleryTitleAssignment(work);
          }}
        >
          <BookOpenText size={16} />
          <span>Title属性</span>
        </button>
        <button
          role="menuitem"
          onpointerdown={(event) => {
            if (event.button !== 0) return;
            event.preventDefault();
            event.stopPropagation();
            const work = galleryContextTargetWork ?? galleryContextMenu?.work ?? null;
            if (!work) {
              showExplorerToast('Character対象を取得できませんでした。', 'error');
              return;
            }
            openGalleryCharacterAssignment(work);
          }}
        >
          <UserRound size={16} />
          <span>Character属性</span>
        </button>
      </div>
    </div>
    <button
      role="menuitem"
      onpointerdown={(event) => {
        if (event.button !== 0) return;
        event.preventDefault();
        event.stopPropagation();
        const work = galleryContextTargetWork ?? galleryContextMenu?.work ?? null;
        if (!work) {
          showExplorerToast('Tag対象を取得できませんでした。', 'error');
          return;
        }
        openGalleryTagAssignment(work);
      }}
    >
      <Tags size={16} />
      <span>タグの登録と解除</span>
    </button>
    <button class="context-danger" role="menuitem" onpointerdown={(event) => {
      if (event.button === 0) {
        event.preventDefault();
        event.stopPropagation();
        requestGalleryDelete();
      }
    }}>
      <Trash2 size={16} />
      <span>ファイルを削除</span>
    </button>
  </div>
{/if}

{#if explorerTabContextMenu}
  <div
    class="explorer-context-menu explorer-tab-context-menu"
    style={`left: ${explorerTabContextMenu.x}px; top: ${explorerTabContextMenu.y}px;`}
    role="menu"
    tabindex="-1"
    aria-label={`${explorerTabContextMenu.creator}の移動先`}
    onpointerdown={(event) => event.stopPropagation()}
    oncontextmenu={(event) => event.preventDefault()}
  >
    <button role="menuitem" onclick={navigateExplorerCreatorTabToGallery}><LayoutGrid size={16} /><span>Galleryへ移動</span></button>
    <button role="menuitem" onclick={navigateExplorerCreatorTabToTracking}><UserRound size={16} /><span>Creator Trackingへ移動</span></button>
    <button role="menuitem" onclick={splitExplorerFromTabContextMenu}><Columns2 size={16} /><span>分割表示</span></button>
  </div>
{/if}

{#if explorerContextMenu}
  <div
    class="explorer-context-menu"
    style={`left: ${explorerContextMenu.x}px; top: ${explorerContextMenu.y}px;`}
    role="menu"
    tabindex="-1"
    aria-label={explorerContextMenu.entry.name}
    onpointerdown={(event) => event.stopPropagation()}
    oncontextmenu={(event) => event.preventDefault()}
  >
    <div
      class="explorer-context-submenu-root"
      role="presentation"
      onpointerenter={() => (explorerFolderCreateSubmenuOpen = true)}
      onpointerleave={() => (explorerFolderCreateSubmenuOpen = false)}
    >
      <button
        role="menuitem"
        aria-haspopup="menu"
        aria-expanded={explorerFolderCreateSubmenuOpen}
        onclick={() => (explorerFolderCreateSubmenuOpen = !explorerFolderCreateSubmenuOpen)}
      >
        <FolderPlus size={16} />
        <span>新規フォルダを作成して移動</span>
        <ArrowRight class="context-submenu-arrow" size={15} />
      </button>
      {#if explorerFolderCreateSubmenuOpen}
        <div class="explorer-context-submenu" role="menu" aria-label="新規フォルダを作成して移動">
          <button role="menuitem" onclick={() => organizeExplorerSelection(explorerContextMenu?.pane ?? 'left', 'aggregate')}>
            <FolderPlus size={16} />
            <span>1フォルダに集約</span>
          </button>
          <button role="menuitem" onclick={() => organizeExplorerSelection(explorerContextMenu?.pane ?? 'left', 'separate')}>
            <FolderPlus size={16} />
            <span>個別フォルダへ分散</span>
          </button>
        </div>
      {/if}
    </div>
    {#if explorerContextMenu.entry.isDirectory}
      <button role="menuitem" onclick={openContextMenuEntryInNewTab}>
        <Plus size={16} />
        <span>新規タブで開く</span>
      </button>
      <div
        class="explorer-context-submenu-root"
        role="presentation"
        onpointerenter={() => (explorerDbManagementSubmenuOpen = true)}
        onpointerleave={() => {
          explorerDbManagementSubmenuOpen = false;
          explorerGidSubmenuOpen = false;
        }}
      >
        <button
          role="menuitem"
          aria-haspopup="menu"
          aria-expanded={explorerDbManagementSubmenuOpen}
          onclick={() => (explorerDbManagementSubmenuOpen = !explorerDbManagementSubmenuOpen)}
        >
          <Wrench size={16} />
          <span>DB管理機能</span>
          <ArrowRight class="context-submenu-arrow" size={15} />
        </button>
        {#if explorerDbManagementSubmenuOpen}
          <div class="explorer-context-submenu explorer-db-management-submenu" role="menu" aria-label="DB管理機能">
            <div
              class="explorer-context-submenu-root"
              role="presentation"
              onpointerenter={() => (explorerGidSubmenuOpen = true)}
              onpointerleave={() => (explorerGidSubmenuOpen = false)}
            >
              <button
                role="menuitem"
                aria-haspopup="menu"
                aria-expanded={explorerGidSubmenuOpen}
                disabled={gidTargetExtensions.length === 0}
                onclick={() => (explorerGidSubmenuOpen = !explorerGidSubmenuOpen)}
              >
                <Hash size={16} />
                <span>gid発行</span>
                <ArrowRight class="context-submenu-arrow" size={15} />
              </button>
              {#if explorerGidSubmenuOpen}
                <div class="explorer-context-submenu explorer-gid-submenu" role="menu" aria-label="gid発番対象拡張子">
                  <button role="menuitem" onclick={() => requestGidAssignment(gidTargetExtensions)}>
                    <Layers3 size={16} />
                    <span>Everything Bellow</span>
                  </button>
                  <div class="explorer-context-menu-separator" role="separator"></div>
                  {#each gidTargetExtensions as extension}
                    <button role="menuitem" onclick={() => requestGidAssignment([extension])}>
                      <File size={16} />
                      <span>{extension}</span>
                    </button>
                  {/each}
                </div>
              {/if}
            </div>
            <button role="menuitem" onclick={requestCreatorFolderConversion}>
              <UserRound size={16} />
              <span>作者フォルダ化</span>
            </button>
            <button role="menuitem" onclick={requestCreatorReassignment}>
              <UsersRound size={16} />
              <span>作者情報の付け替え</span>
            </button>
          </div>
        {/if}
      </div>
      <div
        class="explorer-context-submenu-root"
        role="presentation"
        onpointerenter={() => (explorerCompressionSubmenuOpen = true)}
        onpointerleave={() => (explorerCompressionSubmenuOpen = false)}
      >
        <button
          role="menuitem"
          aria-haspopup="menu"
          aria-expanded={explorerCompressionSubmenuOpen}
          onclick={() => (explorerCompressionSubmenuOpen = !explorerCompressionSubmenuOpen)}
        >
          <Archive size={16} />
          <span>圧縮と処理後削除</span>
          <ArrowRight class="context-submenu-arrow" size={15} />
        </button>
        {#if explorerCompressionSubmenuOpen}
          <div class="explorer-context-submenu" role="menu" aria-label="圧縮と処理後削除">
            <button role="menuitem" onclick={requestIndividualWinRarCompression}>
              <Archive size={16} />
              <span>個別に圧縮</span>
            </button>
            {#if getContextFolderCompressionTargetCount() > 1}
              <button role="menuitem" onclick={requestWinRarPackageCompression}>
                <Archive size={16} />
                <span>1 パッケージ化</span>
              </button>
            {/if}
          </div>
        {/if}
      </div>
      <div
        class="explorer-context-submenu-root"
        role="group"
        aria-label="サムネイル設定"
        onmouseenter={() => (explorerThumbnailSubmenuOpen = true)}
        onmouseleave={() => (explorerThumbnailSubmenuOpen = false)}
      >
        <button
          role="menuitem"
          aria-haspopup="menu"
          aria-expanded={explorerThumbnailSubmenuOpen}
          onclick={(event) => event.preventDefault()}
        >
          <FolderOpen size={16} />
          <span>サムネイル設定</span>
          <ArrowRight class="context-submenu-arrow" size={15} />
        </button>
        {#if explorerThumbnailSubmenuOpen}
          <div class="explorer-context-submenu" role="menu" aria-label="サムネイル設定">
            <button role="menuitem" onclick={setContextEntryAsParentThumbnail}>
              <FolderOpen size={16} />
              <span>親フォルダのサムネイルとして設定する</span>
            </button>
            <button class="context-danger" role="menuitem" onclick={deleteContextFolderThumbnail}>
              <Trash2 size={16} />
              <span>サムネイルを削除する</span>
            </button>
          </div>
        {/if}
      </div>
    {/if}
    {#if !explorerContextMenu.entry.isDirectory}
      <button role="menuitem" onclick={setContextEntryAsParentThumbnail}>
        <FolderOpen size={16} />
        <span>親フォルダのサムネイルとして設定する</span>
      </button>
    {/if}
    {#if isRarArchive(explorerContextMenu.entry)}
      <button role="menuitem" onclick={convertContextRarToZip}>
        <RefreshCw size={16} />
        <span>{getContextRarConversionTargets().length > 1 ? `${getContextRarConversionTargets().length}件をzipに変換` : 'zipに変換'}</span>
      </button>
    {/if}
    {#if isWinRarArchive(explorerContextMenu.entry)}
      <button role="menuitem" onclick={extractContextArchiveWithWinRar}>
        <Archive size={16} />
        <span>WinRARで解凍</span>
      </button>
    {/if}
    {#if winRarSettings.showOpenInContextMenu && (explorerContextMenu.entry.isDirectory || isWinRarArchive(explorerContextMenu.entry))}
      <button role="menuitem" onclick={openContextEntryWithWinRar}>
        <Archive size={16} />
        <span>WinRARで開く</span>
      </button>
    {/if}
    {#each getContextAppRules(explorerContextMenu.entry) as rule}
      <button role="menuitem" onclick={() => openEntryWithProgram(rule, explorerContextMenu.entry)}>
        <ExternalLink size={16} />
        <span>{rule.name}</span>
      </button>
    {/each}
  </div>
{/if}

{#if explorerBlankContextMenu}
  <div
    class="explorer-context-menu explorer-blank-context-menu"
    style={`left: ${explorerBlankContextMenu.x}px; top: ${explorerBlankContextMenu.y}px;`}
    role="menu"
    tabindex="-1"
    aria-label="フォルダ操作"
    onpointerdown={(event) => event.stopPropagation()}
    oncontextmenu={(event) => event.preventDefault()}
  >
    <button role="menuitem" onclick={() => createNewExplorerFolder(explorerBlankContextMenu?.pane ?? 'left')}>
      <FolderPlus size={16} />
      <span>新規フォルダを作成</span>
    </button>
  </div>
{/if}

{#if explorerColumnMenu}
  <div
    class="explorer-columns-menu"
    style={`left: ${explorerColumnMenu.x}px; top: ${explorerColumnMenu.y}px;`}
    role="menu"
    tabindex="-1"
    aria-label="表示列の設定"
    onpointerdown={(event) => event.stopPropagation()}
    oncontextmenu={(event) => event.preventDefault()}
  >
    {#each explorerDetailColumnDefinitions as column}
      <button
        role="menuitemcheckbox"
        aria-checked={explorerDetailColumns.includes(column.id)}
        onclick={() => toggleExplorerDetailColumn(column.id)}
      >
        <span class:checked={explorerDetailColumns.includes(column.id)} class="column-check"><Check size={16} /></span>
        <span>{column.label}</span>
      </button>
    {/each}
  </div>
{/if}

{#if gestureTrail.length > 1}
  <svg
    class="gesture-trail"
    aria-hidden="true"
    style={`--gesture-color: ${mouseGestureSettings.lineColor}; --gesture-width: ${mouseGestureSettings.lineWidth}px;`}
  >
    <polyline points={gestureTrailPoints} />
    {#each gestureTrail.slice(-1) as point}
      <circle cx={point.x} cy={point.y} r="5" />
    {/each}
  </svg>
{/if}

{#if explorerToastMessage}
  <div class:toast-error={explorerToastKind === 'error'} class:toast-progress={explorerToastKind === 'progress'} class="explorer-toast" role="status" aria-live="polite">
    {#if explorerToastKind === 'success'}
      <Check size={17} />
    {:else if explorerToastKind === 'progress'}
      <RefreshCw class="explorer-toast-spinner" size={17} />
    {:else}
      <X size={17} />
    {/if}
    <span>{explorerToastMessage}</span>
    {#if explorerToastAction === 'cancelDatabaseScan'}
      <button
        type="button"
        class="explorer-toast-action"
        onclick={cancelSqliteDatabaseUpdate}
        disabled={sqliteDatabaseCancelRequested}>
        {sqliteDatabaseCancelRequested ? '中断中...' : '安全に中断'}
      </button>
    {/if}
  </div>
{/if}

{#if winRarProgressLabel}
  <div class="explorer-progress" role="status" aria-live="polite">
    <Archive size={17} />
    <div>
      <strong>{winRarProgressLabel}</strong>
      <span>経過時間 {winRarProgressSeconds} 秒</span>
      <i aria-hidden="true"></i>
    </div>
  </div>
{/if}

{#if creatorTrackingNewDialogOpen}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal creator-tracking-new-modal"
      aria-labelledby="creator-tracking-new-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') creatorTrackingNewDialogOpen = false;
        if (event.key === 'Enter') {
          event.preventDefault();
          createNewCreatorTracking();
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="creator-tracking-new-title">Creator Trackingを新規作成</h2>
        <button title="閉じる" onclick={() => (creatorTrackingNewDialogOpen = false)}><X size={18} /></button>
      </div>
      <p>Galleryに作品がないCreatorも、起票して管理できます。</p>
      <label class="rename-field rename-name-field">
        <span>Creator名</span>
        <input
          class="modal-input"
          bind:value={creatorTrackingNewCreator}
          maxlength="160"
          placeholder="例：creator_name"
          aria-label="Creator名"
          use:focusAtEnd
        />
      </label>
      <label class="rename-field">
        <span>区分</span>
        <select class="modal-input" bind:value={creatorTrackingNewCategory} aria-label="区分">
          {#each gallerySections as section}
            <option value={section.id}>{section.label}</option>
          {/each}
        </select>
      </label>
      <p class="gid-assignment-note">作品数ゼロのテンプレートとして作成し、活動場所・ストレージ・課金情報を後から入力できます。</p>
      <div class="modal-actions">
        <button class="primary-button" disabled={!creatorTrackingNewCreator.trim()} onclick={createNewCreatorTracking}><UserPlus size={16} /> 作成</button>
        <button class="quiet-button" onclick={() => (creatorTrackingNewDialogOpen = false)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if creatorTrackingDeleteStep > 0 && creatorTrackingDeleteTarget}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal delete-modal creator-tracking-delete-modal"
      aria-labelledby="creator-tracking-delete-title"
      onkeydown={(event) => {
        if (event.key === 'Escape' && !creatorTrackingDeleteInProgress) {
          cancelCreatorTrackingDelete();
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="creator-tracking-delete-title">
          {creatorTrackingDeleteStep === 1 ? '作者データを削除しますか？' : '最終確認：本当に削除しますか？'}
        </h2>
        <button title="閉じる" disabled={creatorTrackingDeleteInProgress} onclick={cancelCreatorTrackingDelete}><X size={18} /></button>
      </div>
      {#if creatorTrackingDeleteStep === 1}
        <p>Creator「{creatorTrackingDeleteTarget.label}」のDB上の作者データを削除します。</p>
        <div class="creator-tracking-delete-summary">
          <span>削除対象</span>
          <ul>
            <li>Creator Trackingのデータ</li>
            <li>Creatorが「{creatorTrackingDeleteTarget.creator}」になっている作品のDBデータ</li>
            <li>関連するDBキャッシュとCreator Tracking付箋</li>
          </ul>
        </div>
        <p class="gid-assignment-note">ファイルやフォルダ本体は削除・移動・リネームしません。</p>
        <div class="modal-actions">
          <button class="danger-button" onclick={advanceCreatorTrackingDeleteConfirmation}><TriangleAlert size={16} /> 次の確認へ</button>
          <button class="quiet-button" onclick={cancelCreatorTrackingDelete}>キャンセル</button>
        </div>
      {:else}
        <p>この操作はDBから作者情報と作品登録を削除します。ファイル本体は残りますが、DB上の関連付けは消えます。</p>
        <div class="creator-tracking-delete-final-name">{creatorTrackingDeleteTarget.creator}</div>
        <div class="modal-actions">
          <button class="danger-button" disabled={creatorTrackingDeleteInProgress} onclick={executeCreatorTrackingDelete}>
            {#if creatorTrackingDeleteInProgress}
              削除中...
            {:else}
              <Trash2 size={16} /> 作者データを削除
            {/if}
          </button>
          <button class="quiet-button" disabled={creatorTrackingDeleteInProgress} onclick={cancelCreatorTrackingDelete}>キャンセル</button>
        </div>
      {/if}
    </dialog>
  </div>
{/if}

{#if bookmarkSaveDialogOpen && bookmarkCapture}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal bookmark-dialog"
      aria-labelledby="bookmark-save-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') bookmarkSaveDialogOpen = false;
        if (event.key === 'Enter') {
          event.preventDefault();
          saveViewBookmark();
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="bookmark-save-title">現在のビューをBookmark</h2>
        <button title="閉じる" onclick={() => (bookmarkSaveDialogOpen = false)}><X size={18} /></button>
      </div>
      <p>{bookmarkCapture.viewLabel} の現在の状態を保存します。</p>
      <input class="modal-input bookmark-name-input" bind:value={bookmarkNameDraft} maxlength="120" aria-label="Bookmark名" use:focusAtEnd />
      <div class="modal-actions">
        <button class="primary-button" disabled={!bookmarkNameDraft.trim()} onclick={saveViewBookmark}><Bookmark size={16} /> 保存</button>
        <button class="quiet-button" onclick={() => (bookmarkSaveDialogOpen = false)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if bookmarkDeleteCandidate}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal delete-modal bookmark-dialog" aria-labelledby="bookmark-delete-title">
      <div class="modal-heading">
        <h2 id="bookmark-delete-title">Bookmarkを削除しますか？</h2>
        <button title="閉じる" onclick={() => (bookmarkDeleteCandidate = null)}><X size={18} /></button>
      </div>
      <p>「{bookmarkDeleteCandidate.name}」を削除します。保存元のファイルや設定には影響しません。</p>
      <div class="modal-actions">
        <button class="danger-button" onclick={deleteViewBookmark}>削除</button>
        <button class="quiet-button" onclick={() => (bookmarkDeleteCandidate = null)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if bookmarkRestoreWarnings.length > 0}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal bookmark-warning-dialog" aria-labelledby="bookmark-warning-title">
      <div class="modal-heading">
        <h2 id="bookmark-warning-title">一部の状態を復元できませんでした</h2>
        <button title="閉じる" onclick={() => (bookmarkRestoreWarnings = [])}><X size={18} /></button>
      </div>
      <p>復元できる状態は適用しました。次の項目を確認してください。</p>
      <ul>
        {#each bookmarkRestoreWarnings as warning}
          <li>{warning}</li>
        {/each}
      </ul>
      <div class="modal-actions">
        <button class="primary-button" onclick={() => (bookmarkRestoreWarnings = [])}>確認</button>
      </div>
    </dialog>
  </div>
{/if}

{#if pendingFilterEditorNavigation}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal"
      aria-labelledby="filter-editor-navigation-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') {
          event.preventDefault();
          cancelFilterEditorNavigation();
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="filter-editor-navigation-title">変更をコミットしますか？</h2>
        <button title="キャンセル" disabled={filterEditorNavigationCommitInProgress} onclick={cancelFilterEditorNavigation}><X size={18} /></button>
      </div>
      <p>編集中のアイテムに未保存の変更があります。はいを選ぶと更新してから画面を移動します。</p>
      <div class="modal-actions">
        <button class="primary-button" disabled={filterEditorNavigationCommitInProgress} onclick={commitFilterEditorChangesAndNavigate}>{filterEditorNavigationCommitInProgress ? '更新中...' : 'はい'}</button>
        <button class="quiet-button" disabled={filterEditorNavigationCommitInProgress} onclick={discardFilterEditorChangesAndNavigate}>いいえ</button>
        <button class="quiet-button" disabled={filterEditorNavigationCommitInProgress} onclick={cancelFilterEditorNavigation}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if pendingFilterEditorDefinitionDeletion}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal delete-modal"
      aria-labelledby="filter-editor-delete-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') {
          event.preventDefault();
          pendingFilterEditorDefinitionDeletion = null;
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="filter-editor-delete-title">{pendingFilterEditorDefinitionDeletion.filterType === 'title' ? 'Title' : 'Character'} を削除しますか？</h2>
        <button title="閉じる" onclick={() => (pendingFilterEditorDefinitionDeletion = null)}><X size={18} /></button>
      </div>
      <p>
        「{pendingFilterEditorDefinitionDeletion.canonicalName}」を Gallery から非表示にします。
        {#if pendingFilterEditorDefinitionDeletion.filterType === 'title'}従属する Character も非表示になります。{/if}
        登録済みのデータは保持され、同じ組み合わせを再登録すると復帰します。
      </p>
      <div class="modal-actions">
        <button class="danger-button" onclick={deleteFilterEditorDefinition}>削除</button>
        <button class="quiet-button" onclick={() => (pendingFilterEditorDefinitionDeletion = null)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if pendingWinRarIndividualCompression}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal delete-modal"
      aria-labelledby="winrar-individual-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') {
          event.preventDefault();
          pendingWinRarIndividualCompression = null;
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="winrar-individual-title">個別に圧縮しますか？</h2>
        <button title="閉じる" onclick={() => (pendingWinRarIndividualCompression = null)}><X size={18} /></button>
      </div>
      <p>{pendingWinRarIndividualCompression.folderPaths.length} 個のフォルダを、それぞれ ZIP に圧縮して元フォルダを削除します。この操作は元に戻せません。</p>
      <div class="modal-actions">
        <button class="danger-button" onclick={executeIndividualWinRarCompression}>圧縮して削除</button>
        <button class="quiet-button" onclick={() => (pendingWinRarIndividualCompression = null)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if pendingWinRarPackageCompression}
  <div class="modal-backdrop" role="presentation">
    <dialog
      open
      class="modal rename-modal"
      aria-labelledby="winrar-package-title"
      onkeydown={(event) => {
        if (event.key === 'Escape') {
          event.preventDefault();
          pendingWinRarPackageCompression = null;
        }
      }}
    >
      <div class="modal-heading">
        <h2 id="winrar-package-title">1 パッケージ化</h2>
        <button title="閉じる" onclick={() => (pendingWinRarPackageCompression = null)}><X size={18} /></button>
      </div>
      <p>{pendingWinRarPackageCompression.folderPaths.length} 個のフォルダを 1 つの ZIP に圧縮して、元フォルダを削除します。</p>
      <label class="rename-field rename-name-field">
        <span>書庫名</span>
        <input
          class="modal-input"
          bind:value={winRarPackageName}
          maxlength="180"
          use:focusAtEnd
          onkeydown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              executeWinRarPackageCompression();
            }
          }}
        />
      </label>
      <div class="rename-field">
        <span>拡張子</span>
        <code>.zip</code>
      </div>
      <div class="modal-actions">
        <button class="primary-button" onclick={executeWinRarPackageCompression}>圧縮して削除</button>
        <button class="quiet-button" onclick={() => (pendingWinRarPackageCompression = null)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if renamingEntry}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal" aria-labelledby="rename-title" onkeydown={(event) => handleRenameDialogKeydown(event, 'left')}>
      <div class="modal-heading">
        <h2 id="rename-title">名前の変更</h2>
        <button title="閉じる" onclick={() => (renamingEntry = null)}><X size={18} /></button>
      </div>
      <label class="rename-field rename-name-field">
        <span>名前</span>
        <input
          class="modal-input"
          bind:value={renameValue}
          maxlength={renameMaximumLength}
          use:focusAtEnd
          onkeydown={(event) => handleRenameInputKeydown(event, 'left')}
        />
      </label>
      <div class="rename-field">
        <span>gid</span>
        <code>{renameIdentifier || '未設定'}</code>
      </div>
      <div class="rename-field">
        <span>拡張子</span>
        <code>{renameExtension || 'なし'}</code>
      </div>
      <div class="modal-actions">
        <button class="primary-button" onclick={saveRename}>変更</button>
        <button class="quiet-button" onclick={() => (renamingEntry = null)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if galleryDeleteConfirmation}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal delete-modal" aria-labelledby="gallery-delete-title" onkeydown={(event) => {
      if (event.key === 'Escape' && !galleryDeleteInProgress) {
        event.preventDefault();
        closeGalleryDeleteConfirmation();
      }
    }}>
      <div class="modal-heading">
        <h2 id="gallery-delete-title">{selectedGalleryWorkIds.size} 件を削除しますか？</h2>
        <button type="button" class="gallery-delete-close" title="閉じる" disabled={galleryDeleteInProgress} onclick={closeGalleryDeleteConfirmation}><X size={18} /></button>
      </div>
      <p>選択したファイル本体と、Gallery用SQLiteDBの作品情報を削除します。この操作は元に戻せません。</p>
      <div class="modal-actions">
        <button type="button" class="danger-button" disabled={galleryDeleteInProgress} onclick={deleteGallerySelection}>{galleryDeleteInProgress ? '削除中...' : '削除'}</button>
        <button type="button" class="quiet-button gallery-delete-cancel" disabled={galleryDeleteInProgress} onclick={closeGalleryDeleteConfirmation}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if gidMigrationPreview}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal gid-migration-modal" aria-labelledby="gid-migration-title" onkeydown={(event) => {
      if (event.key === 'Escape' && !gidMigrationInProgress) {
        event.preventDefault();
        closeGidMigrationPreview();
      }
    }}>
      <div class="modal-heading">
        <h2 id="gid-migration-title">既存GIDを{gidMigrationPreview.targetDigitCount}桁へ移行しますか？</h2>
        <button title="閉じる" disabled={gidMigrationInProgress} onclick={closeGidMigrationPreview}><X size={18} /></button>
      </div>
      <div class="gid-migration-summary">
        <div><span>DB作品</span><strong>{gidMigrationPreview.itemCount.toLocaleString()}件</strong></div>
        <div><span>ファイル名変更</span><strong>{gidMigrationPreview.fileRenameCount.toLocaleString()}件</strong></div>
        <div><span>DB内のみ変更</span><strong>{gidMigrationPreview.databaseOnlyCount.toLocaleString()}件</strong></div>
        <div><span>DBから抹消</span><strong>{gidMigrationPreview.missingFileCount.toLocaleString()}件</strong></div>
      </div>
      <p class="gid-migration-warning">開始前にGallery本体DBをバックアップします。処理中はファイル操作やアプリ終了を行わないでください。</p>
      {#if gidMigrationPreview.missingFileCount > 0}
        <p class="gid-migration-delete-note">実体が見つからない{gidMigrationPreview.missingFileCount.toLocaleString()}件は、削除済みの作品としてバックアップ後に関連履歴を含めDBから抹消します。</p>
      {/if}
      {#if gidMigrationPreview.issues.length > 0}
        <ul class:gid-migration-delete-list={gidMigrationPreview.canExecute && gidMigrationPreview.missingFileCount > 0} class="gid-migration-issues">
          {#each gidMigrationPreview.issues as issue}<li>{issue}</li>{/each}
        </ul>
      {/if}
      {#if gidMigrationInProgress}
        <div class="gid-migration-progress">
          <div class="gid-migration-progress-heading">
            <span>{gidMigrationProgress || 'GID移行中...'}</span>
            <strong>{gidMigrationOverallPercent.toFixed(1)}%</strong>
          </div>
          <div class="gid-migration-progress-row">
            <div class="gid-migration-progress-label"><span>移行全体</span><span>{gidMigrationOverallPercent.toFixed(1)}%</span></div>
            <div class="gid-migration-progress-track"><span style={`width: ${gidMigrationOverallPercent}%`}></span></div>
          </div>
          <div class="gid-migration-progress-row">
            <div class="gid-migration-progress-label">
              <span>{gidMigrationProgressPhase === 'database' || gidMigrationProgressPhase === 'database_prepare' ? 'データベース' : '現在の工程'}</span>
              <span>{gidMigrationProgressPhase === 'database' || gidMigrationProgressPhase === 'database_prepare' ? `${gidMigrationDatabasePercent.toFixed(1)}%` : (gidMigrationPhaseTotal > 0 ? `${gidMigrationPhaseCompleted.toLocaleString()}/${gidMigrationPhaseTotal.toLocaleString()}` : '準備中')}</span>
            </div>
            <div class="gid-migration-progress-track secondary">
              <span style={`width: ${gidMigrationProgressPhase === 'database' || gidMigrationProgressPhase === 'database_prepare' ? gidMigrationDatabasePercent : (gidMigrationPhaseTotal > 0 ? Math.min(100, gidMigrationPhaseCompleted / gidMigrationPhaseTotal * 100) : 0)}%`}></span>
            </div>
          </div>
          <div class="gid-migration-time-summary">
            <span>経過時間 <strong>{formatGidMigrationDuration(gidMigrationElapsedSeconds)}</strong></span>
            <span>推定残り時間 <strong>{gidMigrationEstimatedRemainingSeconds === null ? '計算中' : `約 ${formatGidMigrationDuration(gidMigrationEstimatedRemainingSeconds)}`}</strong></span>
          </div>
          <small>残り時間は現在までの処理速度から算出する概算です。</small>
        </div>
      {/if}
      <div class="modal-actions">
        <button class="danger-button" disabled={!gidMigrationPreview.canExecute || gidMigrationInProgress} onclick={executeGidMigration}>
          {gidMigrationInProgress ? '移行中...' : 'バックアップして移行'}
        </button>
        <button class="quiet-button gid-migration-cancel-button" disabled={gidMigrationInProgress} onclick={closeGidMigrationPreview}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if gidAssignmentConfirmation}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal gid-assignment-modal" aria-labelledby="gid-assignment-title" onkeydown={(event) => {
      if (event.key === 'Escape' && !gidAssignmentInProgress) {
        event.preventDefault();
        closeGidAssignmentConfirmation();
      }
    }}>
      <div class="modal-heading">
        <h2 id="gid-assignment-title">gidを発行しますか？</h2>
        <button title="閉じる" disabled={gidAssignmentInProgress} onclick={closeGidAssignmentConfirmation}><X size={18} /></button>
      </div>
      <p>選択した{gidAssignmentConfirmation.folderPaths.length}フォルダの配下を再帰的に検索し、<strong>{gidAssignmentConfirmation.extensions.join(' / ')}</strong> ファイルへ{gidSettings.digitCount}桁のgidを付与します。</p>
      <ul>
        {#each gidAssignmentConfirmation.folderPaths.slice(0, 5) as folderPath}
          <li title={folderPath}>{folderPath}</li>
        {/each}
        {#if gidAssignmentConfirmation.folderPaths.length > 5}<li>ほか {gidAssignmentConfirmation.folderPaths.length - 5} フォルダ</li>{/if}
      </ul>
      <p class="gid-assignment-note">既にgidが付与されているファイルはスキップします。発行番号は台帳とGallery本体DBに照合し、重複しない番号を予約してからファイル名を変更します。</p>
      <div class="modal-actions">
        <button class="primary-button" disabled={gidAssignmentInProgress} onclick={confirmGidAssignment}>{gidAssignmentInProgress ? '発行中...' : 'gidを発行'}</button>
        <button class="quiet-button" disabled={gidAssignmentInProgress} onclick={closeGidAssignmentConfirmation}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if creatorFolderConversionConfirmation}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal gid-assignment-modal" aria-labelledby="creator-folder-conversion-title" onkeydown={(event) => {
      if (event.key === 'Escape' && !creatorFolderConversionInProgress) {
        event.preventDefault();
        closeCreatorFolderConversionConfirmation();
      }
    }}>
      <div class="modal-heading">
        <h2 id="creator-folder-conversion-title">作者フォルダ化しますか？</h2>
        <button title="閉じる" disabled={creatorFolderConversionInProgress} onclick={closeCreatorFolderConversionConfirmation}><X size={18} /></button>
      </div>
      {#if creatorFolderConversionConfirmation.folderPaths.length === 1}
        <p>選択した1フォルダを<strong>{getCreatorFolderTargetName(creatorFolderConversionConfirmation.folderPaths[0])}</strong>に変更します。</p>
      {:else}
        <p>選択した{creatorFolderConversionConfirmation.folderPaths.length}フォルダを、以下の変更後名に変更します。</p>
      {/if}
      <ul>
        {#each creatorFolderConversionConfirmation.folderPaths.slice(0, 5) as folderPath}
          <li title={folderPath}>{folderPath} → <strong>{getCreatorFolderTargetName(folderPath)}</strong></li>
        {/each}
        {#if creatorFolderConversionConfirmation.folderPaths.length > 5}<li>ほか {creatorFolderConversionConfirmation.folderPaths.length - 5} フォルダ</li>{/if}
      </ul>
      <p class="gid-assignment-note">フォルダ内の作品をDBへ再登録し、サムネイルキャッシュを作成します。Creator Trackingが未登録の場合は、作者名とフォルダパスを設定したテンプレートを作成します。</p>
      <div class="modal-actions">
        <button class="primary-button" disabled={creatorFolderConversionInProgress} onclick={confirmCreatorFolderConversion}>{creatorFolderConversionInProgress ? '処理中...' : '作者フォルダ化'}</button>
        <button class="quiet-button creator-folder-conversion-cancel-button" disabled={creatorFolderConversionInProgress} onclick={closeCreatorFolderConversionConfirmation}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if creatorReassignmentConfirmation}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal gid-assignment-modal" aria-labelledby="creator-reassignment-title" onkeydown={(event) => {
      if (event.key === 'Escape' && !creatorReassignmentInProgress) {
        event.preventDefault();
        closeCreatorReassignmentConfirmation();
      }
      if (event.key === 'Enter' && !creatorReassignmentInProgress) {
        event.preventDefault();
        confirmCreatorReassignment();
      }
    }}>
      <div class="modal-heading">
        <h2 id="creator-reassignment-title">作者情報を付け替えますか？</h2>
        <button title="閉じる" disabled={creatorReassignmentInProgress} onclick={closeCreatorReassignmentConfirmation}><X size={18} /></button>
      </div>
      <p>選択フォルダ配下のDB上のCreatorを、正しいCreator名へ付け替えます。ファイルやフォルダは移動しません。</p>
      <label class="rename-field">
        <span>現在のCreator</span>
        <input class="modal-input" value={creatorReassignmentConfirmation.sourceCreator} readonly />
      </label>
      <label class="rename-field">
        <span>正しいCreator</span>
        <input
          class="modal-input"
          value={creatorReassignmentConfirmation.targetCreator}
          placeholder="例：正しい作者名"
          disabled={creatorReassignmentInProgress}
          oninput={(event) => {
            if (creatorReassignmentConfirmation) {
              creatorReassignmentConfirmation = {
                ...creatorReassignmentConfirmation,
                targetCreator: event.currentTarget.value
              };
            }
          }}
        />
      </label>
      <ul>
        {#each creatorReassignmentConfirmation.folderPaths.slice(0, 5) as folderPath}
          <li title={folderPath}>{folderPath}</li>
        {/each}
        {#if creatorReassignmentConfirmation.folderPaths.length > 5}<li>ほか {creatorReassignmentConfirmation.folderPaths.length - 5} フォルダ</li>{/if}
      </ul>
      <p class="gid-assignment-note">対象は選択フォルダ配下に登録されている作品です。Creator Trackingは現在のCreator名のページがあり、正しいCreator名のページが未作成の場合だけキーを変更します。</p>
      <div class="modal-actions">
        <button class="primary-button" disabled={creatorReassignmentInProgress || !creatorReassignmentConfirmation.targetCreator.trim()} onclick={confirmCreatorReassignment}>{creatorReassignmentInProgress ? '処理中...' : '付け替え'}</button>
        <button class="quiet-button creator-folder-conversion-cancel-button" disabled={creatorReassignmentInProgress} onclick={closeCreatorReassignmentConfirmation}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if deleteConfirmation}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal delete-modal" aria-labelledby="delete-title">
      <div class="modal-heading">
        <h2 id="delete-title">{selectedPaths.length} 件を削除しますか？</h2>
        <button title="閉じる" onclick={() => (deleteConfirmation = false)}><X size={18} /></button>
      </div>
      <p>ファイルとフォルダの実体を削除します。この操作は元に戻せません。</p>
      <div class="modal-actions">
        <button class="danger-button" onclick={deleteExplorerSelection}>削除</button>
        <button class="quiet-button" onclick={() => (deleteConfirmation = false)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if splitRenamingEntry}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal" aria-labelledby="split-rename-title" onkeydown={(event) => handleRenameDialogKeydown(event, 'right')}>
      <div class="modal-heading">
        <h2 id="split-rename-title">名前の変更</h2>
        <button title="閉じる" onclick={() => (splitRenamingEntry = null)}><X size={18} /></button>
      </div>
      <label class="rename-field rename-name-field">
        <span>名前</span>
        <input
          class="modal-input"
          bind:value={splitRenameValue}
          maxlength={splitRenameMaximumLength}
          use:focusAtEnd
          onkeydown={(event) => handleRenameInputKeydown(event, 'right')}
        />
      </label>
      <div class="rename-field">
        <span>gid</span>
        <code>{splitRenameIdentifier || '未設定'}</code>
      </div>
      <div class="rename-field">
        <span>拡張子</span>
        <code>{splitRenameExtension || 'なし'}</code>
      </div>
      <div class="modal-actions">
        <button class="primary-button" onclick={saveSplitRename}>変更</button>
        <button class="quiet-button" onclick={() => (splitRenamingEntry = null)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}

{#if splitDeleteConfirmation}
  <div class="modal-backdrop" role="presentation">
    <dialog open class="modal rename-modal delete-modal" aria-labelledby="split-delete-title">
      <div class="modal-heading">
        <h2 id="split-delete-title">{explorerSplit?.rightSelectedPaths.length ?? 0} 件を削除しますか？</h2>
        <button title="閉じる" onclick={() => (splitDeleteConfirmation = false)}><X size={18} /></button>
      </div>
      <p>ファイルとフォルダの実体を削除します。この操作は元に戻せません。</p>
      <div class="modal-actions">
        <button class="danger-button" onclick={deleteSplitSelection}>削除</button>
        <button class="quiet-button" onclick={() => (splitDeleteConfirmation = false)}>キャンセル</button>
      </div>
    </dialog>
  </div>
{/if}
