namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects the exact local-background coordinator owner, lease, lifecycle, and failure head.</summary>
public sealed record GovernedLoopCoordinatorPosture(
    string WorkspaceId,
    GovernedLoopOperationalSource Source,
    GovernedLoopPostureSeverity Severity,
    DateTimeOffset ObservedAtUtc,
    string? EvidenceHash,
    string State,
    string ReasonCode,
    string? CoordinatorId,
    string? OwnerId,
    long? OwnershipEpoch,
    DateTimeOffset? HeartbeatRecordedAtUtc,
    DateTimeOffset? LeaseExpiresAtUtc,
    bool LeaseExpired,
    long LatestFailureSequence,
    string? LatestFailureHash,
    IReadOnlyList<GovernedLoopControlEligibility> EligibleControls);
