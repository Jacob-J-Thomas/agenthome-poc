using System.Collections.Immutable;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Retains one bounded append-only authenticated-response operation without private response content, explanation, or caller assertions.</summary>
/// <param name="SchemaVersion">The operation-evidence schema version.</param>
/// <param name="OperationId">The workspace-global idempotency identifier.</param>
/// <param name="CommandHash">The canonical exact-intent command digest.</param>
/// <param name="Kind">The requested response operation.</param>
/// <param name="Outcome">The immutable terminal operation disposition.</param>
/// <param name="FailureCode">The value-free failure classification.</param>
/// <param name="Request">The exact immutable request version.</param>
/// <param name="Binding">The exact request workspace, graph, revision, node, run, and checkpoint binding.</param>
/// <param name="ExpectedLifecycleVersion">The authenticated optimistic pending lifecycle version.</param>
/// <param name="ExpectedLifecycleStatus">The authenticated optimistic pending lifecycle status.</param>
/// <param name="PreviousHead">The exact request head observed before the operation, when one existed.</param>
/// <param name="ResultHead">The exact request head after commit or deterministic no-change disposition, when one existed.</param>
/// <param name="SubmittedResponse">The exact immutable response appended by a successful submit.</param>
/// <param name="TargetResponses">The exact withdrawn response, or the exact manually selected response set in caller-authored order.</param>
/// <param name="Selection">The exact deterministic response selection committed atomically with an answered request head.</param>
/// <param name="ActorId">The authenticated actor retained as attribution, not authority.</param>
/// <param name="ActorRoleId">The trusted eligible role retained for this operation.</param>
/// <param name="AuthenticationEvidenceHash">The server-owned authentication evidence digest.</param>
/// <param name="EligibilityEvidenceHash">The exact request-policy eligibility evidence digest.</param>
/// <param name="RecordedAtUtc">The trusted UTC evidence time.</param>
public sealed partial record HumanInputResponseOperationEvidence(
    int SchemaVersion,
    string OperationId,
    string CommandHash,
    HumanInputResponseOperationKind Kind,
    HumanInputResponseOperationOutcome Outcome,
    HumanInputResponseOperationFailureCode FailureCode,
    HumanInputRequestReference Request,
    HumanInputRequestBinding Binding,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestLifecycleHead? PreviousHead,
    HumanInputRequestLifecycleHead? ResultHead,
    HumanInputResponseReference? SubmittedResponse,
    ImmutableArray<HumanInputResponseReference> TargetResponses,
    HumanInputResponseSelectionReference? Selection,
    AuthorityActorId ActorId,
    string ActorRoleId,
    string AuthenticationEvidenceHash,
    string EligibilityEvidenceHash,
    DateTimeOffset RecordedAtUtc)
{
    /// <summary>The only supported response-operation evidence schema version.</summary>
    public const int CurrentSchemaVersion = HumanInputResponseContractLimits.CurrentSchemaVersion;
}
