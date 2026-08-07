using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Projects one role's proved current revision and lifecycle without granting effective authority.</summary>
/// <param name="SchemaVersion">The projection schema version, which is currently 1.</param>
/// <param name="RoleId">The stable role identifier.</param>
/// <param name="CurrentIdentity">The retained current immutable revision identity.</param>
/// <param name="State">The current lifecycle state.</param>
/// <param name="LastOperationId">The stable identity of the latest proved lifecycle mutation.</param>
/// <param name="LastMutationKind">The latest proved lifecycle mutation kind.</param>
/// <param name="UpdatedAtUtc">The non-default UTC time at which the projection was last proved.</param>
public sealed record ContextualRoleLifecycleSnapshot(
    int SchemaVersion,
    string RoleId,
    ContextualRoleRevisionIdentity CurrentIdentity,
    ContextualRoleLifecycleState State,
    string LastOperationId,
    ContextualRoleRevisionMutationKind LastMutationKind,
    DateTimeOffset UpdatedAtUtc);
