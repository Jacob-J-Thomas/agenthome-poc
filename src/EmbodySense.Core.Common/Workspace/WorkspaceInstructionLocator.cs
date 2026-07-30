namespace EmbodySense.Core.Common.Workspace;

/// <summary>
/// Locates the nearest workspace instruction file and derives its display path.
/// </summary>
public static class WorkspaceInstructionLocator
{
    /// <summary>
    /// File name recognized as workspace instruction context.
    /// </summary>
    public const string FileName = "AGENTS.md";

    /// <summary>
    /// Finds the nearest workspace instruction file by walking from the supplied directory toward the file-system root.
    /// </summary>
    /// <param name="rootPath">The starting directory, resolved to an absolute path before traversal.</param>
    /// <returns>The absolute path to the nearest <see cref="FileName"/> file, or <see langword="null"/> when no ancestor contains one.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="rootPath"/> is empty or whitespace.</exception>
    public static string? FindNearest(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var directory = new DirectoryInfo(Path.GetFullPath(rootPath));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Converts an instruction path to a workspace-relative display path with forward-slash separators.
    /// </summary>
    /// <param name="rootPath">The workspace directory used as the relative-path base.</param>
    /// <param name="instructionsPath">The located instruction file path.</param>
    /// <returns>The relative display path; ancestor instructions retain the required <c>..</c> segments.</returns>
    /// <exception cref="ArgumentException">Thrown when either path is empty or whitespace.</exception>
    public static string GetDisplayPath(string rootPath, string instructionsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionsPath);

        var relativePath = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(instructionsPath));
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
