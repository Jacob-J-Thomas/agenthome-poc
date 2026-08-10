using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Records bounded, value-free correlation and outcome evidence for one lifecycle operation.</summary>
/// <param name="SchemaVersion">The operation-evidence schema version.</param>
/// <param name="OperationId">The globally idempotent operation identifier.</param>
/// <param name="ActorId">The authenticated actor recorded for the operation.</param>
/// <param name="RequestHash">The lowercase SHA-256 hash binding the operation identifier to one canonical request.</param>
/// <param name="Kind">The closed requested lifecycle operation.</param>
/// <param name="Outcome">The closed durable outcome posture.</param>
/// <param name="FailureCode">The closed durable failure cause, or <see cref="GovernedLoopRevisionOperationFailureCode.None"/> for a commit.</param>
/// <param name="PreviousHead">The exact lifecycle head observed before the operation, or <see langword="null"/> for initial creation or a missing lifecycle.</param>
/// <param name="ResultHead">The exact resulting or observed lifecycle head, when one is durably known.</param>
/// <param name="CandidateRevision">The exact immutable revision proposed or created by the operation, when applicable.</param>
/// <param name="TargetRevision">The exact existing revision targeted by the operation, when applicable.</param>
/// <param name="RollbackSourcePublication">The exact historical publication selected by rollback, and only by rollback.</param>
/// <param name="AuthorityEvidenceHash">The lowercase SHA-256 digest of server-produced mutation-authority evidence.</param>
/// <param name="PublicationValidationEvidenceHash">The lowercase SHA-256 validation-evidence digest required by publication and rollback.</param>
/// <param name="RecordedAtUtc">The trusted UTC evidence time.</param>
public sealed record GovernedLoopRevisionOperationEvidence(
    int SchemaVersion,
    string OperationId,
    string ActorId,
    string RequestHash,
    GovernedLoopRevisionOperationKind Kind,
    GovernedLoopRevisionOperationOutcome Outcome,
    GovernedLoopRevisionOperationFailureCode FailureCode,
    GovernedLoopRevisionLifecycleHead? PreviousHead,
    GovernedLoopRevisionLifecycleHead? ResultHead,
    GovernedLoopRevisionReference? CandidateRevision,
    GovernedLoopRevisionReference? TargetRevision,
    GovernedLoopRevisionPublicationPin? RollbackSourcePublication,
    string AuthorityEvidenceHash,
    string? PublicationValidationEvidenceHash,
    DateTimeOffset RecordedAtUtc);
