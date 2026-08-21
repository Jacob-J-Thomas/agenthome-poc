namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Projects one durable governed run with exact graph-revision and lifecycle evidence.</summary>
public sealed record GovernedLoopRunPosture(
    string WorkspaceId,
    GovernedLoopOperationalSource Source,
    GovernedLoopPostureSeverity Severity,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash,
    string RunId,
    string LoopId,
    string? GraphId,
    string? RevisionId,
    long LifecycleVersion,
    string State,
    string ReasonCode,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<GovernedLoopControlEligibility> EligibleControls);
