namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one schedule's exact target and optimistic lifecycle posture.</summary>
public sealed record GovernedLoopSchedulePosture(
    string WorkspaceId,
    GovernedLoopOperationalSource Source,
    GovernedLoopPostureSeverity Severity,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash,
    string ScheduleId,
    string GraphId,
    string RevisionId,
    long DefinitionRevision,
    long StateRevision,
    bool Enabled,
    string State,
    string ReasonCode,
    DateTimeOffset? NextEligibleAtUtc,
    string? PendingDeliveryId,
    string? PendingDeliveryPhase,
    IReadOnlyList<GovernedLoopControlEligibility> EligibleControls);
