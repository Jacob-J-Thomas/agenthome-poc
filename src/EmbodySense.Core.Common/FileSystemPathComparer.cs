namespace EmbodySense.Core.Common;

/// <summary>
/// Compares file-system paths using the host platform's case rules.
/// </summary>
public static class FileSystemPathComparer
{
    /// <summary>
    /// Determines whether a normalized candidate path is lexically within or equal to a normalized root path.
    /// </summary>
    /// <param name="candidatePath">The normalized absolute candidate path.</param>
    /// <param name="rootPath">The normalized absolute root path.</param>
    /// <returns><see langword="true"/> when the candidate equals the root or starts with the root plus a directory separator under the host path-comparison rules; otherwise, <see langword="false"/>.</returns>
    /// <remarks>This method performs no file-system access or path canonicalization. Callers must normalize paths before comparing them.</remarks>
    public static bool IsWithinOrEqual(string candidatePath, string rootPath)
    {
        var normalizedCandidatePath = EnsureTrailingSeparator(candidatePath);
        var normalizedRootPath = EnsureTrailingSeparator(rootPath);
        return normalizedCandidatePath.StartsWith(normalizedRootPath, GetPathComparison());
    }

    /// <summary>
    /// Gets the ordinal comparison used for file-system paths on the current host.
    /// </summary>
    /// <returns><see cref="StringComparison.OrdinalIgnoreCase"/> on Windows; otherwise, <see cref="StringComparison.Ordinal"/>.</returns>
    public static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}
