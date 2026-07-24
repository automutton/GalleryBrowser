using GalleryBrowser.Models;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GalleryBrowser.Services;

public sealed record FolderOrganizationResult(
    IReadOnlyList<string> CreatedFolders,
    IReadOnlyList<string> MovedPaths,
    IReadOnlyList<string> SkippedPaths,
    IReadOnlyList<string> Errors);

public sealed record FileTransferProgress(
    long BytesTransferred,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    string CurrentPath,
    bool IsMove);

public sealed record GidAssignmentResult(
    int TargetFileCount,
    int AssignedCount,
    int ExistingGidCount,
    int SkippedExistingCount,
    IReadOnlyList<string> Errors);

public sealed record CreatorFolderRenameResult(
    string OldPath,
    string NewPath,
    string Creator,
    bool Renamed);

public sealed class FileBrowserService
{
    private const int MaximumEntries = 2_000;
    private static readonly Regex GidTagRegex = new(
        @"\{gid=(?<gid>[^{}]+)\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ArchiveImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".avif", ".heic", ".heif", ".jxl"
    };
    private readonly Dictionary<string, (long Length, long ModifiedTicks, int? PageCount)> _archivePageCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly GalleryDatabase _database;

    public FileBrowserService(GalleryDatabase database)
    {
        _database = database;
    }

    public FileBrowserDirectoryDto ListDirectory(string? requestedPath)
    {
        var path = ResolveDirectoryPath(requestedPath);
        var directory = new DirectoryInfo(path);
        var entries = new List<FileBrowserEntryDto>();
        var isTruncated = false;

        try
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (string.Equals(entry.Name, ThumbnailService.ZipPlaCoverFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entries.Count >= MaximumEntries)
                {
                    isTruncated = true;
                    break;
                }

                try
                {
                    var isDirectory = entry is DirectoryInfo;
                    var file = isDirectory ? null : (FileInfo)entry;
                    entries.Add(new FileBrowserEntryDto(
                        entry.Name,
                        FileNameRomanizer.Romanize(entry.Name),
                        entry.FullName,
                        isDirectory,
                        isDirectory ? string.Empty : Path.GetExtension(entry.Name),
                        file?.Length,
                        file is null ? null : GetArchivePageCount(file),
                        entry.CreationTimeUtc,
                        entry.LastAccessTimeUtc,
                        entry.LastWriteTimeUtc));
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep listing accessible siblings when one entry is protected.
                }
                catch (IOException)
                {
                    // A file can disappear while the directory is being read.
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("このフォルダにアクセスする権限がありません。", ex);
        }

        _database.SaveFileSearchMetadata(entries.Select(entry => new FileSearchMetadataDto(
            entry.Path,
            entry.Name,
            entry.RomanizedName)));

        var orderedEntries = entries
            .OrderBy(entry => !entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var parent = directory.Parent?.FullName;
        return new FileBrowserDirectoryDto(
            directory.FullName,
            parent,
            DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .OrderBy(root => root, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            orderedEntries,
            isTruncated);
    }

    private int? GetArchivePageCount(FileInfo file)
    {
        if (!string.Equals(file.Extension, ".zip", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(file.Extension, ".cbz", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_archivePageCounts.TryGetValue(file.FullName, out var cached) &&
            cached.Length == file.Length && cached.ModifiedTicks == file.LastWriteTimeUtc.Ticks)
        {
            return cached.PageCount;
        }

        int? pageCount;
        try
        {
            using var archive = ZipFile.OpenRead(file.FullName);
            pageCount = archive.Entries.Count(entry =>
                !string.IsNullOrEmpty(entry.Name) &&
                ArchiveImageExtensions.Contains(Path.GetExtension(entry.FullName)));
        }
        catch (InvalidDataException)
        {
            pageCount = null;
        }
        catch (IOException)
        {
            pageCount = null;
        }
        catch (UnauthorizedAccessException)
        {
            pageCount = null;
        }

        _archivePageCounts[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks, pageCount);
        return pageCount;
    }

    public IReadOnlyList<string> Copy(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        Action<FileTransferProgress>? progress = null)
    {
        return Transfer(sourcePaths, destinationDirectory, move: false, progress);
    }

    public IReadOnlyList<string> Move(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        Action<FileTransferProgress>? progress = null)
    {
        return Transfer(sourcePaths, destinationDirectory, move: true, progress);
    }

    public void Delete(IEnumerable<string> paths)
    {
        foreach (var path in NormalizeExistingPaths(paths))
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    public string CreateNewFolder(string directoryPath)
    {
        var directory = ResolveDirectoryPath(directoryPath);
        var nextNumber = Directory.EnumerateDirectories(directory)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => Regex.Match(name!, @"^\s*(\d{2})"))
            .Where(match => match.Success)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var folderName = nextNumber <= 0 ? "00_新規フォルダ" : $"{nextNumber:D2}_";
        var targetPath = Path.Combine(directory, folderName);
        if (Directory.Exists(targetPath) || File.Exists(targetPath))
        {
            throw new InvalidOperationException("新規フォルダ名が既に存在します。フォルダ一覧を更新してから再試行してください。");
        }

        Directory.CreateDirectory(targetPath);
        return targetPath;
    }

    public FolderOrganizationResult CreateFoldersAndMoveItems(IEnumerable<string> selectedPaths, bool separateFolders)
    {
        var sources = NormalizeExistingPaths(selectedPaths).ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException("移動する項目を選択してください。");
        }

        return separateFolders
            ? CreateSeparateFoldersAndMoveItems(sources)
            : CreateAggregateFolderAndMoveItems(sources);
    }

    public GidAssignmentResult AssignGids(
        IEnumerable<string> selectedFolderPaths,
        IEnumerable<string> extensions,
        int digitCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedExtensions = extensions
            .Where(extension => !string.IsNullOrWhiteSpace(extension))
            .Select(NormalizeGidExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedExtensions.Length == 0)
        {
            throw new InvalidOperationException("gid発番対象拡張子を選択してください。");
        }
        var extensionSet = normalizedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = selectedFolderPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();
        for (var index = folders.Count - 1; index >= 0; index--)
        {
            if (folders.Take(index).Any(parent => IsDescendantPath(folders[index], parent)))
            {
                folders.RemoveAt(index);
            }
        }
        if (folders.Count == 0)
        {
            throw new InvalidOperationException("gidを発行するフォルダを選択してください。");
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // A user-selected file may legitimately carry the System attribute,
            // especially on a network drive. Reparse-point directories are still
            // skipped so a junction cannot make the recursive scan leave the
            // selected folder or loop indefinitely.
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(folder, "*", enumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                allFiles.Add(file);
            }
        }

        var knownGids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new List<string>();
        var errors = new List<string>();
        var skippedExistingCount = 0;
        foreach (var file in allFiles.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            var matches = GidTagRegex.Matches(fileName);
            foreach (Match match in matches)
            {
                knownGids.Add(match.Groups["gid"].Value.Trim());
            }
            if (!extensionSet.Contains(Path.GetExtension(fileName)))
            {
                continue;
            }
            if (matches.Count > 0)
            {
                var gids = matches
                    .Select(match => match.Groups["gid"].Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (matches.Count != 1 ||
                    gids.Length != 1 ||
                    gids[0].Any(character => !char.IsAsciiLetterOrDigit(character)))
                {
                    errors.Add(
                        $"{file}: ファイル名のgidを一意なBase36値として解釈できません。ファイル名を修正してください。");
                    continue;
                }
                if (gids[0].Length != digitCount)
                {
                    // Files that were outside their registered path during a previous
                    // bulk migration can reappear with the old-width tag. Treat them as
                    // assignment targets so the stale tag is replaced instead of making
                    // every subsequent database scan fail permanently.
                    targets.Add(file);
                    continue;
                }
                skippedExistingCount++;
                continue;
            }
            targets.Add(file);
        }

        // Complete validation before reserving GIDs or renaming any file. This keeps a
        // malformed name from leaving an otherwise aborted scan partially modified.
        if (errors.Count > 0)
        {
            return new GidAssignmentResult(targets.Count, 0, 0, skippedExistingCount, errors);
        }

        if (targets.Count == 0)
        {
            return new GidAssignmentResult(0, 0, 0, skippedExistingCount, errors);
        }

        // A file can already have a GID in the database even when its physical
        // name does not contain the {gid=...} tag (for example, DB-only rows
        // preserved by a migration). Keep that identity and only reserve a new
        // GID for files that are genuinely unknown to the database.
        var existingGids = _database.GetExistingGidsForPaths(targets, digitCount);
        var newTargets = targets
            .Where(path => !existingGids.ContainsKey(path))
            .ToArray();
        var newGids = _database.ReserveGids(newTargets, digitCount, knownGids);
        var assignedGids = existingGids.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < newTargets.Length; index++)
        {
            assignedGids[newTargets[index]] = newGids[index];
        }

        var assignedCount = 0;
        var existingGidCount = 0;
        foreach (var source in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fileName = Path.GetFileName(source);
                var originalExtension = Path.GetExtension(fileName);
                var targetName = GidTagRegex.IsMatch(fileName)
                    ? GidTagRegex.Replace(fileName, $"{{gid={assignedGids[source]}}}", 1)
                    : $"{Path.GetFileNameWithoutExtension(fileName)}{{gid={assignedGids[source]}}}{originalExtension}";
                if (targetName.Length > 255)
                {
                    throw new InvalidOperationException("gid付与後のファイル名が255文字を超えます。");
                }
                var destination = Path.Combine(Path.GetDirectoryName(source)!, targetName);
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new IOException("gid付与後と同じ名前の項目が既に存在します。");
                }
                File.Move(source, destination);
                assignedCount++;
                if (existingGids.ContainsKey(source))
                {
                    existingGidCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                errors.Add($"{source}: {ex.Message}");
            }
        }

        return new GidAssignmentResult(targets.Count, assignedCount, existingGidCount, skippedExistingCount, errors);
    }

    private static string NormalizeGidExtension(string extension)
    {
        extension = extension.Trim();
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }
        if (extension.Length is <= 1 or > 32 ||
            extension.Skip(1).Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '_' or '-')))
        {
            throw new ArgumentException("gid発番対象拡張子が不正です。", nameof(extension));
        }
        return extension.ToLowerInvariant();
    }

    public void Rename(string path, string newName)
    {
        var source = NormalizeExistingPath(path);
        var trimmedName = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) ||
            trimmedName is "." or ".." ||
            trimmedName.Length > 255 ||
            trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("255文字以内の有効な名前を入力してください。");
        }

        var parent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("ルートフォルダの名前は変更できません。");
        var target = Path.Combine(parent, trimmedName);
        if (PathExists(target) && !PathEquals(source, target))
        {
            throw new InvalidOperationException("同じ名前の項目が既にあります。");
        }

        if (PathEquals(source, target))
        {
            return;
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    public CreatorFolderRenameResult ConvertToCreatorFolder(string path)
    {
        var source = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"作者フォルダ化するフォルダが見つかりません: {source}");
        }

        var parent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("ルートフォルダは作者フォルダ化できません。");
        var originalName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var creator = originalName.StartsWith('【') && originalName.EndsWith('】')
            ? originalName[1..^1].Trim()
            : originalName.Trim();
        if (string.IsNullOrWhiteSpace(creator))
        {
            throw new InvalidOperationException("作者名を取得できないフォルダは作者フォルダ化できません。");
        }

        var targetName = $"【{creator}】";
        if (targetName.Length > 255 || targetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("作者フォルダ化後の名前が255文字を超えるか、使用できない文字を含んでいます。");
        }

        var target = Path.Combine(parent, targetName);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return new CreatorFolderRenameResult(source, source, creator, false);
        }
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException($"作者フォルダ化後と同じ名前の項目が既に存在します: {targetName}");
        }

        Directory.Move(source, target);
        return new CreatorFolderRenameResult(source, target, creator, true);
    }

    private static IReadOnlyList<string> Transfer(
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        bool move,
        Action<FileTransferProgress>? progress)
    {
        var destination = ResolveDirectoryPath(destinationDirectory);
        var sources = NormalizeExistingPaths(sourcePaths).ToArray();
        var metrics = sources.Select(GetTransferMetrics).ToArray();
        var reporter = new FileTransferProgressReporter(
            progress,
            metrics.Sum(metric => metric.TotalBytes),
            metrics.Sum(metric => metric.FileCount),
            move);
        var transferredPaths = new List<string>();
        for (var sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
        {
            var source = sources[sourceIndex];
            var sourceMetrics = metrics[sourceIndex];
            if (PathEquals(source, destination))
            {
                throw new InvalidOperationException("同じ場所へは貼り付けできません。");
            }

            if (move && Path.GetDirectoryName(source) is { } sourceParent && PathEquals(sourceParent, destination))
            {
                throw new InvalidOperationException("同じフォルダ内へは移動できません。");
            }

            if (Directory.Exists(source) && IsDescendantPath(destination, source))
            {
                throw new InvalidOperationException("フォルダ自身の中には貼り付けできません。");
            }

            var target = GetAvailableTargetPath(source, destination);
            if (Directory.Exists(source))
            {
                if (move && IsSameVolume(source, target))
                {
                    Directory.Move(source, target);
                    reporter.CompleteMovedSource(source, sourceMetrics);
                }
                else
                {
                    CopyDirectory(source, target, reporter);
                    if (move)
                    {
                        Directory.Delete(source, recursive: true);
                    }
                }
            }
            else if (move && IsSameVolume(source, target))
            {
                File.Move(source, target);
                reporter.CompleteMovedSource(source, sourceMetrics);
            }
            else
            {
                CopyFile(source, target, reporter);
                if (move)
                {
                    File.Delete(source);
                }
            }

            transferredPaths.Add(target);
        }

        return transferredPaths;
    }

    private static FolderOrganizationResult CreateAggregateFolderAndMoveItems(IReadOnlyList<string> sources)
    {
        var createdFolders = new List<string>();
        var movedPaths = new List<string>();
        var skippedPaths = new List<string>();
        var errors = new List<string>();
        var parent = Path.GetDirectoryName(sources[0])
            ?? throw new InvalidOperationException("ルート項目はフォルダへ移動できません。");
        var folder = CreateUniqueFolder(parent, GetNormalizedFolderName(sources[0]));
        createdFolders.Add(folder);

        foreach (var source in sources)
        {
            if (!PathEquals(Path.GetDirectoryName(source) ?? string.Empty, parent))
            {
                skippedPaths.Add(source);
                continue;
            }

            try
            {
                var target = GetAvailableTargetPath(source, folder);
                MoveEntry(source, target);
                movedPaths.Add(target);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                break;
            }
        }

        RemoveEmptyCreatedFolder(folder, createdFolders);
        return new FolderOrganizationResult(createdFolders, movedPaths, skippedPaths, errors);
    }

    private static FolderOrganizationResult CreateSeparateFoldersAndMoveItems(IReadOnlyList<string> sources)
    {
        var createdFolders = new List<string>();
        var movedPaths = new List<string>();
        var skippedPaths = new List<string>();
        var errors = new List<string>();

        foreach (var source in sources)
        {
            string? folder = null;
            try
            {
                var parent = Path.GetDirectoryName(source)
                    ?? throw new InvalidOperationException("ルート項目はフォルダへ移動できません。");
                folder = CreateUniqueFolder(parent, GetNormalizedFolderName(source));
                createdFolders.Add(folder);
                var target = GetAvailableTargetPath(source, folder);
                MoveEntry(source, target);
                movedPaths.Add(target);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
                if (folder is not null)
                {
                    RemoveEmptyCreatedFolder(folder, createdFolders);
                }
            }
        }

        return new FolderOrganizationResult(createdFolders, movedPaths, skippedPaths, errors);
    }

    private static string CreateUniqueFolder(string parentDirectory, string folderName)
    {
        var folder = GetUniquePath(Path.Combine(parentDirectory, folderName));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string GetNormalizedFolderName(string path)
    {
        var rawName = Directory.Exists(path)
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileNameWithoutExtension(path);
        var name = rawName.Trim();
        if (Regex.IsMatch(name, @"^\d{4}[.\-_]\d{1,2}[.\-_]\d{1,2}$"))
        {
            return name;
        }

        name = Regex.Replace(name, @"【[^】]*】", string.Empty);
        name = Regex.Replace(name, @"\{[^}]*\}", string.Empty);
        name = name.Replace("_", string.Empty, StringComparison.Ordinal).Replace("＿", string.Empty, StringComparison.Ordinal);
        name = Regex.Replace(name, @"\d{4,}", string.Empty);
        name = name.Replace("#", string.Empty, StringComparison.Ordinal);
        name = Regex.Replace(name, @"[①-⑳㉑-㉟㊱-㊿❶-❿⓵-⓾⑴-⒇]", string.Empty);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(name) ? "新規フォルダ" : name;
    }

    private static string GetUniquePath(string path)
    {
        if (!PathExists(path))
        {
            return path;
        }

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = path + "_" + index.ToString(CultureInfo.InvariantCulture);
            if (!PathExists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("空きフォルダ名が見つかりません。");
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, target);
        }
        else
        {
            File.Move(source, target);
        }
    }

    private static void RemoveEmptyCreatedFolder(string folder, List<string> createdFolders)
    {
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder);
            createdFolders.Remove(folder);
        }
    }

    private static IEnumerable<string> NormalizeExistingPaths(IEnumerable<string> paths)
    {
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeExistingPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!PathExists(fullPath))
        {
            throw new FileNotFoundException("対象のファイルまたはフォルダが見つかりません。", fullPath);
        }

        return fullPath;
    }

    private static string ResolveDirectoryPath(string? path)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = string.IsNullOrWhiteSpace(path)
            ? Directory.GetParent(userProfile)?.FullName ?? userProfile
            : path;
        var fullPath = Path.GetFullPath(candidate);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException("フォルダが見つかりません。: " + fullPath);
        }

        return fullPath;
    }

    private static string GetAvailableTargetPath(string source, string destinationDirectory)
    {
        var sourceName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var candidate = Path.Combine(destinationDirectory, sourceName);
        if (!PathExists(candidate))
        {
            return candidate;
        }

        var extension = Directory.Exists(source) ? string.Empty : Path.GetExtension(sourceName);
        var stem = Directory.Exists(source) ? sourceName : Path.GetFileNameWithoutExtension(sourceName);
        for (var number = 2; ; number++)
        {
            candidate = Path.Combine(destinationDirectory, $"{stem} ({number}){extension}");
            if (!PathExists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory, FileTransferProgressReporter reporter)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)), reporter);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            CopyFile(file, Path.Combine(targetDirectory, Path.GetFileName(file)), reporter);
        }
    }

    private static void CopyFile(string sourcePath, string targetPath, FileTransferProgressReporter reporter)
    {
        const int bufferSize = 1024 * 1024;
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan);
        var buffer = new byte[bufferSize];
        while (source.Read(buffer, 0, buffer.Length) is var read && read > 0)
        {
            target.Write(buffer, 0, read);
            reporter.AddBytes(read, sourcePath);
        }

        target.Flush(flushToDisk: true);
        File.SetLastWriteTimeUtc(targetPath, lastWriteTimeUtc);
        reporter.CompleteFile(sourcePath);
    }

    private static TransferMetrics GetTransferMetrics(string source)
    {
        if (File.Exists(source))
        {
            return new TransferMetrics(new FileInfo(source).Length, 1);
        }

        long totalBytes = 0;
        var fileCount = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            totalBytes += new FileInfo(file).Length;
            fileCount++;
        }

        return new TransferMetrics(totalBytes, fileCount);
    }

    private static bool IsSameVolume(string source, string target) =>
        string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);

    private sealed class FileTransferProgressReporter
    {
        private const long ReportIntervalBytes = 4L * 1024 * 1024;
        private readonly Action<FileTransferProgress>? _progress;
        private readonly long _totalBytes;
        private readonly int _totalFiles;
        private readonly bool _isMove;
        private long _bytesTransferred;
        private long _lastReportedBytes;
        private int _completedFiles;

        public FileTransferProgressReporter(Action<FileTransferProgress>? progress, long totalBytes, int totalFiles, bool isMove)
        {
            _progress = progress;
            _totalBytes = totalBytes;
            _totalFiles = totalFiles;
            _isMove = isMove;
            Report(string.Empty, force: true);
        }

        public void AddBytes(int byteCount, string currentPath)
        {
            _bytesTransferred += byteCount;
            Report(currentPath, force: _bytesTransferred - _lastReportedBytes >= ReportIntervalBytes);
        }

        public void CompleteFile(string currentPath)
        {
            _completedFiles++;
            Report(currentPath, force: true);
        }

        public void CompleteMovedSource(string currentPath, TransferMetrics metrics)
        {
            _bytesTransferred += metrics.TotalBytes;
            _completedFiles += metrics.FileCount;
            Report(currentPath, force: true);
        }

        private void Report(string currentPath, bool force)
        {
            if (_progress is null || (!force && _bytesTransferred - _lastReportedBytes < ReportIntervalBytes))
            {
                return;
            }

            _lastReportedBytes = _bytesTransferred;
            _progress(new FileTransferProgress(
                _bytesTransferred,
                _totalBytes,
                _completedFiles,
                _totalFiles,
                currentPath,
                _isMove));
        }
    }

    private sealed record TransferMetrics(long TotalBytes, int FileCount);

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendantPath(string candidate, string parent)
    {
        var parentWithSeparator = Path.GetFullPath(parent).TrimEnd('\\') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
