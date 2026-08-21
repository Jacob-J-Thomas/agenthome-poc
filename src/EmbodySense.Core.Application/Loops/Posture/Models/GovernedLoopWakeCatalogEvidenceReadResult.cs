namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Reports one bounded sleep-catalog posture read.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="Generation">The exact durable catalog generation.</param>
/// <param name="HasMore">Whether later checkpoint identities exist.</param>
/// <param name="ContinuationCursor">The exact next-page cursor when later identities exist.</param>
/// <param name="Items">The checkpoint and wake evidence ordered by checkpoint identity.</param>
public sealed record GovernedLoopWakeCatalogEvidenceReadResult(
    GovernedLoopOperationalEvidenceReadStatus Status,
    long Generation,
    bool HasMore,
    string? ContinuationCursor,
    IReadOnlyList<GovernedLoopWakeEvidenceSnapshot> Items);
