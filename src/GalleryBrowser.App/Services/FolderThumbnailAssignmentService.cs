using System.IO;

namespace GalleryBrowser.Services;

internal sealed record FolderThumbnailAssignmentResult(
    int ScanTargetCount,
    int CreatorFolderCount,
    int MissingFolderCount,
    int AssignedFolderCount,
    int ExistingFolderCount,
    int NoSourceFolderCount,
    IReadOnlyList<string> AssignedFolderPaths,
    IReadOnlyList<string> Errors);

internal sealed class FolderThumbnailAssignmentService
{
    private sealed record FolderCandidate(
        string CoverPath,
        bool OriginatesFromExistingCover,
        int SubtreeMediaCount,
        int SourceImageCount,
        DateTime FolderCreationTimeUtc,
        long SourceLength);

    private sealed record FolderState(
        FolderCandidate? Candidate,
        int SubtreeMediaCount);

    private static readonly HashSet<string> SupportedArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip",
            ".cbz"
        };

    private static readonly HashSet<string> CountedArchiveExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip",
            ".cbz",
            ".rar",
            ".cbr",
            ".7z"
        };

    private static readonly HashSet<string> SupportedVideoExtensions =
        FfmpegService.DefaultSupportedExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly GalleryDatabase _database;
    private readonly FileBrowserService _fileBrowser;
    private readonly ThumbnailService _thumbnailService;
    private IReadOnlyDictionary<string, int> _knownImageCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public FolderThumbnailAssignmentService(
        GalleryDatabase database,
        FileBrowserService fileBrowser,
        ThumbnailService thumbnailService)
    {
        _database = database;
        _fileBrowser = fileBrowser;
        _thumbnailService = thumbnailService;
    }

    public FolderThumbnailAssignmentResult AssignMissingCovers(
        IReadOnlyList<string> requestedCategories,
        CancellationToken cancellationToken)
    {
        var categories = requestedCategories
            .Where(category =>
                string.Equals(category, "ai", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "doujin_anime", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (categories.Length == 0)
        {
            throw new InvalidOperationException("対象区分は生成AI作品または同人アニメを指定してください。");
        }

        var targets = categories
            .SelectMany(category => _database.ListGalleryScanTargets(category))
            .Where(target => Directory.Exists(target.Path))
            .GroupBy(target => Path.GetFullPath(target.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (targets.Length == 0)
        {
            throw new InvalidOperationException("対象区分に有効なGallery走査フォルダが設定されていません。");
        }

        var creatorFolders = targets
            .SelectMany(target => EnumerateCreatorFolders(target.Path, cancellationToken))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var assignedPaths = new List<string>();
        var errors = new List<string>();
        var missingFolderCount = 0;
        var existingFolderCount = 0;
        var noSourceFolderCount = 0;

        foreach (var creatorFolder in creatorFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var creatorMediaPaths = EnumerateSupportedMediaFiles(
                    creatorFolder,
                    cancellationToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _knownImageCounts = _database.ListGalleryImageCountsByPaths(creatorMediaPaths);
            DirectoryInfo[] childFolders;
            try
            {
                childFolders = new DirectoryInfo(creatorFolder)
                    .EnumerateDirectories()
                    .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddError(errors, $"{creatorFolder}: {ex.Message}");
                continue;
            }

            foreach (var childFolder in childFolders)
            {
                ProcessFolder(
                    childFolder,
                    assignedPaths,
                    errors,
                    ref missingFolderCount,
                    ref existingFolderCount,
                    ref noSourceFolderCount,
                    cancellationToken);
            }
        }

        return new FolderThumbnailAssignmentResult(
            targets.Length,
            creatorFolders.Length,
            missingFolderCount,
            assignedPaths.Count,
            existingFolderCount,
            noSourceFolderCount,
            assignedPaths,
            errors);
    }

    private FolderState ProcessFolder(
        DirectoryInfo folder,
        List<string> assignedPaths,
        List<string> errors,
        ref int missingFolderCount,
        ref int existingFolderCount,
        ref int noSourceFolderCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DirectoryInfo[] childFolders;
        FileInfo[] directFiles;
        try
        {
            childFolders = folder
                .EnumerateDirectories()
                .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .ToArray();
            directFiles = folder.EnumerateFiles().ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddError(errors, $"{folder.FullName}: {ex.Message}");
            return new FolderState(null, 0);
        }

        var childStates = new List<FolderState>(childFolders.Length);
        foreach (var childFolder in childFolders)
        {
            childStates.Add(ProcessFolder(
                childFolder,
                assignedPaths,
                errors,
                ref missingFolderCount,
                ref existingFolderCount,
                ref noSourceFolderCount,
                cancellationToken));
        }

        var directMedia = directFiles
            .Where(file => IsSupportedMedia(file.Extension))
            .ToArray();
        var subtreeMediaCount =
            directFiles.Count(file => IsCountedMedia(file.Extension)) +
            childStates.Sum(state => state.SubtreeMediaCount);
        var coverPath = Path.Combine(folder.FullName, ThumbnailService.ZipPlaCoverFileName);
        if (File.Exists(coverPath))
        {
            existingFolderCount++;
            return new FolderState(
                new FolderCandidate(
                    coverPath,
                    true,
                    subtreeMediaCount,
                    0,
                    GetCreationTimeUtc(folder),
                    0),
                subtreeMediaCount);
        }

        missingFolderCount++;
        var existingCandidates = childStates
            .Select(state => state.Candidate)
            .Where(candidate => candidate?.OriginatesFromExistingCover == true)
            .Cast<FolderCandidate>()
            .OrderByDescending(candidate => candidate.SubtreeMediaCount)
            .ThenBy(candidate => candidate.FolderCreationTimeUtc)
            .ThenBy(candidate => candidate.CoverPath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (TryAssignFromChildCandidates(
                folder,
                existingCandidates,
                subtreeMediaCount,
                true,
                assignedPaths,
                errors,
                out var existingResult))
        {
            return new FolderState(existingResult, subtreeMediaCount);
        }

        var directCandidates = directMedia
            .Select(file => new
            {
                File = file,
                ImageCount = GetImageCount(file),
                Length = GetFileLength(file)
            })
            .OrderByDescending(candidate => candidate.ImageCount)
            .ThenByDescending(candidate => candidate.Length)
            .ThenBy(candidate => candidate.File.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        foreach (var directCandidate in directCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var assignedCover = _thumbnailService.SetFolderCover(
                    folder.FullName,
                    directCandidate.File.FullName);
                assignedPaths.Add(folder.FullName);
                return new FolderState(
                    new FolderCandidate(
                        assignedCover,
                        false,
                        subtreeMediaCount,
                        directCandidate.ImageCount,
                        GetCreationTimeUtc(folder),
                        directCandidate.Length),
                    subtreeMediaCount);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddError(
                    errors,
                    $"{folder.FullName} <- {directCandidate.File.FullName}: {ex.Message}");
            }
        }

        var generatedCandidates = childStates
            .Select(state => state.Candidate)
            .Where(candidate => candidate is not null)
            .Cast<FolderCandidate>()
            .OrderByDescending(candidate => candidate.SourceImageCount)
            .ThenByDescending(candidate => candidate.SubtreeMediaCount)
            .ThenByDescending(candidate => candidate.SourceLength)
            .ThenBy(candidate => candidate.FolderCreationTimeUtc)
            .ThenBy(candidate => candidate.CoverPath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (TryAssignFromChildCandidates(
                folder,
                generatedCandidates,
                subtreeMediaCount,
                false,
                assignedPaths,
                errors,
                out var generatedResult))
        {
            return new FolderState(generatedResult, subtreeMediaCount);
        }

        noSourceFolderCount++;
        return new FolderState(null, subtreeMediaCount);
    }

    private bool TryAssignFromChildCandidates(
        DirectoryInfo folder,
        IReadOnlyList<FolderCandidate> candidates,
        int subtreeMediaCount,
        bool originatesFromExistingCover,
        List<string> assignedPaths,
        List<string> errors,
        out FolderCandidate? result)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                var assignedCover = _thumbnailService.SetFolderCover(
                    folder.FullName,
                    candidate.CoverPath);
                assignedPaths.Add(folder.FullName);
                result = new FolderCandidate(
                    assignedCover,
                    originatesFromExistingCover || candidate.OriginatesFromExistingCover,
                    subtreeMediaCount,
                    candidate.SourceImageCount,
                    GetCreationTimeUtc(folder),
                    candidate.SourceLength);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AddError(errors, $"{folder.FullName} <- {candidate.CoverPath}: {ex.Message}");
            }
        }

        result = null;
        return false;
    }

    private int GetImageCount(FileInfo file)
    {
        if (_knownImageCounts.TryGetValue(file.FullName, out var imageCount))
        {
            return Math.Max(0, imageCount);
        }

        return SupportedArchiveExtensions.Contains(file.Extension)
            ? Math.Max(0, _fileBrowser.GetArchivePageCount(file.FullName) ?? 0)
            : 0;
    }

    private static IEnumerable<string> EnumerateCreatorFolders(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootPath);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = new DirectoryInfo(current);
            if (IsCreatorFolder(directory.Name))
            {
                yield return directory.FullName;
                continue;
            }

            DirectoryInfo[] children;
            try
            {
                children = directory
                    .EnumerateDirectories()
                    .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Push(child.FullName);
            }
        }
    }

    private static IEnumerable<string> EnumerateSupportedMediaFiles(
        string rootPath,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(current).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is DirectoryInfo directory)
                {
                    if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(directory.FullName);
                    }
                }
                else if (entry is FileInfo file && IsSupportedMedia(file.Extension))
                {
                    yield return file.FullName;
                }
            }
        }
    }

    private static bool IsCreatorFolder(string name) =>
        name.Length >= 3 && name[0] == '【' && name[^1] == '】';

    private static bool IsSupportedMedia(string extension) =>
        SupportedArchiveExtensions.Contains(extension) ||
        SupportedVideoExtensions.Contains(extension);

    private static bool IsCountedMedia(string extension) =>
        CountedArchiveExtensions.Contains(extension) ||
        SupportedVideoExtensions.Contains(extension);

    private static DateTime GetCreationTimeUtc(DirectoryInfo folder)
    {
        try
        {
            return folder.CreationTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MaxValue;
        }
    }

    private static long GetFileLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void AddError(List<string> errors, string message)
    {
        const int maximumStoredErrors = 100;
        if (errors.Count < maximumStoredErrors)
        {
            errors.Add(message);
        }
    }
}
