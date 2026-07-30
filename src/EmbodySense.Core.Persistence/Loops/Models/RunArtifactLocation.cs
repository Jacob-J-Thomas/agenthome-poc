namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a run artifact location.
/// </summary>
/// <param name="Path">The path.</param>
/// <param name="LoopId">The loop ID.</param>
/// <param name="RunId">The run ID.</param>
internal sealed record RunArtifactLocation(string Path, string LoopId, string RunId);
