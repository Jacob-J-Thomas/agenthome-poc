using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Represents a persistence-agnostic contextual-role revision mutation result.</summary>
/// <param name="Status">The closed mutation outcome.</param>
/// <param name="Revision">The accepted immutable revision when <paramref name="Status"/> is accepted; otherwise <see langword="null"/>.</param>
/// <param name="ValidationErrors">Structured validation errors when <paramref name="Status"/> is invalid.</param>
public sealed record ContextualRoleRevisionMutationResult(ContextualRoleRevisionMutationStatus Status, ContextualRoleRevision? Revision, IReadOnlyList<ContextualRoleValidationError> ValidationErrors);
