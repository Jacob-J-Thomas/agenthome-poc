namespace EmbodySense.Core.Persistence.Loops;

internal sealed record CustomLoopRunPageCursor(DateTimeOffset UpdatedAtUtc, DateTimeOffset CreatedAtUtc, string RunId, string? LoopId);
