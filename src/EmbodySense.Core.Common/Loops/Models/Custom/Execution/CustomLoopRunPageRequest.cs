namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop run page request.
/// </summary>
/// <param name="MaximumCount">The maximum count.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="Cursor">The cursor.</param>
public sealed record CustomLoopRunPageRequest(int MaximumCount, string? LoopId = null, string? Cursor = null);
