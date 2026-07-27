namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunSummaryPageSnapshot(IReadOnlyList<LoopRunSummarySnapshot> Items, string? ContinuationCursor);
