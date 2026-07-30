namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop run page.
/// </summary>
/// <param name="Items">The items.</param>
/// <param name="ContinuationCursor">The continuation cursor.</param>
public sealed record CustomLoopRunPage(IReadOnlyList<CustomLoopRunSummary> Items, string? ContinuationCursor);
