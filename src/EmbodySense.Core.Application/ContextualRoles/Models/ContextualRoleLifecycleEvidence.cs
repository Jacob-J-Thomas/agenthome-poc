using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Provides bounded, secret-free evidence for one immutable contextual-role lifecycle outcome.</summary>
/// <param name="SchemaVersion">The evidence schema version, which is currently 1.</param>
/// <param name="OperationId">The stable operation identity.</param>
/// <param name="RequestHash">The canonical request hash.</param>
/// <param name="Kind">The requested mutation kind.</param>
/// <param name="RoleId">The stable role identity.</param>
/// <param name="ActorId">The stable identity attributable for requesting the mutation.</param>
/// <param name="PreviousIdentity">The exact predecessor observed by the operation, if any.</param>
/// <param name="PreviousStateHash">The full immutable predecessor-state hash, if any.</param>
/// <param name="CurrentIdentity">The exact current revision after the outcome, if any.</param>
/// <param name="CurrentStateHash">The full immutable terminal-state hash, if any.</param>
/// <param name="Sequence">The exact terminal transition sequence, or zero when no role state exists.</param>
/// <param name="State">The resulting lifecycle projection.</param>
/// <param name="Outcome">The proved terminal outcome.</param>
/// <param name="RequestedAtUtc">The original non-default UTC request time.</param>
/// <param name="RecordedAtUtc">The non-default UTC time at which the outcome evidence became durable.</param>
/// <param name="Recovered">Whether the outcome was completed by deterministic recovery of a pending intent.</param>
public sealed record ContextualRoleLifecycleEvidence(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    ContextualRoleRevisionMutationKind Kind,
    string RoleId,
    string ActorId,
    ContextualRoleRevisionIdentity? PreviousIdentity,
    string? PreviousStateHash,
    ContextualRoleRevisionIdentity? CurrentIdentity,
    string? CurrentStateHash,
    long Sequence,
    ContextualRoleLifecycleState State,
    ContextualRoleRevisionMutationStatus Outcome,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset RecordedAtUtc,
    bool Recovered);
