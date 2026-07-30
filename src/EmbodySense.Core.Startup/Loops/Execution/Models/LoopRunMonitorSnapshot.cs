namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunMonitorSnapshot(LoopRunSummarySnapshot Summary, string ArtifactHash);
