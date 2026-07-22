using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using GalleryBrowser.Models;
using SixLabors.ImageSharp;

namespace GalleryBrowser.Services;

public sealed class NConvertZipService
{
    private static readonly HashSet<string> ConvertibleExtensions =
        new(
        [
            ".png",
            ".webp",
            ".jpg",
            ".jpeg",
            ".jpe",
            ".jfif",
            ".jxl",
            ".bmp",
            ".tif",
            ".tiff",
            ".heic",
            ".heif",
            ".avif"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly object _settingsLock = new();
    private NConvertSettingsDto _settings = new(string.Empty, string.Empty);

    public void Configure(NConvertSettingsDto settings)
    {
        lock (_settingsLock)
        {
            _settings = settings with
            {
                ExecutablePath = settings.ExecutablePath.Trim(),
                TemporaryDirectory = settings.TemporaryDirectory.Trim()
            };
        }
    }

    public async Task<NConvertZipResult> ConvertAsync(
        string zipPath,
        IProgress<NConvertZipProgress>? progress,
        CancellationToken cancellationToken)
    {
        var settings = GetSettings();
        ValidateSettings(settings);

        var sourcePath = Path.GetFullPath(zipPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("変換するZIPファイルが見つかりません。", sourcePath);
        }
        if (!string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ZIPファイルだけを変換できます。");
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new InvalidOperationException("ZIPファイルの保存先を確認できません。");
        var removeAutomaticStagingRoot = string.IsNullOrWhiteSpace(settings.TemporaryDirectory);
        var stagingRoot = ResolveStagingRoot(sourcePath, settings.TemporaryDirectory);
        ValidateAsciiTemporaryDirectory(stagingRoot);
        Directory.CreateDirectory(stagingRoot);

        var workDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        var replacementPath = Path.Combine(
            sourceDirectory,
            $".gallerybrowser-nconvert-{Guid.NewGuid():N}.zip");
        var backupPath = sourcePath + $".gallerybrowser-nconvert-backup-{Guid.NewGuid():N}";
        var originalInfo = new FileInfo(sourcePath);
        var originalLength = originalInfo.Length;
        var originalCreationTimeUtc = originalInfo.CreationTimeUtc;
        var originalLastWriteTimeUtc = originalInfo.LastWriteTimeUtc;
        var originalAttributes = originalInfo.Attributes;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buildResult = await BuildReplacementArchiveAsync(
                sourcePath,
                replacementPath,
                workDirectory,
                settings.ExecutablePath,
                progress,
                cancellationToken);

            if (buildResult.ConvertedEntries == 0)
            {
                TryDeleteFile(replacementPath);
                return new NConvertZipResult(sourcePath, 0, buildResult.SourceEntries, originalLength, originalLength);
            }

            progress?.Report(new NConvertZipProgress(
                "verify",
                buildResult.ConvertedEntries,
                buildResult.ConvertedEntries,
                "変換後のZIPを検証しています。"));
            ValidateReplacementArchive(
                replacementPath,
                buildResult.ExpectedEntryNames,
                buildResult.ConvertedEntryNames);

            cancellationToken.ThrowIfCancellationRequested();
            CommitReplacement(replacementPath, sourcePath, backupPath);
            try
            {
                ApplyOriginalFileState(
                    sourcePath,
                    originalCreationTimeUtc,
                    originalLastWriteTimeUtc,
                    originalAttributes);

                // Verify the committed file before removing the recovery copy.
                // This also catches unusual network/share behavior during the
                // final rename.
                ValidateReplacementArchive(
                    sourcePath,
                    buildResult.ExpectedEntryNames,
                    buildResult.ConvertedEntryNames);
            }
            catch
            {
                RestoreBackup(sourcePath, backupPath);
                throw;
            }

            TryDeleteFile(backupPath);
            var resultLength = new FileInfo(sourcePath).Length;
            return new NConvertZipResult(
                sourcePath,
                buildResult.ConvertedEntries,
                buildResult.SourceEntries,
                originalLength,
                resultLength);
        }
        finally
        {
            TryDeleteFile(replacementPath);
            TryDeleteDirectory(workDirectory);
            if (removeAutomaticStagingRoot)
            {
                TryDeleteEmptyDirectory(stagingRoot);
            }
        }
    }

    public static string ResolveStagingRoot(string sourcePath, string configuredTemporaryDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredTemporaryDirectory))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredTemporaryDirectory.Trim()));
        }

        var candidate = Path.GetDirectoryName(Path.GetFullPath(sourcePath))
            ?? throw new ArgumentException("変換元ZIPのフォルダを特定できません。", nameof(sourcePath));
        while (ContainsNonAscii(candidate))
        {
            var parent = Directory.GetParent(candidate);
            if (parent is null)
            {
                throw new InvalidOperationException(
                    "同じドライブ上にNConvert用のASCII一時フォルダを作成できません。" +
                    "Settings > Advanced > NConvert設定で一時フォルダを指定してください。");
            }
            candidate = parent.FullName;
        }

        return Path.Combine(candidate, ".GalleryBrowser-nconvert-temp");
    }

    private NConvertSettingsDto GetSettings()
    {
        lock (_settingsLock)
        {
            return _settings;
        }
    }

    private static void ValidateSettings(NConvertSettingsDto settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ExecutablePath) || !File.Exists(settings.ExecutablePath))
        {
            throw new InvalidOperationException(
                "nconvert.exeが見つかりません。Settings > Advanced > NConvert設定で実行ファイルを指定してください。");
        }
        var expandedTemporaryDirectory = string.IsNullOrWhiteSpace(settings.TemporaryDirectory)
            ? string.Empty
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.TemporaryDirectory));
        if (File.Exists(expandedTemporaryDirectory))
        {
            throw new InvalidOperationException("NConvert一時フォルダにはファイルではなくフォルダを指定してください。");
        }
        if (!string.IsNullOrWhiteSpace(settings.TemporaryDirectory))
        {
            ValidateAsciiTemporaryDirectory(expandedTemporaryDirectory);
        }
    }

    private static void ValidateAsciiTemporaryDirectory(string path)
    {
        if (ContainsNonAscii(path))
        {
            throw new InvalidOperationException(
                "NConvert一時フォルダには日本語・記号・絵文字を含まないASCIIパスを指定してください。");
        }
    }

    private static bool ContainsNonAscii(string value) => value.Any(character => character > 0x7f);

    private static async Task<ArchiveBuildResult> BuildReplacementArchiveAsync(
        string sourcePath,
        string replacementPath,
        string workDirectory,
        string executablePath,
        IProgress<NConvertZipProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            useAsync: true);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: true);
        var plans = CreateConversionPlans(sourceArchive);
        if (plans.Count == 0)
        {
            return new ArchiveBuildResult(
                0,
                sourceArchive.Entries.Count,
                sourceArchive.Entries.Select(entry => entry.FullName).ToArray(),
                []);
        }

        var conversionBySource = plans.ToDictionary(plan => plan.SourceName, StringComparer.OrdinalIgnoreCase);
        var replacedTargets = plans
            .Select(plan => plan.TargetName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceNames = plans
            .Select(plan => plan.SourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedNames = sourceArchive.Entries
            .Where(entry => !replacedTargets.Contains(entry.FullName) || sourceNames.Contains(entry.FullName))
            .Select(entry => conversionBySource.TryGetValue(entry.FullName, out var plan)
                ? plan.TargetName
                : entry.FullName)
            .ToArray();

        await using var destinationStream = new FileStream(
            replacementPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);
        using var destinationArchive = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true);

        var converted = 0;
        var sequence = 0;
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (replacedTargets.Contains(sourceEntry.FullName) && !sourceNames.Contains(sourceEntry.FullName))
            {
                // A source image conversion intentionally replaces an existing
                // JPG with the same archive path.
                continue;
            }

            if (!conversionBySource.TryGetValue(sourceEntry.FullName, out var conversion))
            {
                await CopyEntryAsync(sourceEntry, destinationArchive, cancellationToken);
                continue;
            }

            sequence++;
            progress?.Report(new NConvertZipProgress(
                "convert",
                converted,
                plans.Count,
                sourceEntry.FullName));
            var inputPath = Path.Combine(
                workDirectory,
                $"input-{sequence:D6}{Path.GetExtension(sourceEntry.Name).ToLowerInvariant()}");
            var outputPath = Path.Combine(workDirectory, $"output-{sequence:D6}.jpg");
            try
            {
                await using (var input = new FileStream(
                    inputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 128,
                    useAsync: true))
                await using (var entryStream = sourceEntry.Open())
                {
                    await entryStream.CopyToAsync(input, cancellationToken);
                }
                File.SetLastWriteTimeUtc(inputPath, sourceEntry.LastWriteTime.UtcDateTime);

                await RunNConvertAsync(executablePath, inputPath, outputPath, cancellationToken);
                ValidateJpegOutput(outputPath, sourceEntry.FullName);

                var destinationEntry = destinationArchive.CreateEntry(conversion.TargetName, CompressionLevel.Optimal);
                CopyEntryMetadata(sourceEntry, destinationEntry);
                await using var output = new FileStream(
                    outputPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1024 * 128,
                    useAsync: true);
                await using var destinationEntryStream = destinationEntry.Open();
                await output.CopyToAsync(destinationEntryStream, cancellationToken);
                converted++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"ZIP内画像を変換できませんでした: {sourceEntry.FullName}{Environment.NewLine}{exception.Message}",
                    exception);
            }
            finally
            {
                TryDeleteFile(inputPath);
                TryDeleteFile(outputPath);
            }
        }

        destinationArchive.Dispose();
        await destinationStream.FlushAsync(cancellationToken);
        return new ArchiveBuildResult(
            converted,
            sourceArchive.Entries.Count,
            expectedNames,
            plans.Select(plan => plan.TargetName).ToArray());
    }

    private static IReadOnlyList<ConversionPlan> CreateConversionPlans(ZipArchive archive)
    {
        var plans = new List<ConversionPlan>();
        var targetOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || !ConvertibleExtensions.Contains(Path.GetExtension(entry.Name)))
            {
                continue;
            }

            var targetName = ChangeArchiveEntryExtension(entry.FullName, ".jpg");
            if (targetOwners.TryGetValue(targetName, out var existingSource))
            {
                throw new InvalidOperationException(
                    $"同じJPG名になる画像がZIP内に複数あります: {existingSource} / {entry.FullName}");
            }
            targetOwners[targetName] = entry.FullName;
            plans.Add(new ConversionPlan(entry.FullName, targetName));
        }
        return plans;
    }

    private static string ChangeArchiveEntryExtension(string fullName, string extension)
    {
        var slash = Math.Max(fullName.LastIndexOf('/'), fullName.LastIndexOf('\\'));
        var directory = slash >= 0 ? fullName[..(slash + 1)] : string.Empty;
        var name = slash >= 0 ? fullName[(slash + 1)..] : fullName;
        return directory + Path.GetFileNameWithoutExtension(name) + extension;
    }

    private static async Task CopyEntryAsync(
        ZipArchiveEntry sourceEntry,
        ZipArchive destinationArchive,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sourceEntry.Name))
        {
            var directoryEntry = destinationArchive.CreateEntry(sourceEntry.FullName, CompressionLevel.NoCompression);
            CopyEntryMetadata(sourceEntry, directoryEntry);
            return;
        }

        var destinationEntry = destinationArchive.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
        CopyEntryMetadata(sourceEntry, destinationEntry);
        await using var source = sourceEntry.Open();
        await using var destination = destinationEntry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static void CopyEntryMetadata(ZipArchiveEntry source, ZipArchiveEntry destination)
    {
        destination.LastWriteTime = source.LastWriteTime;
        destination.ExternalAttributes = source.ExternalAttributes;
    }

    private static async Task RunNConvertAsync(
        string executablePath,
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        string[] arguments =
        [
            "-quiet", "-overwrite", "-no_auto_ext",
            "-out", "jpegli", "-q", "95",
            "-i", "-opthuff", "-dct", "2", "-smoothingf", "0", "-subsampling", "2",
            "-ratio", "-rtype", "mitchell", "-rflag", "decr", "-resize", "0", "1600",
            "-keepfiledate", "-o", outputPath, inputPath
        ];
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("NConvertを起動できませんでした。");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("NConvertが10分以内に完了しませんでした。");
            }
            throw;
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"NConvertが終了コード {process.ExitCode} を返しました。{FormatProcessDetails(standardOutput, standardError)}");
        }
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new InvalidOperationException(
                "NConvertは正常終了しましたが、出力ファイルを作成しませんでした。" +
                FormatProcessDetails(standardOutput, standardError));
        }
    }

    private static string FormatProcessDetails(string standardOutput, string standardError)
    {
        var lines = (standardError + Environment.NewLine + standardOutput)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();
        return lines.Length == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static void ValidateJpegOutput(string path, string sourceEntryName)
    {
        using (var stream = File.OpenRead(path))
        {
            Span<byte> signature = stackalloc byte[3];
            if (stream.Read(signature) != signature.Length ||
                signature[0] != 0xff || signature[1] != 0xd8 || signature[2] != 0xff)
            {
                throw new InvalidDataException($"NConvertの出力がJPEGではありません: {sourceEntryName}");
            }
        }

        var imageInfo = Image.Identify(path)
            ?? throw new InvalidDataException($"変換後のJPEGを読み取れません: {sourceEntryName}");
        if (imageInfo.Width <= 0 || imageInfo.Height <= 0 || imageInfo.Height > 1600)
        {
            throw new InvalidDataException(
                $"変換後のJPEGサイズが正しくありません: {sourceEntryName} ({imageInfo.Width}x{imageInfo.Height})");
        }
    }

    private static void ValidateReplacementArchive(
        string archivePath,
        IReadOnlyList<string> expectedEntryNames,
        IReadOnlyList<string> convertedEntryNames)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var actualNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (actualNames.Length != expectedEntryNames.Count ||
            !actualNames.OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(expectedEntryNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException("変換後のZIP内のファイル構成が元の構成と一致しません。");
        }

        var entries = archive.Entries
            .Where(entry => convertedEntryNames.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(entry => entry.FullName, StringComparer.OrdinalIgnoreCase);
        foreach (var convertedEntryName in convertedEntryNames)
        {
            if (!entries.TryGetValue(convertedEntryName, out var entry) || entry.Length == 0)
            {
                throw new InvalidDataException($"変換後のJPEGがZIP内に見つかりません: {convertedEntryName}");
            }
            using var entryStream = entry.Open();
            var signature = new byte[3];
            if (entryStream.Read(signature) != signature.Length ||
                signature[0] != 0xff || signature[1] != 0xd8 || signature[2] != 0xff)
            {
                throw new InvalidDataException($"ZIP内の変換結果がJPEGではありません: {convertedEntryName}");
            }
        }
    }

    private static void CommitReplacement(string replacementPath, string sourcePath, string backupPath)
    {
        try
        {
            File.Replace(replacementPath, sourcePath, backupPath, ignoreMetadataErrors: true);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            if (File.Exists(backupPath))
            {
                RestoreBackup(sourcePath, backupPath);
                throw;
            }
            if (!File.Exists(replacementPath) || !File.Exists(sourcePath) || File.Exists(backupPath))
            {
                throw;
            }

            File.Move(sourcePath, backupPath);
            try
            {
                File.Move(replacementPath, sourcePath);
            }
            catch
            {
                if (!File.Exists(sourcePath) && File.Exists(backupPath))
                {
                    File.Move(backupPath, sourcePath);
                }
                throw;
            }
        }
    }

    private static void RestoreBackup(string sourcePath, string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            return;
        }

        var failedPath = sourcePath + $".gallerybrowser-nconvert-invalid-{Guid.NewGuid():N}";
        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, failedPath);
        }
        try
        {
            File.Move(backupPath, sourcePath);
            TryDeleteFile(failedPath);
        }
        catch
        {
            if (!File.Exists(sourcePath) && File.Exists(failedPath))
            {
                File.Move(failedPath, sourcePath);
            }
            throw;
        }
    }

    private static void ApplyOriginalFileState(
        string path,
        DateTime creationTimeUtc,
        DateTime lastWriteTimeUtc,
        FileAttributes attributes)
    {
        File.SetCreationTimeUtc(path, creationTimeUtc);
        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        File.SetAttributes(path, attributes);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TryDeleteFile(string path)
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
        }
        catch (UnauthorizedAccessException)
        {
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ConversionPlan(string SourceName, string TargetName);

    private sealed record ArchiveBuildResult(
        int ConvertedEntries,
        int SourceEntries,
        IReadOnlyList<string> ExpectedEntryNames,
        IReadOnlyList<string> ConvertedEntryNames);
}

public sealed record NConvertZipProgress(
    string Phase,
    int Completed,
    int Total,
    string CurrentEntryName);

public sealed record NConvertZipResult(
    string ZipPath,
    int ConvertedEntries,
    int SourceEntries,
    long OriginalBytes,
    long ResultBytes);
