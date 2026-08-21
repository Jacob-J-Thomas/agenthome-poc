namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one durable trigger delivery without payload or secret values. Queue position remains absent when coordinator-local fairness prevents an authoritative rank.</summary>
public sealed record GovernedLoopQueueItemPosture(
    string WorkspaceId,
    GovernedLoopOperationalSource Source,
    GovernedLoopPostureSeverity Severity,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash,
    string DeliveryId,
    string LoopId,
    string? GraphId,
    string? RevisionId,
    string State,
    string ReasonCode,
    int? QueuePosition,
    DateTimeOffset EligibleAtUtc,
    long Revision,
    string? WorkerId,
    long? WorkerGeneration,
    DateTimeOffset? WorkerLeaseExpiresAtUtc,
    bool WorkerLeaseExpired,
    IReadOnlyList<GovernedLoopControlEligibility> EligibleControls);
