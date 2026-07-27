namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopRunPage(IReadOnlyList<CustomLoopRunSummary> Items, string? ContinuationCursor);
