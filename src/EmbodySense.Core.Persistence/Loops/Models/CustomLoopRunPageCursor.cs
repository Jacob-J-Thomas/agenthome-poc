namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Represents a custom loop run page cursor.
/// </summary>
/// <param name="CreatedAtUtc">The created at UTC.</param>
/// <param name="RunId">The run ID.</param>
/// <param name="LoopId">The loop ID.</param>
internal sealed record CustomLoopRunPageCursor(DateTimeOffset CreatedAtUtc, string RunId, string? LoopId);
