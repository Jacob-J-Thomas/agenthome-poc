using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Retains bounded append-only Human Input request lifecycle operation evidence without private request content.</summary>
/// <param name="SchemaVersion">The operation-evidence schema version.</param>
/// <param name="OperationId">The workspace-global idempotency identifier.</param>
/// <param name="RequestHash">The canonical lifecycle-mutation request hash.</param>
/// <param name="Kind">The requested lifecycle operation.</param>
/// <param name="Outcome">The immutable terminal operation disposition.</param>
/// <param name="FailureCode">The value-free failure classification.</param>
/// <param name="TargetRequestId">The stable request lifecycle targeted by the operation.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic lifecycle version supplied by authenticated intent, or zero for create.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic lifecycle status supplied by authenticated intent, or unknown for create.</param>
/// <param name="ExpectedRequest">The exact optimistic request reference supplied by authenticated intent, or null for create.</param>
/// <param name="ExpectedBinding">The exact optimistic request binding supplied by authenticated intent, or null for create.</param>
/// <param name="PreviousHead">The exact target head observed before the operation, when one existed.</param>
/// <param name="ResultHead">The exact target head after the operation or deterministic no-change disposition.</param>
/// <param name="RelatedRequestId">The second request affected only by supersede.</param>
/// <param name="RelatedPreviousHead">The exact related head observed before supersede; null for a committed new request.</param>
/// <param name="RelatedResultHead">The exact related head after supersede.</param>
/// <param name="CandidateRequest">The exact immutable request version proposed by a candidate-bearing operation.</param>
/// <param name="ActorId">The authenticated actor retained as attribution, not authority.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
/// <param name="GrantReference">The exact active grant used by a delivery-producing operation.</param>
/// <param name="AuthorityEvidenceHash">The server-owned actor-authorization evidence digest.</param>
/// <param name="GrantDependencyEvidenceHash">The exact grant-dependency evidence digest when a grant is required.</param>
/// <param name="RecordedAtUtc">The trusted UTC evidence time.</param>
public sealed partial record HumanInputRequestLifecycleOperationEvidence(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    HumanInputRequestLifecycleOperationKind Kind,
    HumanInputRequestLifecycleOperationOutcome Outcome,
    HumanInputRequestLifecycleOperationFailureCode FailureCode,
    string TargetRequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestReference? ExpectedRequest,
    HumanInputRequestBinding? ExpectedBinding,
    HumanInputRequestLifecycleHead? PreviousHead,
    HumanInputRequestLifecycleHead? ResultHead,
    string? RelatedRequestId,
    HumanInputRequestLifecycleHead? RelatedPreviousHead,
    HumanInputRequestLifecycleHead? RelatedResultHead,
    HumanInputRequestReference? CandidateRequest,
    AuthorityActorId ActorId,
    AuthorityPurpose Reason,
    AuthorityGrantReference? GrantReference,
    string AuthorityEvidenceHash,
    string? GrantDependencyEvidenceHash,
    DateTimeOffset RecordedAtUtc);
