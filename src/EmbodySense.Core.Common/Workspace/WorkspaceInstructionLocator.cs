namespace EmbodySense.Core.Common.Workspace;

public static class WorkspaceInstructionLocator
{
    public const string FileName = "AGENTS.md";

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

    public static string GetDisplayPath(string rootPath, string instructionsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructionsPath);

        var relativePath = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(instructionsPath));
        return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
