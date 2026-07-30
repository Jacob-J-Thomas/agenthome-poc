namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Combines a lightweight run summary with the validator for its full durable artifact.
/// </summary>
/// <param name="Summary">The summary.</param>
/// <param name="ArtifactHash">The artifact hash.</param>
public sealed record LoopRunMonitorSnapshot(LoopRunSummarySnapshot Summary, string ArtifactHash);
