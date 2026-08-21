namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Retains one exact target in a bounded restart-reconcilable control batch.</summary>
public sealed record GovernedLoopOperationalControlProgress(
    string TargetId,
    long ExpectedRevision,
    string ExpectedEvidenceHash,
    GovernedLoopOperationalControlStatus Status,
    long? CurrentRevision,
    string? CurrentEvidenceHash,
    string ReasonCode);
