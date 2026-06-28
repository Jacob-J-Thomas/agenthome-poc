using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

internal static class LoopArtifactPaths
{
    public static string GetDefinitionPath(WorkspacePaths paths, string loopId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Path.Combine(paths.LoopDefinitionsPath, ValidateArtifactId(loopId) + ".json");
    }

    public static string GetRunPath(WorkspacePaths paths, string loopId, string runId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Path.Combine(paths.LoopRunsPath, ValidateArtifactId(loopId), ValidateArtifactId(runId) + ".json");
    }

    public static string ValidateArtifactId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim();
        if (normalized is "." or ".." || normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || normalized.Contains('/') || normalized.Contains('\\'))
        {
            throw new ArgumentException("Loop artifact ids must be file-name safe values.", nameof(value));
        }

        return normalized;
    }
}
