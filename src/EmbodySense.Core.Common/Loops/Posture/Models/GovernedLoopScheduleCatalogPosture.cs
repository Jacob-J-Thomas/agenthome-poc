namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one bounded schedule-catalog page.</summary>
public sealed record GovernedLoopScheduleCatalogPosture(long Generation, bool HasMore, string? ContinuationCursor, IReadOnlyList<GovernedLoopSchedulePosture> Items);
