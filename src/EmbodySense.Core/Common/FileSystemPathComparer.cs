namespace EmbodySense.Core.Common;

internal static class FileSystemPathComparer
{
    public static bool IsWithinOrEqual(string candidatePath, string rootPath)
    {
        var normalizedCandidatePath = EnsureTrailingSeparator(candidatePath);
        var normalizedRootPath = EnsureTrailingSeparator(rootPath);
        return normalizedCandidatePath.StartsWith(normalizedRootPath, GetPathComparison());
    }

    public static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}
