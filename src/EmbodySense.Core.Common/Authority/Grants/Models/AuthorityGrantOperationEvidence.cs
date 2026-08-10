using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Retains one bounded append-only authority-grant operation disposition.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="RequestHash">The canonical exact-intent hash.</param>
/// <param name="Kind">The requested lifecycle operation.</param>
/// <param name="Outcome">The durable or fail-closed disposition.</param>
/// <param name="FailureCode">The value-free failure classification.</param>
/// <param name="GrantId">The exact target grant identity.</param>
/// <param name="ExpectedRevision">The expected optimistic revision, or zero for creation.</param>
/// <param name="ResultingGrant">The immutable committed successor reference, when one exists.</param>
/// <param name="ActorId">The exact authenticated actor attribution.</param>
/// <param name="Reason">The bounded non-secret reason.</param>
/// <param name="AuthorityEvidenceHash">The server-owned authority-evidence digest.</param>
/// <param name="DependencyEvidenceHash">The combined exact dependency-evidence digest, when evaluated.</param>
/// <param name="RecordedAtUtc">The exact trusted UTC evidence time.</param>
public sealed record AuthorityGrantOperationEvidence(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    AuthorityGrantOperationKind Kind,
    AuthorityGrantOperationOutcome Outcome,
    AuthorityGrantOperationFailureCode FailureCode,
    AuthorityGrantId GrantId,
    long ExpectedRevision,
    AuthorityGrantReference? ResultingGrant,
    AuthorityActorId ActorId,
    AuthorityPurpose Reason,
    string AuthorityEvidenceHash,
    string? DependencyEvidenceHash,
    DateTimeOffset RecordedAtUtc);
