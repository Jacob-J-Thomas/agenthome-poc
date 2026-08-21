namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Reports one closed, bounded durable-run page.</summary>
public sealed record GovernedLoopRunEvidenceReadResult(
    GovernedLoopOperationalEvidenceReadStatus Status,
    bool HasMore,
    string? ContinuationCursor,
    IReadOnlyList<GovernedLoopRunEvidenceSnapshot> Items);
