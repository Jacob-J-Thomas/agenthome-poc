using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

internal sealed record GovernedLoopEffectAuthorityTargetReservation(
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
    string TargetFingerprint,
    DateTimeOffset ReservedAtUtc)
{
    internal const int CurrentSchemaVersion = 1;
}
