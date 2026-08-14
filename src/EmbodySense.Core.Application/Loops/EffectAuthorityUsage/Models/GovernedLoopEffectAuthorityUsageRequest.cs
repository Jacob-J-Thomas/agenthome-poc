using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;

/// <summary>Requests one atomic non-renewable authority-usage check and optional reservation.</summary>
/// <param name="SchemaVersion">The persisted contract schema version.</param>
/// <param name="Grant">The exact immutable admitted grant revision and content hash.</param>
/// <param name="CompletionConstraint">The exact admitted grant completion constraint.</param>
/// <param name="AdmissionReceiptHash">The canonical immutable admission receipt hash.</param>
/// <param name="RunId">The exact bound run identity.</param>
/// <param name="ExecutionGeneration">The exact positive frontier generation.</param>
/// <param name="NodeId">The exact graph-node identity.</param>
/// <param name="NodeAttempt">The exact positive node attempt.</param>
/// <param name="EffectOperationId">The exact stable effect-operation identity.</param>
/// <param name="BoundaryKind">The exact effect boundary being checked.</param>
/// <param name="MaxTargetCount">The current non-renewable distinct-target ceiling for the exact admitted run across every generation, node, and retry.</param>
/// <param name="TargetFingerprint">The optional SHA-256 fingerprint of the stable server-owned effect target: a resolved absolute workspace path or immutable invoking-conversation identity.</param>
/// <param name="EvaluatedAtUtc">The trusted exact UTC instant at which usage was checked.</param>
public sealed record GovernedLoopEffectAuthorityUsageRequest(
    int SchemaVersion,
    AuthorityGrantReference Grant,
    AuthorityGrantCompletionConstraintKind CompletionConstraint,
    string AdmissionReceiptHash,
    string RunId,
    long ExecutionGeneration,
    string NodeId,
    int NodeAttempt,
    string EffectOperationId,
    GovernedLoopEffectBoundaryKind BoundaryKind,
    int MaxTargetCount,
    string? TargetFingerprint,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>Gets the only supported POC schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
