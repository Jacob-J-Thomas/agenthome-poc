namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Provides one bounded cursor page of retained run summaries.
/// </summary>
/// <param name="Items">The items.</param>
/// <param name="ContinuationCursor">The continuation cursor.</param>
public sealed record LoopRunSummaryPageSnapshot(IReadOnlyList<LoopRunSummarySnapshot> Items, string? ContinuationCursor);
