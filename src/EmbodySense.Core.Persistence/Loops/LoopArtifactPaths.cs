using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Produces file-name-safe default-loop definition and run artifact paths.
/// </summary>
internal static class LoopArtifactPaths
{
    /// <summary>
    /// Gets the canonical JSON path for a loop definition.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="loopId">The loop ID.</param>
    /// <returns>The definition path beneath the configured definitions directory.</returns>
    public static string GetDefinitionPath(WorkspacePaths paths, string loopId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Path.Combine(paths.LoopDefinitionsPath, ValidateArtifactId(loopId) + ".json");
    }

    /// <summary>
    /// Gets the canonical JSON path for a run beneath its loop directory.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="runId">The run ID.</param>
    /// <returns>The run path beneath the configured runs directory.</returns>
    public static string GetRunPath(WorkspacePaths paths, string loopId, string runId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return Path.Combine(paths.LoopRunsPath, ValidateArtifactId(loopId), ValidateArtifactId(runId) + ".json");
    }

    /// <summary>
    /// Validates the artifact ID.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The trimmed file-name-safe identifier.</returns>
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
