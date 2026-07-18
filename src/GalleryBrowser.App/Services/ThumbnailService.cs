using GalleryBrowser.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GalleryBrowser.Services;

public sealed class ThumbnailService
{
    public const string ZipPlaCoverFileName = "{ZipPlaCoverFile}.jpg";
    private const string ThumbnailCacheVersion = "v6";
    private const int MaximumArchiveEntriesToInspect = 10_000;
    private const int MaximumNestedArchiveDepth = 1;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp",
        ".gif"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".cbz",
    };

    private const int MaximumConcurrentGenerations = 2;
    private readonly object _generationQueueLock = new();
    private readonly object _cacheEntryLock = new();
    private readonly object _inflightRequestLock = new();
    private readonly PriorityQueue<ThumbnailWork, (int Priority, long Sequence)> _generationQueue = new();
    private readonly Dictionary<string, Task<string?>> _inflightRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly GalleryDatabase _database;
    private readonly FfmpegService _ffmpegService;
    private readonly string _defaultCacheRoot;
    private int _activeGenerationCount;
    private long _generationSequence;
    private HashSet<string>? _trackedCachePaths;
    private IReadOnlyList<string> _targetDirectories = [];

    public ThumbnailService(GalleryDatabase database, FfmpegService ffmpegService)
    {
        _database = database;
        _ffmpegService = ffmpegService;
        _defaultCacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalleryBrowser",
            "thumb-cache");
        CacheRoot = _defaultCacheRoot;
        Directory.CreateDirectory(CacheRoot);
    }

    public string CacheRoot { get; private set; }

    public void ConfigureCacheRoot(string cacheRoot)
    {
        var normalizedRoot = NormalizeCacheRoot(cacheRoot);
        if (!string.Equals(CacheRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            CacheRoot = normalizedRoot;
            lock (_cacheEntryLock)
            {
                _trackedCachePaths = null;
            }
        }
        Directory.CreateDirectory(CacheRoot);
    }

    public ThumbnailCacheMoveResult MoveCacheRoot(string cacheRoot)
    {
        var newRoot = NormalizeCacheRoot(cacheRoot);
        var oldRoot = CacheRoot;
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return new ThumbnailCacheMoveResult(0, 0, oldRoot);
        }

        Directory.CreateDirectory(newRoot);
        var movedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failedCount = 0;
        foreach (var sourcePath in EnumerateCacheFiles().ToArray())
        {
            try
            {
                var relativePath = Path.GetRelativePath(oldRoot, sourcePath);
                var destinationPath = Path.Combine(newRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                File.Move(sourcePath, destinationPath, overwrite: true);
                movedPaths[sourcePath] = destinationPath;
            }
            catch (IOException)
            {
                failedCount++;
            }
            catch (UnauthorizedAccessException)
            {
                failedCount++;
            }
        }

        _database.UpdateThumbnailCacheEntryPaths(movedPaths);
        CacheRoot = newRoot;
        lock (_cacheEntryLock)
        {
            _trackedCachePaths = null;
        }
        return new ThumbnailCacheMoveResult(movedPaths.Count, failedCount, CacheRoot);
    }

    public void ConfigureTargetDirectories(IEnumerable<string> targetDirectories)
    {
        _targetDirectories = targetDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void InvalidateCacheTracking()
    {
        lock (_cacheEntryLock)
        {
            _trackedCachePaths = null;
        }
    }

    public int InvalidateSourcesUnderPath(string sourceRoot)
    {
        var normalizedRoot = Path.GetFullPath(sourceRoot.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        var entries = _database.ListThumbnailCacheEntries()
            .Where(entry => string.Equals(entry.SourcePath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                            entry.SourcePath.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0)
        {
            return 0;
        }

        foreach (var entry in entries)
        {
            DeleteCacheFile(entry.CachePath);
        }
        var cachePaths = entries.Select(entry => entry.CachePath).ToArray();
        _database.DeleteThumbnailCacheEntries(cachePaths);
        lock (_cacheEntryLock)
        {
            _trackedCachePaths?.ExceptWith(cachePaths);
        }
        return entries.Length;
    }

    public async Task<string?> GetOrCreateThumbnailUriAsync(
        GalleryItemDto item,
        int priority = 0,
        bool forceRefresh = false,
        ThumbnailCropAdjustmentDto? cropAdjustment = null)
    {
        var sourcePath = Path.GetFullPath(item.Path);
        if (!IsCacheTarget(sourcePath))
        {
            return null;
        }

        var sourceStamp = GetSourceStamp(sourcePath);
        var normalizedAdjustment = NormalizeCropAdjustment(cropAdjustment);
        var cachePath = GetCachePath(sourcePath, sourceStamp, normalizedAdjustment.CacheKey);
        if (!forceRefresh && File.Exists(cachePath))
        {
            EnsureCacheEntryTracked(cachePath, sourcePath, sourceStamp);
            return ToCacheUri(cachePath);
        }

        Task<string?> request;
        lock (_inflightRequestLock)
        {
            if (_inflightRequests.TryGetValue(cachePath, out var inflightRequest))
            {
                request = inflightRequest;
            }
            else
            {
                request = CreateAndTrackThumbnailAsync(
                    sourcePath,
                    sourceStamp,
                    cachePath,
                    normalizedAdjustment,
                    priority,
                    forceRefresh);
                _inflightRequests[cachePath] = request;
            }
        }

        try
        {
            return await request;
        }
        finally
        {
            lock (_inflightRequestLock)
            {
                if (_inflightRequests.TryGetValue(cachePath, out var currentRequest) && ReferenceEquals(currentRequest, request))
                {
                    _inflightRequests.Remove(cachePath);
                }
            }
        }
    }

    public string? TryGetCachedThumbnailUri(
        GalleryItemDto item,
        ThumbnailCropAdjustmentDto? cropAdjustment = null)
    {
        try
        {
            var sourcePath = Path.GetFullPath(item.Path);
            if (!IsCacheTarget(sourcePath))
            {
                return null;
            }

            var sourceStamp = GetSourceStamp(sourcePath);
            var normalizedAdjustment = NormalizeCropAdjustment(cropAdjustment);
            var cachePath = GetCachePath(sourcePath, sourceStamp, normalizedAdjustment.CacheKey);
            if (!File.Exists(cachePath))
            {
                return null;
            }

            EnsureCacheEntryTracked(cachePath, sourcePath, sourceStamp);
            return ToCacheUri(cachePath);
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> CreateAndTrackThumbnailAsync(
        string sourcePath,
        string sourceStamp,
        string cachePath,
        ThumbnailCropAdjustment cropAdjustment,
        int priority,
        bool forceRefresh)
    {
        try
        {
            if (forceRefresh && File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }

            var created = await QueueThumbnailGeneration(() => CreateThumbnail(sourcePath, cachePath, cropAdjustment), priority);
            if (created)
            {
                EnsureCacheEntryTracked(cachePath, sourcePath, sourceStamp);
            }
            return created ? ToCacheUri(cachePath) : null;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureCacheEntryTracked(string cachePath, string sourcePath, string sourceStamp)
    {
        lock (_cacheEntryLock)
        {
            _trackedCachePaths ??= _database.ListThumbnailCacheEntries()
                .Select(entry => entry.CachePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (_trackedCachePaths.Contains(cachePath))
            {
                return;
            }

            _database.UpsertThumbnailCacheEntry(cachePath, sourcePath, sourceStamp);
            _trackedCachePaths.Add(cachePath);
        }
    }

    public ThumbnailCacheMaintenanceResult MaintainCache()
    {
        var staleEntries = new List<string>();
        foreach (var entry in _database.ListThumbnailCacheEntries())
        {
            var sourceExists = File.Exists(entry.SourcePath) || Directory.Exists(entry.SourcePath);
            var currentStamp = sourceExists ? GetSourceStamp(entry.SourcePath) : string.Empty;
            if (!sourceExists ||
                !File.Exists(entry.CachePath) ||
                !string.Equals(entry.SourceStamp, currentStamp, StringComparison.Ordinal))
            {
                DeleteCacheFile(entry.CachePath);
                staleEntries.Add(entry.CachePath);
            }
        }

        _database.DeleteThumbnailCacheEntries(staleEntries);
        if (staleEntries.Count > 0)
        {
            lock (_cacheEntryLock)
            {
                _trackedCachePaths?.ExceptWith(staleEntries);
            }
        }
        var trackedPaths = _database.ListThumbnailCacheEntries()
            .Select(entry => entry.CachePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphanFiles = EnumerateCacheFiles()
            .Where(path => !trackedPaths.Contains(path))
            .ToArray();
        foreach (var orphanFile in orphanFiles)
        {
            DeleteCacheFile(orphanFile);
        }

        return new ThumbnailCacheMaintenanceResult(staleEntries.Count, orphanFiles.Length);
    }

    public async Task<ThumbnailCacheRebuildResult> RebuildAsync(Func<int, int, Task>? reportProgress = null)
    {
        if (_targetDirectories.Count == 0)
        {
            throw new InvalidOperationException("再構築するには、サムネイルキャッシュの対象ディレクトリを登録してください。");
        }

        var deletedCount = ClearCache();
        var sources = EnumerateRebuildSources().ToArray();
        var generatedCount = 0;
        for (var index = 0; index < sources.Length; index++)
        {
            var sourcePath = sources[index];
            var uri = await GetOrCreateThumbnailUriAsync(new GalleryItemDto(
                StringComparer.OrdinalIgnoreCase.GetHashCode(sourcePath),
                Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Directory.Exists(sourcePath) ? "folder" : "archive",
                sourcePath,
                0,
                [],
                "#93c5fd",
                null), priority: 1_000);
            if (uri is not null)
            {
                generatedCount++;
            }

            if (reportProgress is not null && (index == sources.Length - 1 || (index + 1) % 20 == 0))
            {
                await reportProgress(index + 1, sources.Length);
            }
        }

        return new ThumbnailCacheRebuildResult(deletedCount, generatedCount, sources.Length);
    }

    private int ClearCache()
    {
        var files = EnumerateCacheFiles().ToArray();
        foreach (var file in files)
        {
            DeleteCacheFile(file);
        }

        _database.ClearThumbnailCacheEntries();
        lock (_cacheEntryLock)
        {
            _trackedCachePaths = [];
        }
        return files.Length;
    }

    private IEnumerable<string> EnumerateRebuildSources()
    {
        foreach (var targetPath in _targetDirectories.Where(Directory.Exists))
        {
            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(targetPath);
            while (pendingDirectories.TryPop(out var directoryPath))
            {
                yield return directoryPath;

                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(directoryPath).GetFileSystemInfos();
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry is DirectoryInfo directory && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pendingDirectories.Push(directory.FullName);
                    }
                    else if (entry is FileInfo file &&
                              (ImageExtensions.Contains(file.Extension) ||
                               ArchiveExtensions.Contains(file.Extension) ||
                               _ffmpegService.SupportsVideo(file.FullName)))
                    {
                        yield return file.FullName;
                    }
                }
            }
        }
    }

    private bool IsCacheTarget(string sourcePath)
    {
        if (_targetDirectories.Count == 0)
        {
            return true;
        }

        return _targetDirectories.Any(target =>
        {
            var normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(sourcePath, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                sourcePath.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                sourcePath.StartsWith(normalizedTarget + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }

    private IEnumerable<string> EnumerateCacheFiles()
    {
        try
        {
            return Directory.EnumerateFiles(CacheRoot, "*", SearchOption.AllDirectories).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void DeleteCacheFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A locked cache file can be removed during the next maintenance run.
        }
        catch (UnauthorizedAccessException)
        {
            // A protected cache file can be removed during the next maintenance run.
        }
    }

    private string NormalizeCacheRoot(string cacheRoot) =>
        string.IsNullOrWhiteSpace(cacheRoot)
            ? _defaultCacheRoot
            : Path.GetFullPath(cacheRoot);

    private Task<bool> QueueThumbnailGeneration(Func<bool> generation, int priority)
    {
        var work = new ThumbnailWork(generation);
        lock (_generationQueueLock)
        {
            var normalizedPriority = Math.Clamp(priority, 0, 1_000_000);
            _generationQueue.Enqueue(work, (-normalizedPriority, _generationSequence++));
            StartQueuedGenerations();
        }

        return work.Completion.Task;
    }

    private void StartQueuedGenerations()
    {
        while (_activeGenerationCount < MaximumConcurrentGenerations && _generationQueue.TryDequeue(out var work, out _))
        {
            _activeGenerationCount++;
            _ = Task.Run(() =>
            {
                try
                {
                    work.Completion.TrySetResult(work.Generation());
                }
                catch (Exception ex)
                {
                    work.Completion.TrySetException(ex);
                }
                finally
                {
                    lock (_generationQueueLock)
                    {
                        _activeGenerationCount--;
                        StartQueuedGenerations();
                    }
                }
            });
        }
    }

    public bool DeleteFolderCover(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("フォルダが見つかりません。: " + fullPath);
        }

        var coverPath = Path.Combine(fullPath, ZipPlaCoverFileName);
        if (!File.Exists(coverPath))
        {
            return false;
        }

        File.Delete(coverPath);
        return true;
    }

    public string SetParentFolderCover(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath) && !Directory.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("対象のファイルまたはフォルダが見つかりません。", fullSourcePath);
        }

        var sourceForParent = Directory.Exists(fullSourcePath)
            ? fullSourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullSourcePath;
        var parentPath = Path.GetDirectoryName(sourceForParent)
            ?? throw new InvalidOperationException("ルートフォルダには親フォルダのサムネイルを設定できません。");

        var coverPath = Path.Combine(parentPath, ZipPlaCoverFileName);
        if (_ffmpegService.SupportsVideo(fullSourcePath))
        {
            if (!_ffmpegService.TryCreateThumbnail(fullSourcePath, coverPath))
            {
                throw new InvalidOperationException("動画からサムネイルを生成できませんでした。FFmpeg設定を確認してください。");
            }

            return parentPath;
        }

        using var image = OpenRepresentativeImage(fullSourcePath)
            ?? throw new InvalidOperationException("サムネイルに利用できる画像が見つかりません。");
        SaveCoverImage(image, coverPath);
        return parentPath;
    }

    private bool CreateThumbnail(string sourcePath, string cachePath, ThumbnailCropAdjustment cropAdjustment)
    {
        if (_ffmpegService.SupportsVideo(sourcePath))
        {
            return _ffmpegService.TryCreateThumbnail(sourcePath, cachePath);
        }

        using var image = OpenRepresentativeImage(sourcePath);
        if (image is null)
        {
            return false;
        }

        ResizeForThumbnail(image, cropAdjustment);

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        image.SaveAsJpeg(cachePath, new JpegEncoder { Quality = 82 });
        return true;
    }

    private static void SaveCoverImage(Image image, string coverPath)
    {
        ResizeForThumbnail(image, ThumbnailCropAdjustment.Default);
        Directory.CreateDirectory(Path.GetDirectoryName(coverPath)!);
        var temporaryPath = coverPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            image.SaveAsJpeg(temporaryPath, new JpegEncoder { Quality = 82 });
            File.Move(temporaryPath, coverPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ResizeForThumbnail(Image image, ThumbnailCropAdjustment cropAdjustment)
    {
        const int targetWidth = 462;
        const int targetHeight = 350;
        var targetRatio = targetWidth / (double)targetHeight;
        var sourceRatio = image.Width / (double)image.Height;
        var cropWidth = image.Width;
        var cropHeight = image.Height;

        if (sourceRatio > targetRatio)
        {
            cropWidth = Math.Clamp((int)Math.Round(image.Height * targetRatio), 1, image.Width);
        }
        else if (sourceRatio < targetRatio)
        {
            cropHeight = Math.Clamp((int)Math.Round(image.Width / targetRatio), 1, image.Height);
        }

        var scale = cropAdjustment.ScalePercent / 100d;
        cropWidth = Math.Clamp((int)Math.Round(cropWidth / scale), 1, cropWidth);
        cropHeight = Math.Clamp((int)Math.Round(cropHeight / scale), 1, cropHeight);

        var maxOffsetX = image.Width - cropWidth;
        var maxOffsetY = image.Height - cropHeight;
        var cropX = Math.Clamp(
            (int)Math.Round(maxOffsetX / 2d + cropWidth * cropAdjustment.HorizontalOffsetPercent / 100d),
            0,
            maxOffsetX);
        var cropY = Math.Clamp(
            (int)Math.Round(maxOffsetY / 2d + cropHeight * cropAdjustment.VerticalOffsetPercent / 100d),
            0,
            maxOffsetY);

        image.Mutate(context => context
            .Crop(new Rectangle(cropX, cropY, cropWidth, cropHeight))
            .Resize(targetWidth, targetHeight));
    }

    private static Image? OpenRepresentativeImage(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            var imagePath = FindFirstImageInFolder(sourcePath);
            return imagePath is null ? null : Image.Load(imagePath);
        }

        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var extension = Path.GetExtension(sourcePath);
        if (ImageExtensions.Contains(extension))
        {
            return Image.Load(sourcePath);
        }

        if (ArchiveExtensions.Contains(extension))
        {
            return OpenFirstImageInArchive(sourcePath);
        }

        return null;
    }

    private static string? FindFirstImageInFolder(string folderPath)
    {
        try
        {
            var coverPath = Path.Combine(folderPath, ZipPlaCoverFileName);
            if (File.Exists(coverPath))
            {
                return coverPath;
            }

            return Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(path => ImageExtensions.Contains(Path.GetExtension(path)) &&
                               !string.Equals(Path.GetFileName(path), ZipPlaCoverFileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, Comparer<string>.Create(CompareNaturally))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static Image? OpenFirstImageInArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return OpenRepresentativeImageInArchive(archive, depth: 0);
    }

    private static Image? OpenRepresentativeImageInArchive(ZipArchive archive, int depth)
    {
        var candidates = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Take(MaximumArchiveEntriesToInspect)
            .ToArray();

        foreach (var entry in candidates
                     .Where(candidate => ImageExtensions.Contains(Path.GetExtension(candidate.FullName)))
                     .OrderBy(candidate => GetRepresentativeImagePriority(candidate.Name))
                     .ThenBy(candidate => candidate.FullName, Comparer<string>.Create(CompareNaturally)))
        {
            try
            {
                using var stream = entry.Open();
                return Image.Load(stream);
            }
            catch
            {
                // Broken or unsupported images do not prevent later pages from becoming the cover.
            }
        }

        if (depth >= MaximumNestedArchiveDepth)
        {
            return null;
        }

        foreach (var entry in candidates
                     .Where(candidate => ArchiveExtensions.Contains(Path.GetExtension(candidate.Name)))
                     .OrderBy(candidate => candidate.FullName, Comparer<string>.Create(CompareNaturally)))
        {
            try
            {
                using var stream = entry.Open();
                using var nestedArchive = new ZipArchive(stream, ZipArchiveMode.Read);
                var nestedImage = OpenRepresentativeImageInArchive(nestedArchive, depth + 1);
                if (nestedImage is not null)
                {
                    return nestedImage;
                }
            }
            catch
            {
                // Nested archives are optional candidates, matching ZipPla's search-until-found behavior.
            }
        }

        return null;
    }

    private static int GetRepresentativeImagePriority(string name) =>
        string.Equals(name, ZipPlaCoverFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static int CompareNaturally(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsDigit(left[leftIndex]);
            var rightIsDigit = char.IsDigit(right[rightIndex]);
            if (leftIsDigit && rightIsDigit)
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

                var leftNumber = left.AsSpan(leftStart, leftIndex - leftStart).TrimStart('0');
                var rightNumber = right.AsSpan(rightStart, rightIndex - rightStart).TrimStart('0');
                var lengthComparison = leftNumber.Length.CompareTo(rightNumber.Length);
                if (lengthComparison != 0) return lengthComparison;

                var numberComparison = leftNumber.CompareTo(rightNumber, StringComparison.Ordinal);
                if (numberComparison != 0) return numberComparison;
                continue;
            }

            var characterComparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (characterComparison != 0) return characterComparison;
            leftIndex++;
            rightIndex++;
        }

        return left.Length.CompareTo(right.Length);
    }

    private string GetCachePath(string sourcePath) =>
        GetCachePath(sourcePath, GetSourceStamp(sourcePath), ThumbnailCropAdjustment.Default.CacheKey);

    private string GetCachePath(string sourcePath, string sourceStamp, string cropVariant)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourcePath + "|" + sourceStamp + "|" + ThumbnailCacheVersion + "|" + cropVariant)))
            .ToLowerInvariant();
        return Path.Combine(CacheRoot, key[..2], key + ".jpg");
    }

    private static ThumbnailCropAdjustment NormalizeCropAdjustment(ThumbnailCropAdjustmentDto? adjustment) =>
        adjustment is null
            ? ThumbnailCropAdjustment.Default
            : new ThumbnailCropAdjustment(
                Math.Clamp(adjustment.HorizontalOffsetPercent, -45, 45),
                Math.Clamp(adjustment.VerticalOffsetPercent, -45, 45),
                Math.Clamp(adjustment.ScalePercent, 100, 250));

    private sealed record ThumbnailCropAdjustment(
        double HorizontalOffsetPercent,
        double VerticalOffsetPercent,
        double ScalePercent)
    {
        public static readonly ThumbnailCropAdjustment Default = new(0, 0, 100);

        public string CacheKey => $"{HorizontalOffsetPercent:0.##}:{VerticalOffsetPercent:0.##}:{ScalePercent:0.##}";
    }

    private static string GetSourceStamp(string sourcePath)
    {
        try
        {
            if (File.Exists(sourcePath))
            {
                var info = new FileInfo(sourcePath);
                return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }

            if (Directory.Exists(sourcePath))
            {
                return Directory.GetLastWriteTimeUtc(sourcePath).Ticks.ToString();
            }
        }
        catch
        {
            return "unknown";
        }

        return "missing";
    }

    private string ToCacheUri(string cachePath)
    {
        var relative = Path.GetRelativePath(CacheRoot, cachePath);
        var segments = relative.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        var revision = File.Exists(cachePath) ? File.GetLastWriteTimeUtc(cachePath).Ticks : 0;
        return "https://gallerybrowser-cache.local/" + string.Join("/", segments) + "?v=" + revision;
    }

    private sealed class ThumbnailWork
    {
        public ThumbnailWork(Func<bool> generation)
        {
            Generation = generation;
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Func<bool> Generation { get; }
        public TaskCompletionSource<bool> Completion { get; }
    }
}

public sealed record ThumbnailCacheMaintenanceResult(int StaleEntriesRemoved, int OrphanFilesRemoved);

public sealed record ThumbnailCacheRebuildResult(int DeletedCacheFiles, int GeneratedThumbnails, int ScannedSources);

public sealed record ThumbnailCacheMoveResult(int MovedFiles, int FailedFiles, string CacheRoot);
