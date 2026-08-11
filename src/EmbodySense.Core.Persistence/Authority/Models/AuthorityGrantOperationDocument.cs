using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityGrantOperationDocument(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    AuthorityGrantOperationKind Kind,
    AuthorityGrantOperationOutcome Outcome,
    AuthorityGrantOperationFailureCode FailureCode,
    string GrantId,
    long ExpectedRevision,
    string? ResultingGrantId,
    int? ResultingGrantRevision,
    string? ResultingGrantContentHash,
    string ActorId,
    string Reason,
    string AuthorityEvidenceHash,
    string? DependencyEvidenceHash,
    DateTimeOffset RecordedAtUtc);
