namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects bounded queue capacity, lifecycle, and worker-lease evidence in canonical store evidence order, which is not a worker-selection ranking.</summary>
public sealed record GovernedLoopQueuePosture(
    long Generation,
    string EvidenceHash,
    int QueuedEntries,
    long QueuedReservationBytes,
    int RetainedEntries,
    long RetainedReservationBytes,
    bool PersistenceBackpressured,
    bool HasMore,
    string? ContinuationCursor,
    IReadOnlyList<GovernedLoopQueueItemPosture> Items,
    IReadOnlyList<GovernedLoopControlEligibility> EligibleControls);
