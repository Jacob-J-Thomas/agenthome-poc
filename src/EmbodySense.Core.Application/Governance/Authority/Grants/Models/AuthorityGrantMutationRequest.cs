using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Requests one authenticated, optimistic authority-grant lifecycle operation.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="Kind">The requested lifecycle operation.</param>
/// <param name="GrantId">The stable grant identity.</param>
/// <param name="ExpectedRevision">The exact current revision, or zero for creation.</param>
/// <param name="ExpectedStatus">The exact current posture, or unknown for creation.</param>
/// <param name="CandidateBinding">The exact successor dependency pins for create, narrow, or replace.</param>
/// <param name="CandidateCeiling">The requested successor ceiling for create, narrow, or replace.</param>
/// <param name="CandidateBoundary">The requested successor boundary for create, narrow, or replace.</param>
/// <param name="ActorId">The authenticated actor attribution.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
/// <param name="RequestHash">The canonical exact-intent hash.</param>
public sealed record AuthorityGrantMutationRequest(
    int SchemaVersion,
    string OperationId,
    AuthorityGrantOperationKind Kind,
    AuthorityGrantId GrantId,
    long ExpectedRevision,
    AuthorityGrantLifecycleStatus ExpectedStatus,
    AuthorityGrantBinding? CandidateBinding,
    AuthorityCeiling? CandidateCeiling,
    AuthorityGrantBoundary? CandidateBoundary,
    AuthorityActorId ActorId,
    AuthorityPurpose Reason,
    string RequestHash);
