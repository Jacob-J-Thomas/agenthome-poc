using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Retains exact, non-secret evidence identifying the parent authority and issuer context.</summary>
/// <param name="WorkspaceId">The canonical workspace scope.</param>
/// <param name="ParentExecution">The exact parent run, revision, and generation.</param>
/// <param name="OriginNodeId">The exact issuing node identity.</param>
/// <param name="OriginNodeAttempt">The exact positive issuing-node attempt.</param>
/// <param name="ParentAdmissionReceiptHash">The exact immutable admission receipt hash.</param>
/// <param name="ActorId">The non-secret authenticated actor attribution from admission.</param>
/// <param name="GrantReference">The exact immutable parent grant revision.</param>
/// <param name="GrantBinding">The exact profile, role, and loop pins carried by that grant.</param>
/// <param name="OriginBindingEvidenceHash">The stable server-owned origin evidence hash.</param>
/// <param name="GrantDependencyEvidenceHash">The exact current grant dependency-evidence hash.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC evaluation time.</param>
/// <param name="ContentHash">The canonical hash over this reference except this field.</param>
public sealed record AuthorityDelegationParentEvidenceReference(
    string WorkspaceId,
    GovernedLoopExecutionBinding ParentExecution,
    string OriginNodeId,
    int OriginNodeAttempt,
    string ParentAdmissionReceiptHash,
    AuthorityActorId ActorId,
    AuthorityGrantReference GrantReference,
    AuthorityGrantBinding GrantBinding,
    string OriginBindingEvidenceHash,
    string GrantDependencyEvidenceHash,
    DateTimeOffset EvaluatedAtUtc,
    string ContentHash);
