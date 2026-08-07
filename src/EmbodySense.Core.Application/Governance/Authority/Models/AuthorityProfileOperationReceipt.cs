using EmbodySense.Core.Common.Authority;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>Projects immutable bounded lifecycle operation evidence without prompts, private targets, credentials, or secrets.</summary>
/// <param name="OperationId">The canonical idempotency identity.</param>
/// <param name="RequestHash">The canonical hash binding the operation id to exact intent.</param>
/// <param name="Kind">The persisted lifecycle operation.</param>
/// <param name="Outcome">The committed outcome.</param>
/// <param name="ProfileId">The affected profile identifier.</param>
/// <param name="ResultingRevision">The resulting profile revision when a revision was appended.</param>
/// <param name="ActorId">The safe actor reference.</param>
/// <param name="Reason">The bounded non-secret reason.</param>
/// <param name="RecordedAtUtc">The trusted receipt time.</param>
public sealed record AuthorityProfileOperationReceipt(string OperationId, string RequestHash, AuthorityProfileMutationKind Kind, AuthorityProfileMutationStatus Outcome, AuthorityProfileId ProfileId, int? ResultingRevision, AuthorityActorId ActorId, AuthorityPurpose Reason, DateTimeOffset RecordedAtUtc);
