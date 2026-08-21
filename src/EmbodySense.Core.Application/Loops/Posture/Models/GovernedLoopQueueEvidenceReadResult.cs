using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Reports one closed, bounded, authoritative queue page.</summary>
public sealed record GovernedLoopQueueEvidenceReadResult(
    GovernedLoopOperationalEvidenceReadStatus Status,
    long Generation,
    int QueuedEntries,
    long QueuedReservationBytes,
    int RetainedEntries,
    long RetainedReservationBytes,
    bool PersistenceBackpressured,
    bool HasMore,
    string? ContinuationCursor,
    IReadOnlyList<TriggerQueueEntry> Items);
