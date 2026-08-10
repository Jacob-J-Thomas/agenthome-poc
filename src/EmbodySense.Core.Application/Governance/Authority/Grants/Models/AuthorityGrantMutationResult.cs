using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns bounded exact evidence for one authority-grant lifecycle operation.</summary>
/// <param name="Status">The application outcome.</param>
/// <param name="OperationId">The operation identity, or an empty value when absent.</param>
/// <param name="RequestHash">The canonical request hash, or an empty value when unavailable.</param>
/// <param name="Grant">The committed, replayed, or safely observed immutable grant.</param>
/// <param name="Evidence">The exact durable terminal operation evidence when known.</param>
/// <param name="ValidationErrors">Bounded request validation errors.</param>
public sealed record AuthorityGrantMutationResult(
    AuthorityGrantMutationStatus Status,
    string OperationId,
    string RequestHash,
    AuthorityGrant? Grant,
    AuthorityGrantOperationEvidence? Evidence,
    IReadOnlyList<AuthorityGrantMutationValidationError> ValidationErrors);
