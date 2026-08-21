using EmbodySense.Core.Common.Loops.Posture.Models;

namespace EmbodySense.Core.Startup.Loops.Posture.Models;

/// <summary>Requests one exact operational mutation without accepting caller-supplied authority scope.</summary>
public sealed record LoopOperationalControlInput(
    string OperationId,
    GovernedLoopOperationalControlKind Kind,
    string TargetId,
    long ExpectedRevision,
    string ExpectedEvidenceHash,
    string ExpectedAuthorityEvidenceHash,
    int MaximumBatchItems = 1);
