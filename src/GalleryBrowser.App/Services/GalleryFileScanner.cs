using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GalleryBrowser.Services;

internal sealed class GalleryFileScanner
{
    private const string ArchiveMetadataEntry = ".archive_meta.json";
    private const string ExcludedFolderNamePart = "アドミ関係";
    private static readonly Regex CreatorRegex = new(@"^【(.+)】$", RegexOptions.Compiled);
    private static readonly Regex ZpiRegex = new(@"\s*\{zpi\$([^}]*)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GidRegex = new(@"\s*\{gid=([A-Za-z0-9]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".cbz", ".cbr"
    };

    public async Task<GalleryFileScanResult> ScanAsync(
        GalleryScanPlan plan,
        IReadOnlyList<string> configuredCategoryRoots,
        Action<string>? reportProgress,
        CancellationToken cancellationToken)
    {
        var extensions = (plan.Extensions.Length > 0 ? plan.Extensions : GalleryDatabaseUpdateService.DefaultSupportedExtensions)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = await Task.Run(
            () => EnumerateSupportedFiles(plan.Targets, extensions, cancellationToken),
            cancellationToken);
        var categoryRoots = configuredCategoryRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(Directory.Exists)
            .OrderByDescending(path => path.Length)
            .ToArray();
        var items = new ConcurrentBag<GalleryScannedItem>();
        var errors = new ConcurrentBag<string>();
        var processed = 0;
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 4)
        };

        await Parallel.ForEachAsync(files, options, (path, token) =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                items.Add(ScanFile(path, plan.Category, categoryRoots));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                errors.Add($"{path}: {ex.Message}");
            }
            finally
            {
                var completed = Interlocked.Increment(ref processed);
                if (completed == 1 || completed % 25 == 0 || completed == files.Count)
                {
                    reportProgress?.Invoke($"[progress] processed={completed:N0}/{files.Count:N0}");
                }
            }
            return ValueTask.CompletedTask;
        });

        return new GalleryFileScanResult(
            items.OrderBy(item => item.CurrentPath, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            errors.OrderBy(error => error, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            files.Count);
    }

    private static IReadOnlyList<string> EnumerateSupportedFiles(
        IReadOnlyList<string> roots,
        IReadOnlySet<string> extensions,
        CancellationToken cancellationToken)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                                !Path.GetFileName(entry).Contains(ExcludedFolderNamePart, StringComparison.OrdinalIgnoreCase))
                            {
                                pending.Push(entry);
                            }
                        }
                        else if (extensions.Contains(Path.GetExtension(entry)))
                        {
                            files.Add(Path.GetFullPath(entry));
                        }
                    }
                    catch (IOException)
                    {
                        // Continue with the accessible entries in this folder.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Continue with the accessible entries in this folder.
                    }
                }
            }
            catch (IOException)
            {
                // An inaccessible subtree is skipped, matching Explorer enumeration behavior.
            }
            catch (UnauthorizedAccessException)
            {
                // An inaccessible subtree is skipped, matching Explorer enumeration behavior.
            }
        }
        return files.OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static GalleryScannedItem ScanFile(
        string path,
        string category,
        IReadOnlyList<string> configuredCategoryRoots)
    {
        var file = new FileInfo(path);
        var zpi = ParseZpiFields(file.Name);
        var filenameGid = ParseGid(file.Name);
        if (string.IsNullOrWhiteSpace(filenameGid))
        {
            throw new InvalidDataException("正式な{gid=...}がファイル名にありません。");
        }
        var creator = InferCreatorFromPath(path);
        var topFolder = InferTopFolder(path, configuredCategoryRoots);
        var extension = file.Extension;
        var mediaType = ArchiveExtensions.Contains(extension) ? "archive" : "video";
        var rating = zpi.Rating;
        var tags = zpi.Tags.ToList();
        int? imageCount = null;
        int? zipEntryCount = null;
        long? totalUncompressedSize = null;

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var archiveMetadata = ReadArchiveMetadata(archive);
            rating = Math.Max(rating, archiveMetadata.Rating);
            tags = tags.Concat(archiveMetadata.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            creator = FirstNonEmpty(creator, archiveMetadata.Creator);
            topFolder = FirstNonEmpty(topFolder, archiveMetadata.TopFolder);

            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                                !entry.FullName.Equals(ArchiveMetadataEntry, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            zipEntryCount = entries.Length;
            imageCount = entries.Count(entry => ImageExtensions.Contains(Path.GetExtension(entry.FullName)));
            totalUncompressedSize = entries.Sum(entry => entry.Length);
        }

        return new GalleryScannedItem(
            filenameGid.ToUpperInvariant(),
            file.FullName,
            file.Name,
            mediaType,
            category,
            topFolder,
            creator,
            string.Empty,
            string.Empty,
            rating,
            tags,
            imageCount,
            null,
            zipEntryCount,
            totalUncompressedSize,
            file.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            file.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static GalleryArchiveMetadata ReadArchiveMetadata(ZipArchive archive)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Equals(ArchiveMetadataEntry, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return GalleryArchiveMetadata.Empty;
        }
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var galleryMetadata = root.TryGetProperty("gallery_metadata", out var metadata) &&
                              metadata.ValueKind == JsonValueKind.Object
            ? metadata
            : default;
        if (galleryMetadata.ValueKind != JsonValueKind.Object)
        {
            return GalleryArchiveMetadata.Empty;
        }

        var tags = galleryMetadata.TryGetProperty("tags", out var tagsProperty)
            ? ReadStringValues(tagsProperty)
            : [];
        return new GalleryArchiveMetadata(
            ReadInt(galleryMetadata, "rating"),
            tags,
            ReadString(galleryMetadata, "creator"),
            ReadString(galleryMetadata, "top_folder"));
    }

    private static string InferCreatorFromPath(string path)
    {
        foreach (var part in path.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = CleanDisplayName(part);
            if (CreatorRegex.Match(clean) is { Success: true } creatorMatch)
            {
                return creatorMatch.Groups[1].Value.Trim();
            }
        }
        // Title and Character are DB-managed attributes and are intentionally not inferred from path tokens.
        return string.Empty;
    }

    private static string InferTopFolder(string path, IReadOnlyList<string> configuredCategoryRoots)
    {
        foreach (var root in configuredCategoryRoots)
        {
            if (!IsPathWithinRoot(path, root))
            {
                continue;
            }
            var relative = Path.GetRelativePath(root, path);
            return relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static GalleryZpiMetadata ParseZpiFields(string name)
    {
        var rating = 0;
        var tags = new List<string>();
        foreach (Match match in ZpiRegex.Matches(name))
        {
            foreach (var part in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }
                var key = part[..separator].Trim();
                var value = part[(separator + 1)..].Trim();
                if (key.Equals("r", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRating))
                {
                    rating = Math.Max(rating, parsedRating);
                }
                else if (key.Equals("t", StringComparison.OrdinalIgnoreCase))
                {
                    tags.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }
        }
        return new GalleryZpiMetadata(rating, tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string ParseGid(string name) =>
        GidRegex.Match(name) is { Success: true } match ? match.Groups[1].Value.Trim() : string.Empty;

    private static string CleanDisplayName(string value) =>
        ZpiRegex.Replace(GidRegex.Replace(value, string.Empty), string.Empty).Trim();

    private static IReadOnlyList<string> NormalizeStringValues(object? values)
    {
        var raw = values switch
        {
            null => [],
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IEnumerable<string> sequence => sequence,
            _ => [Convert.ToString(values, CultureInfo.InvariantCulture) ?? string.Empty]
        };
        return raw
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadStringValues(JsonElement property)
    {
        if (property.ValueKind == JsonValueKind.Array)
        {
            return property.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (property.ValueKind == JsonValueKind.String)
        {
            return NormalizeStringValues(property.GetString());
        }
        return [];
    }

    private static string ReadString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }
        if (property.TryGetInt32(out var value))
        {
            return value;
        }
        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            ? value
            : 0;
    }

    private static string FirstNonEmpty(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private sealed record GalleryZpiMetadata(int Rating, IReadOnlyList<string> Tags);
    private sealed record GalleryArchiveMetadata(
        int Rating,
        IReadOnlyList<string> Tags,
        string Creator,
        string TopFolder)
    {
        public static GalleryArchiveMetadata Empty { get; } = new(0, [], "", "");
    }
}

internal sealed record GalleryScannedItem(
    string Gid,
    string CurrentPath,
    string SourceFileName,
    string MediaType,
    string Category,
    string TopFolder,
    string Creator,
    string Title,
    string Character,
    int Rating,
    IReadOnlyList<string> Tags,
    int? ImageCount,
    int? DurationSeconds,
    int? ZipEntryCount,
    long? TotalUncompressedSize,
    string LastAccessTime,
    string LastWriteTime);

internal sealed record GalleryFileScanResult(
    IReadOnlyList<GalleryScannedItem> Items,
    IReadOnlyList<string> Errors,
    int DiscoveredFileCount);

internal sealed record GalleryDatabaseScanWriteResult(
    int WrittenCount,
    IReadOnlyList<string> Errors);
