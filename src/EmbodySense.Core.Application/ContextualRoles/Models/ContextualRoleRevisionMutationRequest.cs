using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Submits one idempotent contextual-role revision or lifecycle mutation.</summary>
/// <param name="OperationId">The stable idempotency identity for this exact intent.</param>
/// <param name="RequestHash">The canonical hash produced by <see cref="ContextualRoleRevisionMutationRequestHash"/>.</param>
/// <param name="Kind">The explicit lifecycle mutation kind.</param>
/// <param name="RoleId">The stable contextual-role identifier.</param>
/// <param name="ActorId">The stable identity attributable for requesting the mutation.</param>
/// <param name="Revision">The proposed immutable revision for create or replace; otherwise <see langword="null"/>.</param>
/// <param name="ExpectedPreviousIdentity">The exact expected current revision, or <see langword="null"/> only for create.</param>
/// <param name="RequestedAtUtc">The non-default UTC time at which the mutation was requested.</param>
public sealed record ContextualRoleRevisionMutationRequest(
    string OperationId,
    string RequestHash,
    ContextualRoleRevisionMutationKind Kind,
    string RoleId,
    string ActorId,
    ContextualRoleRevision? Revision,
    ContextualRoleRevisionIdentity? ExpectedPreviousIdentity,
    DateTimeOffset RequestedAtUtc);
