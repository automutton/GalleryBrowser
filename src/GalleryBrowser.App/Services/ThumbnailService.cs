using GalleryBrowser.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;
using System.Buffers;
using System.Collections.Concurrent;
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
    private const int MaximumKnownCacheUris = 12_000;
    private static readonly TimeSpan SourceStampValidationInterval = TimeSpan.FromSeconds(30);
    private static readonly DecoderOptions ThumbnailDecoderOptions = new()
    {
        TargetSize = new Size(1_200, 900),
        SkipMetadata = true
    };

    private static readonly int MaximumConcurrentGenerations =
        Math.Clamp(Environment.ProcessorCount / 4, 2, 3);
    private readonly object _generationQueueLock = new();
    private readonly object _cacheEntryLock = new();
    private readonly object _inflightRequestLock = new();
    private readonly PriorityQueue<ThumbnailWork, (int Priority, long Sequence)> _generationQueue = new();
    private readonly Dictionary<string, ThumbnailInflightRequest> _inflightRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly GalleryDatabase _database;
    private readonly FfmpegService _ffmpegService;
    private readonly string _defaultCacheRoot;
    private int _activeGenerationCount;
    private long _generationSequence;
    private readonly HashSet<string> _trackedCachePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownSourceStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _sourceStampValidationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string SourcePath, string ExpectedStamp)> _pendingSourceStampValidations = new();
    private readonly Dictionary<string, (string Uri, long Sequence)> _knownCacheUris = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Path, long Sequence)> _knownCacheUriOrder = new();
    private long _knownCacheUriSequence;
    private int _sourceStampValidationWorkerScheduled;
    private Task? _trackedCachePathsLoadTask;
    private IReadOnlyList<ThumbnailCacheTarget> _cacheTargets = [];

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
                _trackedCachePaths.Clear();
                _knownSourceStamps.Clear();
                _sourceStampValidationTimes.Clear();
                _knownCacheUris.Clear();
                _knownCacheUriOrder.Clear();
                _trackedCachePathsLoadTask = null;
            }
        }
        Directory.CreateDirectory(CacheRoot);
        lock (_cacheEntryLock)
        {
            _trackedCachePathsLoadTask ??= LoadAndMergeTrackedCachePathsAsync();
        }
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
            _trackedCachePaths.Clear();
            _knownSourceStamps.Clear();
            _sourceStampValidationTimes.Clear();
            _knownCacheUris.Clear();
            _knownCacheUriOrder.Clear();
            _trackedCachePathsLoadTask = LoadAndMergeTrackedCachePathsAsync();
        }
        return new ThumbnailCacheMoveResult(movedPaths.Count, failedCount, CacheRoot);
    }

    public void ConfigureTargetDirectories(IEnumerable<string> targetDirectories)
    {
        var candidates = targetDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(CreateThumbnailCacheTarget)
            .OrderBy(target => target.Path.Length)
            .ThenBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cacheTargets = new List<ThumbnailCacheTarget>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var covered = false;
            foreach (var existing in cacheTargets)
            {
                if (IsWithinCacheTarget(candidate.Path, existing))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                cacheTargets.Add(candidate);
            }
        }

        _cacheTargets = cacheTargets;
    }

    private static ThumbnailCacheTarget CreateThumbnailCacheTarget(string path)
    {
        var normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(path);
        if (!string.IsNullOrWhiteSpace(volumeRoot) &&
            string.Equals(
                normalized,
                volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = volumeRoot;
        }

        var prefixRoot = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return new ThumbnailCacheTarget(
            normalized,
            normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : prefixRoot + Path.DirectorySeparatorChar,
            normalized.EndsWith(Path.AltDirectorySeparatorChar) ? normalized : prefixRoot + Path.AltDirectorySeparatorChar);
    }

    public void InvalidateCacheTracking()
    {
        lock (_cacheEntryLock)
        {
            _trackedCachePaths.Clear();
            _knownSourceStamps.Clear();
            _sourceStampValidationTimes.Clear();
            _knownCacheUris.Clear();
            _knownCacheUriOrder.Clear();
            _trackedCachePathsLoadTask = LoadAndMergeTrackedCachePathsAsync();
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
            _trackedCachePaths.ExceptWith(cachePaths);
            foreach (var cachePath in cachePaths)
            {
                _knownCacheUris.Remove(cachePath);
            }
            foreach (var sourcePath in entries.Select(entry => entry.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _knownSourceStamps.Remove(sourcePath);
                _sourceStampValidationTimes.Remove(sourcePath);
            }
        }
        return entries.Length;
    }

    public Task<string?> GetOrCreateThumbnailUriAsync(
        GalleryItemDto item,
        int priority = 0,
        bool forceRefresh = false,
        ThumbnailCropAdjustmentDto? cropAdjustment = null) =>
        GetOrCreateThumbnailUriAsync(item.Path, priority, forceRefresh, cropAdjustment);

    public async Task<string?> GetOrCreateThumbnailUriAsync(
        string sourcePath,
        int priority = 0,
        bool forceRefresh = false,
        ThumbnailCropAdjustmentDto? cropAdjustment = null)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!IsCacheTarget(sourcePath))
        {
            return null;
        }

        var normalizedAdjustment = NormalizeCropAdjustment(cropAdjustment);
        if (!forceRefresh && TryGetKnownSourceStamp(sourcePath, out var knownSourceStamp))
        {
            var knownCachePath = GetCachePath(sourcePath, knownSourceStamp, normalizedAdjustment.CacheKey);
            if (TryGetCacheRevision(knownCachePath, out var knownCacheRevision))
            {
                QueueKnownSourceStampValidation(sourcePath, knownSourceStamp);
                return ToCacheUri(knownCachePath, knownCacheRevision);
            }
        }

        if (!TryGetSourceStamp(sourcePath, out var sourceStamp))
        {
            return null;
        }
        RememberSourceStamp(sourcePath, sourceStamp);
        var cachePath = GetCachePath(sourcePath, sourceStamp, normalizedAdjustment.CacheKey);
        if (!forceRefresh && TryGetCacheRevision(cachePath, out var cacheRevision))
        {
            EnsureCacheEntryTracked(cachePath, sourcePath, sourceStamp);
            return ToCacheUri(cachePath, cacheRevision);
        }

        ThumbnailInflightRequest request;
        lock (_inflightRequestLock)
        {
            if (_inflightRequests.TryGetValue(cachePath, out var inflightRequest))
            {
                request = inflightRequest;
                RaiseThumbnailGenerationPriority(request.Work, priority);
            }
            else
            {
                request = CreateAndTrackThumbnailRequest(
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
            return await request.Task;
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

    public async Task<string?> GetOrCreateThumbnailDataUriAsync(
        string sourcePath,
        int priority = 0,
        ThumbnailCropAdjustmentDto? cropAdjustment = null)
    {
        var thumbnailUri = await GetOrCreateThumbnailUriAsync(
            sourcePath,
            priority,
            forceRefresh: false,
            cropAdjustment);
        if (string.IsNullOrWhiteSpace(thumbnailUri))
        {
            return null;
        }

        sourcePath = Path.GetFullPath(sourcePath);
        if (!TryGetSourceStamp(sourcePath, out var sourceStamp))
        {
            return null;
        }

        var normalizedAdjustment = NormalizeCropAdjustment(cropAdjustment);
        var cachePath = GetCachePath(sourcePath, sourceStamp, normalizedAdjustment.CacheKey);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        // Dynamic tool image inputs are self-contained so the Codex child process
        // never needs access to the WebView2-only virtual thumbnail host.
        var bytes = await File.ReadAllBytesAsync(cachePath);
        return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
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

            if (!TryGetSourceStamp(sourcePath, out var sourceStamp))
            {
                return null;
            }
            var normalizedAdjustment = NormalizeCropAdjustment(cropAdjustment);
            var cachePath = GetCachePath(sourcePath, sourceStamp, normalizedAdjustment.CacheKey);
            if (!TryGetCacheRevision(cachePath, out var cacheRevision))
            {
                return null;
            }

            EnsureCacheEntryTracked(cachePath, sourcePath, sourceStamp);
            return ToCacheUri(cachePath, cacheRevision);
        }
        catch
        {
            return null;
        }
    }

    private ThumbnailInflightRequest CreateAndTrackThumbnailRequest(
        string sourcePath,
        string sourceStamp,
        string cachePath,
        ThumbnailCropAdjustment cropAdjustment,
        int priority,
        bool forceRefresh)
    {
        var work = new ThumbnailWork(() => CreateThumbnail(sourcePath, cachePath, cropAdjustment));
        var task = CompleteAsync();
        return new ThumbnailInflightRequest(work, task);

        async Task<string?> CompleteAsync()
        {
            try
            {
                if (forceRefresh && File.Exists(cachePath))
                {
                    ForgetCacheUri(cachePath);
                    File.Delete(cachePath);
                }

                QueueThumbnailGeneration(work, priority);
                var created = await work.Completion.Task;
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
    }

    private void EnsureCacheEntryTracked(string cachePath, string sourcePath, string sourceStamp)
    {
        RememberSourceStamp(sourcePath, sourceStamp);
        var requiresUpsert = false;
        lock (_cacheEntryLock)
        {
            requiresUpsert = _trackedCachePaths.Add(cachePath);
        }
        if (!requiresUpsert)
        {
            return;
        }

        try
        {
            _database.UpsertThumbnailCacheEntry(cachePath, sourcePath, sourceStamp);
        }
        catch
        {
            lock (_cacheEntryLock)
            {
                _trackedCachePaths.Remove(cachePath);
                _knownCacheUris.Remove(cachePath);
            }
            throw;
        }
    }

    public ThumbnailCacheMaintenanceResult MaintainCache()
    {
        var staleEntries = new List<string>();
        var staleSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _database.ListThumbnailCacheEntries())
        {
            var sourceExists = TryGetSourceStamp(entry.SourcePath, out var currentStamp);
            if (!sourceExists ||
                !File.Exists(entry.CachePath) ||
                !string.Equals(entry.SourceStamp, currentStamp, StringComparison.Ordinal))
            {
                DeleteCacheFile(entry.CachePath);
                staleEntries.Add(entry.CachePath);
                staleSourcePaths.Add(entry.SourcePath);
            }
        }

        _database.DeleteThumbnailCacheEntries(staleEntries);
        if (staleEntries.Count > 0)
        {
            lock (_cacheEntryLock)
            {
                _trackedCachePaths.ExceptWith(staleEntries);
                foreach (var cachePath in staleEntries)
                {
                    _knownCacheUris.Remove(cachePath);
                }
                foreach (var sourcePath in staleSourcePaths)
                {
                    _knownSourceStamps.Remove(sourcePath);
                    _sourceStampValidationTimes.Remove(sourcePath);
                }
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
        if (_cacheTargets.Count == 0)
        {
            throw new InvalidOperationException("再構築するには、サムネイルキャッシュの対象ディレクトリを登録してください。");
        }

        var deletedCount = ClearCache();
        var sources = EnumerateRebuildSources().ToArray();
        var generatedCount = 0;
        for (var index = 0; index < sources.Length; index++)
        {
            var sourcePath = sources[index];
            var uri = await GetOrCreateThumbnailUriAsync(sourcePath, priority: 1_000);
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
            _trackedCachePaths.Clear();
            _knownSourceStamps.Clear();
            _sourceStampValidationTimes.Clear();
            _knownCacheUris.Clear();
            _knownCacheUriOrder.Clear();
            _trackedCachePathsLoadTask = Task.CompletedTask;
        }
        return files.Length;
    }

    private async Task LoadAndMergeTrackedCachePathsAsync()
    {
        IReadOnlyList<ThumbnailCacheEntryDto> loadedEntries;
        try
        {
            loadedEntries = await Task.Run(_database.ListThumbnailCacheEntries);
        }
        catch
        {
            return;
        }

        lock (_cacheEntryLock)
        {
            foreach (var entry in loadedEntries)
            {
                _trackedCachePaths.Add(entry.CachePath);
                _knownSourceStamps[entry.SourcePath] = entry.SourceStamp;
            }
        }
    }

    private bool TryGetKnownSourceStamp(string sourcePath, out string sourceStamp)
    {
        lock (_cacheEntryLock)
        {
            return _knownSourceStamps.TryGetValue(sourcePath, out sourceStamp!);
        }
    }

    private void RememberSourceStamp(string sourcePath, string sourceStamp)
    {
        lock (_cacheEntryLock)
        {
            _knownSourceStamps[sourcePath] = sourceStamp;
        }
    }

    private void QueueKnownSourceStampValidation(string sourcePath, string expectedStamp)
    {
        var now = DateTime.UtcNow;
        lock (_cacheEntryLock)
        {
            if (_sourceStampValidationTimes.TryGetValue(sourcePath, out var lastValidation) &&
                now - lastValidation < SourceStampValidationInterval)
            {
                return;
            }
            _sourceStampValidationTimes[sourcePath] = now;
        }

        _pendingSourceStampValidations.Enqueue((sourcePath, expectedStamp));
        if (Interlocked.CompareExchange(ref _sourceStampValidationWorkerScheduled, 1, 0) == 0)
        {
            _ = Task.Run(ProcessPendingSourceStampValidations);
        }
    }

    private void ProcessPendingSourceStampValidations()
    {
        while (true)
        {
            while (_pendingSourceStampValidations.TryDequeue(out var validation))
            {
                try
                {
                    if (!TryGetSourceStamp(validation.SourcePath, out var currentStamp) ||
                        !string.Equals(currentStamp, validation.ExpectedStamp, StringComparison.Ordinal))
                    {
                        InvalidateSourcesUnderPath(validation.SourcePath);
                    }
                }
                catch
                {
                    // Validation is opportunistic. A later request retries after the interval.
                }
            }

            Interlocked.Exchange(ref _sourceStampValidationWorkerScheduled, 0);
            if (_pendingSourceStampValidations.IsEmpty ||
                Interlocked.CompareExchange(ref _sourceStampValidationWorkerScheduled, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private IEnumerable<string> EnumerateRebuildSources()
    {
        foreach (var target in _cacheTargets)
        {
            var targetPath = target.Path;
            if (!Directory.Exists(targetPath))
            {
                continue;
            }

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
                              (IsImagePath(file.Name) ||
                               IsArchivePath(file.Name) ||
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
        if (_cacheTargets.Count == 0)
        {
            return true;
        }

        foreach (var target in _cacheTargets)
        {
            if (IsWithinCacheTarget(sourcePath, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWithinCacheTarget(string path, ThumbnailCacheTarget target)
    {
        return string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(target.PrimaryChildPrefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(target.AlternateChildPrefix, StringComparison.OrdinalIgnoreCase);
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

    private void QueueThumbnailGeneration(ThumbnailWork work, int priority)
    {
        lock (_generationQueueLock)
        {
            var normalizedPriority = Math.Clamp(priority, 0, 1_000_000);
            work.TryRaisePriority(normalizedPriority);
            _generationQueue.Enqueue(work, (-normalizedPriority, _generationSequence++));
            StartQueuedGenerations();
        }
    }

    private void RaiseThumbnailGenerationPriority(ThumbnailWork work, int priority)
    {
        var normalizedPriority = Math.Clamp(priority, 0, 1_000_000);
        lock (_generationQueueLock)
        {
            if (work.HasStarted || !work.TryRaisePriority(normalizedPriority))
            {
                return;
            }

            // PriorityQueue has no in-place priority update. Re-enqueue the same work;
            // the stale queue entry is ignored after TryStart succeeds once.
            _generationQueue.Enqueue(work, (-normalizedPriority, _generationSequence++));
            StartQueuedGenerations();
        }
    }

    private void StartQueuedGenerations()
    {
        while (_activeGenerationCount < MaximumConcurrentGenerations && _generationQueue.TryDequeue(out var work, out _))
        {
            if (!work.TryStart())
            {
                continue;
            }

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

        SetFolderCover(parentPath, fullSourcePath);
        return parentPath;
    }

    public string SetFolderCover(string folderPath, string sourcePath)
    {
        var fullFolderPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullFolderPath))
        {
            throw new DirectoryNotFoundException("サムネイル設定先のフォルダが見つかりません。: " + fullFolderPath);
        }

        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath) && !Directory.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("対象のファイルまたはフォルダが見つかりません。", fullSourcePath);
        }

        var coverPath = Path.Combine(fullFolderPath, ZipPlaCoverFileName);
        if (_ffmpegService.SupportsVideo(fullSourcePath))
        {
            if (!_ffmpegService.TryCreateThumbnail(fullSourcePath, coverPath))
            {
                throw new InvalidOperationException("動画からサムネイルを生成できませんでした。FFmpeg設定を確認してください。");
            }

            return coverPath;
        }

        using var image = OpenRepresentativeImage(fullSourcePath)
            ?? throw new InvalidOperationException("サムネイルに利用できる画像が見つかりません。");
        SaveCoverImage(image, coverPath);
        return coverPath;
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
        if (IsImagePath(sourcePath) && File.Exists(sourcePath))
        {
            return Image.Load(ThumbnailDecoderOptions, sourcePath);
        }

        if (IsArchivePath(sourcePath) && File.Exists(sourcePath))
        {
            return OpenFirstImageInArchive(sourcePath);
        }

        if (Directory.Exists(sourcePath))
        {
            var imagePath = FindFirstImageInFolder(sourcePath);
            return imagePath is null ? null : Image.Load(ThumbnailDecoderOptions, imagePath);
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

            string? firstImagePath = null;
            var relativePathStart = folderPath
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Length + 1;
            foreach (var path in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
            {
                if (!IsImagePath(path) ||
                    Path.GetFileName(path.AsSpan()).Equals(
                        ZipPlaCoverFileName.AsSpan(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (firstImagePath is null || CompareNaturally(path, firstImagePath, relativePathStart) < 0)
                {
                    firstImagePath = path;
                }
            }
            return firstImagePath;
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
        var entries = archive.Entries;
        var inspectedEntryCount = Math.Min(entries.Count, MaximumArchiveEntriesToInspect);
        ZipArchiveEntry? firstImageEntry = null;
        for (var index = 0; index < inspectedEntryCount; index++)
        {
            var entry = entries[index];
            if (string.IsNullOrEmpty(entry.Name) ||
                !IsImagePath(entry.FullName))
            {
                continue;
            }

            if (firstImageEntry is null || CompareRepresentativeEntries(entry, firstImageEntry) < 0)
            {
                firstImageEntry = entry;
            }
        }

        if (firstImageEntry is not null)
        {
            try
            {
                using var stream = firstImageEntry.Open();
                return Image.Load(ThumbnailDecoderOptions, stream);
            }
            catch
            {
                // Broken first images fall back to the remaining naturally ordered candidates.
                foreach (var entry in archive.Entries
                             .Take(MaximumArchiveEntriesToInspect)
                             .Where(candidate =>
                                 !ReferenceEquals(candidate, firstImageEntry) &&
                                 !string.IsNullOrEmpty(candidate.Name) &&
                                 IsImagePath(candidate.FullName))
                             .OrderBy(candidate => GetRepresentativeImagePriority(candidate.Name))
                             .ThenBy(candidate => candidate.FullName, Comparer<string>.Create(CompareNaturally)))
                {
                    try
                    {
                        using var stream = entry.Open();
                        return Image.Load(ThumbnailDecoderOptions, stream);
                    }
                    catch
                    {
                        // Broken or unsupported images do not prevent later pages from becoming the cover.
                    }
                }
            }
        }

        if (depth >= MaximumNestedArchiveDepth)
        {
            return null;
        }

        foreach (var entry in archive.Entries
                     .Take(MaximumArchiveEntriesToInspect)
                     .Where(candidate => IsArchivePath(candidate.Name))
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

    private static bool IsImagePath(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsArchivePath(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cbz", StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareRepresentativeEntries(ZipArchiveEntry left, ZipArchiveEntry right)
    {
        var priorityComparison = GetRepresentativeImagePriority(left.Name)
            .CompareTo(GetRepresentativeImagePriority(right.Name));
        return priorityComparison != 0
            ? priorityComparison
            : CompareNaturally(left.FullName, right.FullName);
    }

    private static int GetRepresentativeImagePriority(string name) =>
        string.Equals(name, ZipPlaCoverFileName, StringComparison.OrdinalIgnoreCase) ? 0 : 1;

    private static int CompareNaturally(string? left, string? right) =>
        CompareNaturally(left, right, 0);

    private static int CompareNaturally(string? left, string? right, int startIndex)
    {
        left ??= string.Empty;
        right ??= string.Empty;

        var leftIndex = Math.Min(Math.Max(startIndex, 0), left.Length);
        var rightIndex = Math.Min(Math.Max(startIndex, 0), right.Length);
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

    private string GetCachePath(string sourcePath, string sourceStamp, string cropVariant)
    {
        var key = ComputeCacheKey(sourcePath, sourceStamp, cropVariant);
        return Path.Combine(CacheRoot, key[..2], key + ".jpg");
    }

    private static string ComputeCacheKey(string sourcePath, string sourceStamp, string cropVariant)
    {
        var characterCount = sourcePath.Length +
                             sourceStamp.Length +
                             ThumbnailCacheVersion.Length +
                             cropVariant.Length +
                             3;
        var maximumByteCount = Encoding.UTF8.GetMaxByteCount(characterCount);
        byte[]? rentedBuffer = null;
        Span<byte> utf8Buffer = maximumByteCount <= 1_024
            ? stackalloc byte[maximumByteCount]
            : (rentedBuffer = ArrayPool<byte>.Shared.Rent(maximumByteCount));
        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(sourcePath.AsSpan(), utf8Buffer);
            utf8Buffer[bytesWritten++] = (byte)'|';
            bytesWritten += Encoding.UTF8.GetBytes(sourceStamp.AsSpan(), utf8Buffer[bytesWritten..]);
            utf8Buffer[bytesWritten++] = (byte)'|';
            bytesWritten += Encoding.UTF8.GetBytes(ThumbnailCacheVersion.AsSpan(), utf8Buffer[bytesWritten..]);
            utf8Buffer[bytesWritten++] = (byte)'|';
            bytesWritten += Encoding.UTF8.GetBytes(cropVariant.AsSpan(), utf8Buffer[bytesWritten..]);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(utf8Buffer[..bytesWritten], hash);
            return Convert.ToHexStringLower(hash);
        }
        finally
        {
            if (rentedBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }
        }
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

        public string CacheKey { get; } =
            $"{HorizontalOffsetPercent:0.##}:{VerticalOffsetPercent:0.##}:{ScalePercent:0.##}";
    }

    private static bool TryGetSourceStamp(string sourcePath, out string sourceStamp)
    {
        try
        {
            var fileInfo = new FileInfo(sourcePath);
            if (fileInfo.Exists)
            {
                sourceStamp = $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
                return true;
            }

            var directoryInfo = new DirectoryInfo(sourcePath);
            if (directoryInfo.Exists)
            {
                sourceStamp = directoryInfo.LastWriteTimeUtc.Ticks.ToString();
                return true;
            }
        }
        catch
        {
            sourceStamp = string.Empty;
            return false;
        }

        sourceStamp = string.Empty;
        return false;
    }

    private static bool TryGetCacheRevision(string cachePath, out long revision)
    {
        try
        {
            var cacheFile = new FileInfo(cachePath);
            if (cacheFile.Exists)
            {
                revision = cacheFile.LastWriteTimeUtc.Ticks;
                return true;
            }
        }
        catch
        {
            // Match File.Exists semantics for inaccessible or transient cache entries.
        }

        revision = 0;
        return false;
    }

    private string ToCacheUri(string cachePath, long? knownRevision = null)
    {
        lock (_cacheEntryLock)
        {
            if (_knownCacheUris.TryGetValue(cachePath, out var cached))
            {
                return cached.Uri;
            }
        }

        var key = Path.GetFileNameWithoutExtension(cachePath);
        var resourcePath = key.Length >= 2
            ? key[..2] + "/" + key + ".jpg"
            : Uri.EscapeDataString(Path.GetFileName(cachePath));
        var revision = knownRevision ??
                       (TryGetCacheRevision(cachePath, out var detectedRevision) ? detectedRevision : 0);
        var uri = "https://gallerybrowser-cache.local/" + resourcePath + "?v=" + revision;
        lock (_cacheEntryLock)
        {
            var sequence = ++_knownCacheUriSequence;
            _knownCacheUris[cachePath] = (uri, sequence);
            _knownCacheUriOrder.Enqueue((cachePath, sequence));
            while (_knownCacheUris.Count > MaximumKnownCacheUris &&
                   _knownCacheUriOrder.TryDequeue(out var oldest))
            {
                if (_knownCacheUris.TryGetValue(oldest.Path, out var current) &&
                    current.Sequence == oldest.Sequence)
                {
                    _knownCacheUris.Remove(oldest.Path);
                }
            }
            if (_knownCacheUriOrder.Count > MaximumKnownCacheUris * 2)
            {
                _knownCacheUriOrder.Clear();
                foreach (var (path, entry) in _knownCacheUris)
                {
                    _knownCacheUriOrder.Enqueue((path, entry.Sequence));
                }
            }
        }
        return uri;
    }

    private void ForgetCacheUri(string cachePath)
    {
        lock (_cacheEntryLock)
        {
            _knownCacheUris.Remove(cachePath);
        }
    }

    private sealed class ThumbnailWork
    {
        private int _priority;
        private int _started;

        public ThumbnailWork(Func<bool> generation)
        {
            Generation = generation;
            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Func<bool> Generation { get; }
        public TaskCompletionSource<bool> Completion { get; }
        public bool HasStarted => Volatile.Read(ref _started) != 0;

        public bool TryRaisePriority(int priority)
        {
            var current = Volatile.Read(ref _priority);
            while (priority > current)
            {
                var previous = Interlocked.CompareExchange(ref _priority, priority, current);
                if (previous == current)
                {
                    return true;
                }
                current = previous;
            }
            return false;
        }

        public bool TryStart() => Interlocked.CompareExchange(ref _started, 1, 0) == 0;
    }

    private readonly record struct ThumbnailCacheTarget(
        string Path,
        string PrimaryChildPrefix,
        string AlternateChildPrefix);

    private sealed record ThumbnailInflightRequest(ThumbnailWork Work, Task<string?> Task);
}

public sealed record ThumbnailCacheMaintenanceResult(int StaleEntriesRemoved, int OrphanFilesRemoved);

public sealed record ThumbnailCacheRebuildResult(int DeletedCacheFiles, int GeneratedThumbnails, int ScannedSources);

public sealed record ThumbnailCacheMoveResult(int MovedFiles, int FailedFiles, string CacheRoot);
