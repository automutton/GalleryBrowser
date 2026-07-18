using System.IO;

namespace GalleryBrowser.Services;

internal static class ApplicationPaths
{
    public const string PortableMarkerFileName = "GalleryBrowser.portable";
    private const string DataDirectoryEnvironmentVariable = "GALLERYBROWSER_DATA_DIR";
    private const string LocalApplicationDataToken = "%LOCALAPPDATA%";
    private const string UserProfileToken = "%USERPROFILE%";

    public static string GetDataDirectory()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return ResolvePathExpression(configuredDirectory);
        }

        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var portableMarkerPath = Path.Combine(applicationDirectory, PortableMarkerFileName);
        if (File.Exists(portableMarkerPath))
        {
            var configuredPortableDirectory = File.ReadAllText(portableMarkerPath).Trim();
            if (!string.IsNullOrWhiteSpace(configuredPortableDirectory))
            {
                return ResolvePathExpression(configuredPortableDirectory);
            }

            return Path.Combine(applicationDirectory, "data");
        }

        var repositoryRoot = TryGetRepositoryRoot(applicationDirectory);
        if (repositoryRoot is not null)
        {
            return Path.Combine(repositoryRoot, "data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GalleryBrowser");
    }

    public static string ToEnvironmentVariablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(path);
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var compacted = ReplacePathRoot(fullPath, localApplicationData, LocalApplicationDataToken);
        if (!string.Equals(compacted, fullPath, StringComparison.Ordinal))
        {
            return compacted;
        }

        return ReplacePathRoot(
            fullPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserProfileToken);
    }

    private static string ResolvePathExpression(string expression)
    {
        var expanded = Environment.ExpandEnvironmentVariables(expression.Trim());
        return Path.GetFullPath(expanded);
    }

    private static string ReplacePathRoot(string path, string root, string token)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return path;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return token;
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? token + path[normalizedRoot.Length..]
            : path;
    }

    public static string? TryGetRepositoryRoot(string? startingDirectory = null)
    {
        var current = new DirectoryInfo(startingDirectory ?? AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GalleryBrowser.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
