using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Represents a contextual-role revision mutation result with bounded failure evidence when available.</summary>
/// <param name="Status">The closed mutation outcome.</param>
/// <param name="OperationId">The stable operation identity, or an empty value when the request could not be validated.</param>
/// <param name="RequestHash">The canonical request hash, or an empty value when the request could not be validated.</param>
/// <param name="Kind">The requested lifecycle mutation kind.</param>
/// <param name="Revision">The exact current immutable revision when one is proved; otherwise <see langword="null"/>.</param>
/// <param name="Evidence">The bounded terminal lifecycle evidence when one is durably proved.</param>
/// <param name="ValidationErrors">Structured validation errors when <paramref name="Status"/> is invalid.</param>
public sealed record ContextualRoleRevisionMutationResult(
    ContextualRoleRevisionMutationStatus Status,
    string OperationId,
    string RequestHash,
    ContextualRoleRevisionMutationKind Kind,
    ContextualRoleRevision? Revision,
    ContextualRoleLifecycleEvidence? Evidence,
    IReadOnlyList<ContextualRoleValidationError> ValidationErrors)
{
    /// <summary>Gets bounded persistence failure evidence when an unavailable or ambiguous outcome can be attributed to one guarded stage.</summary>
    public ContextualRoleRevisionMutationDiagnostic? Diagnostic { get; init; }
}
