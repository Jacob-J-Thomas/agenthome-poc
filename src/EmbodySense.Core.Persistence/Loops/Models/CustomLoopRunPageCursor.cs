namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record CustomLoopRunPageCursor(DateTimeOffset CreatedAtUtc, string RunId, string? LoopId);
