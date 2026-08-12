using System.IO;

namespace GalleryBrowser.Services;

public sealed class GalleryDatabaseUpdateService
{
    internal static readonly string[] DefaultSupportedExtensions =
    [
        ".zip", ".rar", ".7z", ".cbz", ".cbr",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpeg", ".mpg", ".ts"
    ];

    private readonly GalleryDatabase _database;
    private readonly FileBrowserService _fileBrowser;
    private readonly ThumbnailService _thumbnailService;
    private readonly GalleryFileScanner _fileScanner = new();
    private IReadOnlyDictionary<string, string> _categoryLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public GalleryDatabaseUpdateService(
        GalleryDatabase database,
        FileBrowserService fileBrowser,
        ThumbnailService thumbnailService)
    {
        _database = database;
        _fileBrowser = fileBrowser;
        _thumbnailService = thumbnailService;
    }

    public async Task<GalleryDatabaseUpdateResult> UpdateAsync(
        IReadOnlyList<string> categories,
        Action<string>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshCategoryLabels();
        var selectedCategories = categories
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedCategories.Length == 0)
        {
            throw new ArgumentException("更新するギャラリー対象を選択してください。", nameof(categories));
        }

        var scanPlans = selectedCategories
            .Select(category => new GalleryScanPlan(
                category,
                _database.ListGalleryScanTargets(category)
                    .Select(target => target.Path)
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                _database.GetGalleryScanSettings(category).SupportedExtensions
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeExtension)
                    .Where(extension => !string.IsNullOrWhiteSpace(extension))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .Where(plan => plan.Targets.Length > 0)
            .ToArray();
        if (scanPlans.Length == 0)
        {
            throw new InvalidOperationException("選択した区分に、存在する走査対象ディレクトリがありません。");
        }

        var ratings = _database.SnapshotGalleryRatings();
        var completed = false;
        try
        {
            var error = new List<string>();
            var scanSummaries = new List<string>();
            var deletedMissingRecords = 0;
            var deletedThumbnailEntries = 0;
            for (var index = 0; index < scanPlans.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = scanPlans[index];
                reportProgress?.Invoke($"[区分 {index + 1}/{scanPlans.Length}] {GetCategoryLabel(plan.Category)} を開始: {plan.Targets.Length}フォルダ / {(plan.Extensions.Length == 0 ? "全対応形式" : $"{plan.Extensions.Length}形式")}");
                AssignConfiguredGids(plan, reportProgress, cancellationToken);
                var result = await ScanCategoryAsync(plan, reportProgress, cancellationToken);
                error.AddRange(result.Error);
                scanSummaries.Add($"{GetCategoryLabel(plan.Category)}: {result.ScannedSummary}");
                deletedMissingRecords += result.DeletedMissingRecords;
                deletedThumbnailEntries += result.DeletedThumbnailEntries;
            }

            _database.RestoreGalleryRatings(ratings);
            _database.ApplyGalleryTargetCategories(selectedCategories);
            completed = true;
            var scanned = scanSummaries.Count > 0
                ? string.Join(" / ", scanSummaries)
                : "更新が完了しました。";
            return new GalleryDatabaseUpdateResult(
                scanned,
                $"Errors: {error.Count}",
                deletedMissingRecords,
                deletedThumbnailEntries);
        }
        finally
        {
            _database.InvalidateExternalGallerySchemaCache();
            if (!completed)
            {
                _database.RestoreGalleryRatings(ratings);
            }
        }
    }

    public async Task<GalleryDatabaseUpdateResult> UpdateFoldersAsync(
        IReadOnlyList<GalleryFolderScanRequest> requests,
        Action<string>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshCategoryLabels();
        var scanPlans = requests
            .Where(request => !string.IsNullOrWhiteSpace(request.Category))
            .GroupBy(request => request.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var configuredRoots = _database.ListGalleryScanTargets(group.Key)
                    .Where(target => !string.IsNullOrWhiteSpace(target.Path))
                    .Select(target => Path.GetFullPath(target.Path.Trim())
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .ToArray();
                var targets = group
                    .SelectMany(request => request.Folders)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFullPath(path.Trim())
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    .Where(Directory.Exists)
                    .Where(path => configuredRoots.Any(root => IsPathWithinRoot(path, root)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var configuredExtensions = _database.GetGalleryScanSettings(group.Key).SupportedExtensions
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(NormalizeExtension)
                    .Where(extension => !string.IsNullOrWhiteSpace(extension))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var additionalExtensions = group
                    .SelectMany(request => request.AdditionalExtensions ?? [])
                    .Select(NormalizeExtension)
                    .Where(extension => !string.IsNullOrWhiteSpace(extension))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var extensions = (configuredExtensions.Length > 0
                        ? configuredExtensions
                        : DefaultSupportedExtensions)
                    .Concat(additionalExtensions)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new GalleryScanPlan(group.Key, targets, extensions);
            })
            .Where(plan => plan.Targets.Length > 0)
            .ToArray();
        if (scanPlans.Length == 0)
        {
            throw new InvalidOperationException("選択したフォルダは、存在するGallery走査対象ディレクトリの配下にありません。");
        }

        try
        {
            var error = new List<string>();
            var scanSummaries = new List<string>();
            var deletedMissingRecords = 0;
            var deletedThumbnailEntries = 0;
            for (var index = 0; index < scanPlans.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = scanPlans[index];
                reportProgress?.Invoke($"[DB同期 {index + 1}/{scanPlans.Length}] {GetCategoryLabel(plan.Category)}: {plan.Targets.Length}フォルダを走査します。");
                AssignConfiguredGids(plan, reportProgress, cancellationToken);
                var result = await ScanCategoryAsync(plan, reportProgress, cancellationToken);
                error.AddRange(result.Error);
                scanSummaries.Add($"{GetCategoryLabel(plan.Category)}: {result.ScannedSummary}");
                deletedMissingRecords += result.DeletedMissingRecords;
                deletedThumbnailEntries += result.DeletedThumbnailEntries;
                _database.SynchronizeGidRegistryPathsUnderFolders(plan.Targets);
            }

            var categories = scanPlans.Select(plan => plan.Category).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _database.ApplyGalleryTargetCategories(categories);
            var scanned = scanSummaries.Count > 0
                ? string.Join(" / ", scanSummaries)
                : "更新が完了しました。";
            return new GalleryDatabaseUpdateResult(
                scanned,
                $"Errors: {error.Count}",
                deletedMissingRecords,
                deletedThumbnailEntries);
        }
        finally
        {
            _database.InvalidateExternalGallerySchemaCache();
        }
    }

    private async Task<GalleryScanProcessResult> ScanCategoryAsync(
        GalleryScanPlan plan,
        Action<string>? reportProgress,
        CancellationToken cancellationToken)
    {
        foreach (var target in plan.Targets)
        {
            reportProgress?.Invoke($"[scan-root] {target}");
        }
        var configuredRoots = _database.ListGalleryScanTargets(plan.Category)
            .Select(target => target.Path)
            .ToArray();
        var scanned = await _fileScanner.ScanAsync(
            plan,
            configuredRoots,
            message => reportProgress?.Invoke($"[{GetCategoryLabel(plan.Category)}] {message}"),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke($"[{GetCategoryLabel(plan.Category)}] DBへ{scanned.Items.Count:N0}件を書き込んでいます。");
        var written = await Task.Run(
            () => _database.UpsertScannedGalleryItems(scanned.Items, cancellationToken),
            cancellationToken);
        var errors = scanned.Errors.Concat(written.Errors).ToArray();
        foreach (var error in errors.Take(20))
        {
            reportProgress?.Invoke($"[{GetCategoryLabel(plan.Category)}] [error] {error}");
        }
        var cleanup = errors.Length == 0
            ? CleanupMissingGalleryItems(plan, reportProgress, cancellationToken)
            : new MissingGalleryItemCleanupResult(0, 0);
        if (errors.Length > 0)
        {
            reportProgress?.Invoke(
                $"[{GetCategoryLabel(plan.Category)}] 走査エラーがあるため、存在しない作品レコードの整理をスキップしました。");
        }
        var scannedSummary = $"Scanned files: {written.WrittenCount} / Missing records removed: {cleanup.DeletedRecords}";
        var errorSummary = $"Errors: {errors.Length}";
        reportProgress?.Invoke($"[{GetCategoryLabel(plan.Category)}] {scannedSummary}");
        reportProgress?.Invoke($"[{GetCategoryLabel(plan.Category)}] {errorSummary}");
        return new GalleryScanProcessResult(
            scannedSummary,
            errors,
            cleanup.DeletedRecords,
            cleanup.DeletedThumbnailEntries);
    }

    private MissingGalleryItemCleanupResult CleanupMissingGalleryItems(
        GalleryScanPlan plan,
        Action<string>? reportProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var missingPaths = _database.ListGalleryItemPathsUnderFolders(plan.Category, plan.Targets)
            .Where(path => !File.Exists(path) && !Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missingPaths.Length == 0)
        {
            return new MissingGalleryItemCleanupResult(0, 0);
        }

        reportProgress?.Invoke(
            $"[{GetCategoryLabel(plan.Category)}] 実体がない作品レコードを{missingPaths.Length:N0}件整理しています。");
        var deletedRecords = 0;
        var deletedThumbnailEntries = 0;
        foreach (var batch in missingPaths.Chunk(150))
        {
            cancellationToken.ThrowIfCancellationRequested();
            deletedRecords += _database.DeleteGalleryWorks(batch);
            foreach (var path in batch)
            {
                deletedThumbnailEntries += _thumbnailService.InvalidateSourcesUnderPath(path);
            }
        }
        reportProgress?.Invoke(
            $"[{GetCategoryLabel(plan.Category)}] 欠損作品の整理完了: DB {deletedRecords:N0}件 / サムネイル {deletedThumbnailEntries:N0}件");
        return new MissingGalleryItemCleanupResult(deletedRecords, deletedThumbnailEntries);
    }

    private void AssignConfiguredGids(
        GalleryScanPlan plan,
        Action<string>? reportProgress,
        CancellationToken cancellationToken)
    {
        var extensions = plan.Extensions.Length > 0 ? plan.Extensions : DefaultSupportedExtensions;
        var settings = _database.GetGidSettings();
        reportProgress?.Invoke(
            $"[{GetCategoryLabel(plan.Category)}] DB走査前に{settings.DigitCount}桁gidを確認しています。");
        var result = _fileBrowser.AssignGids(
            plan.Targets,
            extensions,
            settings.DigitCount,
            cancellationToken);
        if (result.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"gidを正式なファイル名へ付与できないファイルが{result.Errors.Count:N0}件あるため、DB走査を中断しました。" +
                Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Take(10)));
        }
        reportProgress?.Invoke(
            $"[{GetCategoryLabel(plan.Category)}] gid確認完了: 新規{result.AssignedCount:N0}件 / 設定済み{result.SkippedExistingCount:N0}件");
    }

    private static string NormalizeExtension(string extension)
    {
        var value = extension.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.StartsWith(".", StringComparison.Ordinal) ? value : "." + value;
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

    private void RefreshCategoryLabels()
    {
        _categoryLabels = _database.ListGallerySections()
            .ToDictionary(section => section.Id, section => section.Label, StringComparer.OrdinalIgnoreCase);
    }

    private string GetCategoryLabel(string category) =>
        _categoryLabels.TryGetValue(category, out var label) ? label : category;

}

public sealed record GalleryDatabaseUpdateResult(
    string ScanSummary,
    string ErrorSummary,
    int DeletedMissingRecords = 0,
    int DeletedThumbnailEntries = 0);
public sealed record GalleryFolderScanRequest(
    string Category,
    IReadOnlyList<string> Folders,
    IReadOnlyList<string>? AdditionalExtensions = null);

internal sealed record GalleryScanPlan(string Category, string[] Targets, string[] Extensions);
internal sealed record GalleryScanProcessResult(
    string ScannedSummary,
    IReadOnlyList<string> Error,
    int DeletedMissingRecords,
    int DeletedThumbnailEntries);
internal sealed record MissingGalleryItemCleanupResult(
    int DeletedRecords,
    int DeletedThumbnailEntries);
