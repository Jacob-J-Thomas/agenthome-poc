using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Represents the result of reading one exact contextual-role revision.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="Revision">The immutable revision when <paramref name="Status"/> is found; otherwise <see langword="null"/>.</param>
/// <param name="ValidationErrors">Structured request errors when <paramref name="Status"/> is invalid.</param>
public sealed record ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus Status, ContextualRoleRevision? Revision, IReadOnlyList<ContextualRoleValidationError> ValidationErrors);
