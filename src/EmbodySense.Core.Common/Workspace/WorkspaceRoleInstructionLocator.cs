namespace EmbodySense.Core.Common.Workspace;

public static class WorkspaceRoleInstructionLocator
{
    public const string FileName = "ROLE.md";
    public const string LegacyFileName = "AGENT.md";

    public static string ResolvePath(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var rolePath = paths.AgentFile(FileName);
        if (File.Exists(rolePath))
        {
            return rolePath;
        }

        var legacyPath = paths.AgentFile(LegacyFileName);
        return File.Exists(legacyPath) ? legacyPath : rolePath;
    }

    public static string GetDisplayPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return $".agent/{Path.GetFileName(path)}";
    }
}
