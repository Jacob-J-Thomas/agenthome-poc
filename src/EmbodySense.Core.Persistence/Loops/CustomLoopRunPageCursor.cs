namespace EmbodySense.Core.Persistence.Loops;

internal sealed record CustomLoopRunPageCursor(DateTimeOffset CreatedAtUtc, string RunId, string? LoopId);
