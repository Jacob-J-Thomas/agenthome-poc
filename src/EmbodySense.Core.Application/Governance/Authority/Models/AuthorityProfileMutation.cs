using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Requests one bounded idempotent optimistic authority-profile mutation without granting authority.</summary>
/// <param name="Kind">The explicit persisted lifecycle operation.</param>
/// <param name="OperationId">The canonical durable idempotency identity.</param>
/// <param name="ExpectedRevision">The current profile revision required before mutation, or zero for creation.</param>
/// <param name="Profile">The complete successor declaration for create or revise operations.</param>
/// <param name="ProfileId">The target profile identifier for status and tombstone operations.</param>
/// <param name="Status">The successor status for a status transition.</param>
/// <param name="ActorId">The safe actor reference retained as lifecycle evidence.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
public sealed record AuthorityProfileMutation(AuthorityProfileMutationKind Kind, string OperationId, int ExpectedRevision, AuthorityProfile? Profile, AuthorityProfileId? ProfileId, AuthorityProfileStatus? Status, AuthorityActorId ActorId, AuthorityPurpose Reason);
