using System.IO;

namespace GalleryBrowser.Services;

internal static class GalleryCreatorPathRules
{
    internal const string OtherCreatorName = "その他";
    internal const string OtherCreatorFolderName = "AI生成_FANZA_DLsite販売作品";
    internal const string LegacyOtherCreatorFolderName = "【AI生成_FANZA_DLsite販売作品】";
    internal static readonly IReadOnlyList<string> OtherCreatorFolderNames =
        [OtherCreatorFolderName, LegacyOtherCreatorFolderName];

    internal static string ResolveCreatorOverride(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return EnumeratePathSegments(path).Any(segment =>
            OtherCreatorFolderNames.Contains(
                segment,
                StringComparer.OrdinalIgnoreCase))
            ? OtherCreatorName
            : string.Empty;
    }

    internal static bool IsCreatorBoundary(string segment, string creator)
    {
        if (string.Equals(
                segment,
                $"【{creator.Trim()}】",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
                   creator.Trim(),
                   OtherCreatorName,
                   StringComparison.OrdinalIgnoreCase)
               && OtherCreatorFolderNames.Contains(
                   segment,
                   StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumeratePathSegments(string path) =>
        path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
