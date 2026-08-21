namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one sleeping checkpoint and exact revision-pinned wake posture.</summary>
public sealed record GovernedLoopWakePosture(
    string WorkspaceId,
    GovernedLoopOperationalSource Source,
    GovernedLoopPostureSeverity Severity,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash,
    string CheckpointId,
    string RunId,
    string NodeId,
    string GraphId,
    string RevisionId,
    string State,
    string ReasonCode,
    DateTimeOffset? WakeAtUtc,
    long? WakeEvidenceVersion,
    string? WakeId,
    IReadOnlyList<GovernedLoopControlEligibility> EligibleControls);
