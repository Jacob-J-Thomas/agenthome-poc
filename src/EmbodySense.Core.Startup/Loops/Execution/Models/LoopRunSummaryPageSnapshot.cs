namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunSummaryPageSnapshot(IReadOnlyList<LoopRunSummarySnapshot> Items, string? ContinuationCursor);
