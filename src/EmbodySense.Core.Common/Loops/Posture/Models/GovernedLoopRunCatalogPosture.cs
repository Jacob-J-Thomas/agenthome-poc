namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one bounded durable-run page.</summary>
public sealed record GovernedLoopRunCatalogPosture(bool HasMore, string? ContinuationCursor, IReadOnlyList<GovernedLoopRunPosture> Items);
