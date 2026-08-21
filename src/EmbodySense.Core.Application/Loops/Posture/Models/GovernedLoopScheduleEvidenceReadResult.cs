namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Reports one bounded schedule-catalog posture read.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="Generation">The exact durable catalog generation.</param>
/// <param name="HasMore">Whether later schedule identities exist.</param>
/// <param name="ContinuationCursor">The exact next-page cursor when later identities exist.</param>
/// <param name="Items">The schedule evidence ordered by schedule identity.</param>
public sealed record GovernedLoopScheduleEvidenceReadResult(
    GovernedLoopOperationalEvidenceReadStatus Status,
    long Generation,
    bool HasMore,
    string? ContinuationCursor,
    IReadOnlyList<GovernedLoopScheduleEvidenceSnapshot> Items);
