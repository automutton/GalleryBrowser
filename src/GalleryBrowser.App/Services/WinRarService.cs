using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using GalleryBrowser.Models;
using SharpCompress.Archives;

namespace GalleryBrowser.Services;

public sealed class WinRarService
{
    private const string DefaultSupportedExtensions = ".zip,.rar,.7z,.cbz,.cbr";
    private static readonly string[] CandidatePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinRAR", "WinRAR.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinRAR", "WinRAR.exe")
    ];

    private string _configuredExecutablePath = string.Empty;
    private string _supportedExtensions = DefaultSupportedExtensions;

    public bool IsAvailable => ResolveExecutablePath() is not null;

    public void Configure(WinRarSettingsDto settings)
    {
        _configuredExecutablePath = settings.ExecutablePath.Trim();
        _supportedExtensions = settings.SupportedExtensions;
    }

    public bool SupportsArchive(string path) =>
        !Directory.Exists(path) &&
        _supportedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(extension => string.Equals(extension, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    public void Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("WinRAR で開く項目が見つかりません。", path);
        }

        var executablePath = ResolveExecutablePath()
            ?? throw new FileNotFoundException("WinRAR.exe が見つかりません。WinRAR設定で実行ファイルを確認してください。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo);
    }

    public async Task ExtractHereAsync(string archivePath)
    {
        var destination = Path.GetDirectoryName(archivePath)
            ?? throw new InvalidOperationException("解凍先フォルダを取得できません。");
        await RunAsync("e", archivePath, destination);
    }

    public async Task<string> ExtractToSubfolderAsync(string archivePath)
    {
        var parentPath = Path.GetDirectoryName(archivePath)
            ?? throw new InvalidOperationException("解凍先フォルダを取得できません。");
        var folderName = Path.GetFileNameWithoutExtension(archivePath);
        var destination = Path.Combine(parentPath, folderName);
        await RunAsync("x", archivePath, destination);
        return destination;
    }

    public async Task<WinRarExtractionResult> ExtractAutomaticallyAsync(string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("書庫ファイルが見つかりません。", archivePath);
        }

        var parentPath = Path.GetDirectoryName(archivePath)
            ?? throw new InvalidOperationException("解凍先フォルダを取得できません。");
        var extractHere = HasSingleTopLevelDirectory(archivePath);
        var destination = extractHere
            ? parentPath
            : Path.Combine(parentPath, Path.GetFileNameWithoutExtension(archivePath));

        // Preserve the archive paths so a single top-level folder remains intact.
        await RunAsync("x", archivePath, destination);
        return new WinRarExtractionResult(destination, extractHere);
    }

    public async Task<RarToZipConversionResult> ConvertRarToZipAsync(
        string rarPath,
        Action<string>? reportProgress = null)
    {
        rarPath = Path.GetFullPath(rarPath);
        if (!File.Exists(rarPath))
        {
            throw new FileNotFoundException("変換するRARファイルが見つかりません。", rarPath);
        }
        if (!string.Equals(Path.GetExtension(rarPath), ".rar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("RARファイルだけをZIPへ変換できます。");
        }

        _ = ResolveExecutablePath()
            ?? throw new FileNotFoundException("WinRAR.exe が見つかりません。WinRAR設定で実行ファイルを確認してください。");
        var parentPath = Path.GetDirectoryName(rarPath)
            ?? throw new InvalidOperationException("変換元フォルダを取得できません。");
        var zipPath = Path.ChangeExtension(rarPath, ".zip");
        if (File.Exists(zipPath) || Directory.Exists(zipPath))
        {
            throw new InvalidOperationException("同名のZIPファイルが既に存在します: " + Path.GetFileName(zipPath));
        }

        var operationId = Guid.NewGuid().ToString("N");
        var workRoot = Path.Combine(Path.GetTempPath(), "GalleryBrowser", "RarToZip", operationId);
        var extractionRoot = Path.Combine(workRoot, "extracted");
        var candidateZipPath = Path.Combine(workRoot, Path.GetFileName(zipPath));
        var stagingPath = Path.Combine(parentPath, $".{Path.GetFileName(zipPath)}.{operationId}.tmp");
        Directory.CreateDirectory(extractionRoot);

        try
        {
            // The original RAR remains untouched at its source until the fully
            // verified ZIP has been placed. Only extracted and generated data is
            // written to the isolated work directory.
            reportProgress?.Invoke("WinRAR: 作業フォルダへRARを展開しています。");
            await RunAsync("x", rarPath, extractionRoot);
            var extractedStructure = BuildDirectoryStructure(extractionRoot);

            reportProgress?.Invoke("WinRAR: 展開内容をZIPへ圧縮しています。");
            await CreateZipFromDirectoryAsync(
                extractionRoot,
                candidateZipPath,
                extractedStructure);

            reportProgress?.Invoke("WinRAR: ZIPの内部構成を検証しています。");
            var zipStructure = ReadZipStructure(candidateZipPath);
            var structureMismatch = DescribeStructureMismatch(extractedStructure, zipStructure);
            if (structureMismatch is not null)
            {
                throw new InvalidDataException(
                    "RARの展開結果とZIPの内部構成が一致しないため、変換を中止しました。 " +
                    structureMismatch);
            }

            reportProgress?.Invoke("WinRAR: 検証済みZIPを元のフォルダへ配置しています。");
            File.Copy(candidateZipPath, stagingPath, overwrite: false);
            if (new FileInfo(stagingPath).Length != new FileInfo(candidateZipPath).Length)
            {
                throw new IOException("ZIPを元のフォルダへコピーした際にサイズが一致しませんでした。");
            }
            File.Move(stagingPath, zipPath);

            try
            {
                // The source archive is deleted only after the ZIP has been
                // generated, structurally verified, copied, and atomically named.
                File.Delete(rarPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteFile(zipPath);
                throw new IOException(
                    "ZIPは作成できましたが元RARを削除できなかったため、ZIPを取り消しました。",
                    ex);
            }

            return new RarToZipConversionResult(
                rarPath,
                zipPath,
                extractedStructure.Files.Count,
                extractedStructure.Directories.Count);
        }
        finally
        {
            TryDeleteFile(stagingPath);
            TryDeleteDirectory(workRoot);
        }
    }

    public void ValidateCompressAndDeleteTargets(IEnumerable<string> folderPaths)
    {
        var paths = folderPaths.ToArray();
        if (paths.Length == 0)
        {
            throw new InvalidOperationException("圧縮するフォルダを選択してください。");
        }

        foreach (var folderPath in paths)
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException("圧縮するフォルダが見つかりません: " + folderPath);
            }

            var archivePath = GetArchivePath(folderPath);
            if (File.Exists(archivePath))
            {
                throw new InvalidOperationException("同名の ZIP ファイルが既に存在します: " + Path.GetFileName(archivePath));
            }
        }
    }

    public async Task<string> CompressFolderAndDeleteAsync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException("圧縮するフォルダが見つかりません: " + folderPath);
        }

        var archivePath = GetArchivePath(folderPath);
        if (File.Exists(archivePath))
        {
            throw new InvalidOperationException("同名の ZIP ファイルが既に存在します: " + Path.GetFileName(archivePath));
        }

        var executablePath = ResolveExecutablePath()
            ?? throw new FileNotFoundException("WinRAR.exe が見つかりません。WinRAR をインストールしてください。");
        var parentPath = Path.GetDirectoryName(folderPath)
            ?? throw new InvalidOperationException("圧縮先フォルダを取得できません。");
        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = parentPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "a", "-afzip", "-df", "-y", "-ibck", Path.GetFileName(archivePath), folderName }
        }) ?? throw new InvalidOperationException("WinRAR を起動できませんでした。");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"WinRAR の圧縮に失敗しました。(終了コード: {process.ExitCode})");
        }

        return archivePath;
    }

    public async Task<string> CompressFoldersAsPackageAndDeleteAsync(
        IReadOnlyList<string> folderPaths,
        string archiveName)
    {
        if (folderPaths.Count == 0)
        {
            throw new InvalidOperationException("圧縮するフォルダを選択してください。");
        }

        var parentPath = Path.GetDirectoryName(folderPaths[0])
            ?? throw new InvalidOperationException("圧縮先フォルダを取得できません。");
        if (folderPaths.Any(path => !Directory.Exists(path)))
        {
            throw new DirectoryNotFoundException("圧縮するフォルダが見つかりません。");
        }
        if (folderPaths.Any(path => !string.Equals(Path.GetDirectoryName(path), parentPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("同じフォルダ内のフォルダだけをまとめて圧縮できます。");
        }

        var archiveFileName = NormalizeZipArchiveFileName(archiveName);
        var archivePath = Path.Combine(parentPath, archiveFileName);
        if (File.Exists(archivePath))
        {
            throw new InvalidOperationException("同名の ZIP ファイルが既に存在します: " + archiveFileName);
        }

        var executablePath = ResolveExecutablePath()
            ?? throw new FileNotFoundException("WinRAR.exe が見つかりません。WinRAR をインストールしてください。");
        var folderNames = folderPaths
            .Select(path => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .ToArray();

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = parentPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("a");
        startInfo.ArgumentList.Add("-afzip");
        startInfo.ArgumentList.Add("-df");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-ibck");
        startInfo.ArgumentList.Add(archiveFileName);
        foreach (var folderName in folderNames)
        {
            startInfo.ArgumentList.Add(folderName);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("WinRAR を起動できませんでした。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"WinRAR の圧縮に失敗しました。(終了コード: {process.ExitCode})");
        }

        return archivePath;
    }

    private async Task RunAsync(string command, string archivePath, string destination)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("書庫ファイルが見つかりません。", archivePath);
        }

        var executablePath = ResolveExecutablePath()
            ?? throw new FileNotFoundException("WinRAR.exe が見つかりません。WinRAR をインストールしてください。");

        Directory.CreateDirectory(destination);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { command, "-y", "-ibck", archivePath, destination + Path.DirectorySeparatorChar }
        }) ?? throw new InvalidOperationException("WinRAR を起動できませんでした。");

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"WinRAR の解凍に失敗しました。(終了コード: {process.ExitCode})");
        }
    }

    private static async Task CreateZipFromDirectoryAsync(
        string sourceDirectory,
        string zipPath,
        ArchiveStructure structure)
    {
        await using var zipStream = new FileStream(
            zipPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false);

        // Explicit directory entries preserve empty folders and keep the same
        // archive tree even though ZIP readers can infer non-empty parents.
        foreach (var directoryPath in structure.Directories.Order(StringComparer.Ordinal))
        {
            archive.CreateEntry(directoryPath.TrimEnd('/') + "/");
        }

        foreach (var relativePath in structure.Files.Keys.Order(StringComparer.Ordinal))
        {
            var sourcePath = Path.Combine(
                sourceDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = ClampZipTimestamp(File.GetLastWriteTime(sourcePath));

            await using var sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var entryStream = entry.Open();
            await sourceStream.CopyToAsync(entryStream);
        }
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime timestamp)
    {
        var localTimestamp = timestamp.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(timestamp, DateTimeKind.Local)
            : timestamp.ToLocalTime();
        var minimum = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var maximum = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        return new DateTimeOffset(localTimestamp < minimum
            ? minimum
            : localTimestamp > maximum
                ? maximum
                : localTimestamp);
    }

    private static ArchiveStructure BuildDirectoryStructure(string rootPath)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        var emptyDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pendingDirectories = new Stack<(string Path, string RelativePath)>();
        pendingDirectories.Push((rootPath, string.Empty));

        while (pendingDirectories.Count > 0)
        {
            var (directory, directoryRelativePath) = pendingDirectories.Pop();
            var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            if (entries.Length == 0 && !string.IsNullOrEmpty(directoryRelativePath))
            {
                emptyDirectories.Add(directoryRelativePath);
            }

            foreach (var entryPath in entries)
            {
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("RAR内に再解析ポイントが含まれるため、安全のため変換を中止しました。");
                }

                var relativePath = NormalizeArchivePath(Path.GetRelativePath(rootPath, entryPath));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relativePath);
                    AddParentDirectories(relativePath, directories);
                    pendingDirectories.Push((entryPath, relativePath));
                }
                else
                {
                    files.Add(relativePath, new FileInfo(entryPath).Length);
                    AddParentDirectories(relativePath, directories);
                }
            }
        }

        return new ArchiveStructure(files, directories, emptyDirectories);
    }

    private static ArchiveStructure ReadZipStructure(string zipPath)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var normalizedPath = NormalizeArchivePath(entry.FullName);
            if (string.IsNullOrEmpty(normalizedPath))
            {
                continue;
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                directories.Add(normalizedPath);
            }
            else
            {
                if (!files.TryAdd(normalizedPath, entry.Length))
                {
                    throw new InvalidDataException(
                        "作成したZIPに同じパスのファイルが複数含まれています: " + normalizedPath);
                }
            }
            AddParentDirectories(normalizedPath, directories);
        }
        return new ArchiveStructure(files, directories, []);
    }

    private static string? DescribeStructureMismatch(
        ArchiveStructure extracted,
        ArchiveStructure zipped)
    {
        var missingFiles = extracted.Files.Keys.Except(zipped.Files.Keys, StringComparer.Ordinal).Take(3).ToArray();
        if (missingFiles.Length > 0)
        {
            return "ZIPに存在しないファイル: " + string.Join(", ", missingFiles);
        }

        var extraFiles = zipped.Files.Keys.Except(extracted.Files.Keys, StringComparer.Ordinal).Take(3).ToArray();
        if (extraFiles.Length > 0)
        {
            return "ZIPにだけ存在するファイル: " + string.Join(", ", extraFiles);
        }

        var sizeMismatches = extracted.Files
            .Where(pair => zipped.Files.TryGetValue(pair.Key, out var zipLength) && pair.Value != zipLength)
            .Take(3)
            .Select(pair => $"{pair.Key} (展開後 {pair.Value:N0} bytes / ZIP {zipped.Files[pair.Key]:N0} bytes)")
            .ToArray();
        if (sizeMismatches.Length > 0)
        {
            return "ファイルサイズが一致しません: " + string.Join(", ", sizeMismatches);
        }

        // A ZIP does not need explicit entries for directories which contain
        // files. Empty directories do need entries or the archive structure
        // would change after extraction.
        var missingEmptyDirectories = extracted.EmptyDirectories
            .Except(zipped.Directories, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        if (missingEmptyDirectories.Length > 0)
        {
            return "ZIPに存在しない空フォルダ: " + string.Join(", ", missingEmptyDirectories);
        }

        var extraDirectories = zipped.Directories
            .Except(extracted.Directories, StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        return extraDirectories.Length > 0
            ? "ZIPにだけ存在するフォルダ: " + string.Join(", ", extraDirectories)
            : null;
    }

    private static void AddParentDirectories(string archivePath, ISet<string> directories)
    {
        var separatorIndex = archivePath.LastIndexOf('/');
        while (separatorIndex > 0)
        {
            var parent = archivePath[..separatorIndex];
            directories.Add(parent);
            separatorIndex = parent.LastIndexOf('/');
        }
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('\\', '/').Trim('/');

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is best effort; the original exception remains primary.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best effort; stale work data is isolated under temp.
        }
    }

    private string? ResolveExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_configuredExecutablePath))
        {
            return File.Exists(_configuredExecutablePath) ? _configuredExecutablePath : null;
        }

        return CandidatePaths.FirstOrDefault(File.Exists);
    }

    private static string GetArchivePath(string folderPath)
    {
        var parentPath = Path.GetDirectoryName(folderPath)
            ?? throw new InvalidOperationException("圧縮先フォルダを取得できません。");
        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new InvalidOperationException("ドライブ直下は圧縮できません。");
        }

        return Path.Combine(parentPath, folderName + ".zip");
    }

    private static string NormalizeZipArchiveFileName(string archiveName)
    {
        var baseName = archiveName.Trim();
        if (baseName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        if (string.IsNullOrWhiteSpace(baseName) ||
            baseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            baseName.Contains(Path.DirectorySeparatorChar) ||
            baseName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("書庫名にはファイル名として使用できる文字を入力してください。");
        }

        return baseName + ".zip";
    }

    private static bool HasSingleTopLevelDirectory(string archivePath)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasRootFile = false;

            foreach (var entry in archive.Entries)
            {
                var key = entry.Key?.Replace('\\', '/').Trim('/') ?? string.Empty;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                var segments = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    continue;
                }

                rootNames.Add(segments[0]);
                if (segments.Length == 1 && !entry.IsDirectory)
                {
                    hasRootFile = true;
                }

                if (hasRootFile || rootNames.Count > 1)
                {
                    return false;
                }
            }

            return !hasRootFile && rootNames.Count == 1;
        }
        catch
        {
            // If entries cannot be inspected, use an isolated subfolder as the safe fallback.
            return false;
        }
    }

    private sealed record ArchiveStructure(
        Dictionary<string, long> Files,
        HashSet<string> Directories,
        HashSet<string> EmptyDirectories);
}

public sealed record WinRarExtractionResult(string Destination, bool ExtractedHere);

public sealed record RarToZipConversionResult(
    string RarPath,
    string ZipPath,
    int FileCount,
    int DirectoryCount);
