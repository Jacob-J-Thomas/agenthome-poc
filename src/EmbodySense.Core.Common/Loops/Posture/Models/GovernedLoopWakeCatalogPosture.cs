namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one bounded sleeping-checkpoint page.</summary>
public sealed record GovernedLoopWakeCatalogPosture(long Generation, bool HasMore, string? ContinuationCursor, IReadOnlyList<GovernedLoopWakePosture> Items);
