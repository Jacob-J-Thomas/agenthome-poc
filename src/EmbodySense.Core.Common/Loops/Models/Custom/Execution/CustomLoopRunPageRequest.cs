namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopRunPageRequest(int MaximumCount, string? LoopId = null, string? Cursor = null);
