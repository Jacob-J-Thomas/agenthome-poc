using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

internal sealed record ContextualRolePrimaryStateArtifact(
    int SchemaVersion,
    string WorkspaceAnchorHash,
    string RoleId,
    ContextualRoleRevisionIdentity CurrentIdentity,
    ContextualRoleLifecycleState State,
    string LastOperationId,
    ContextualRoleRevisionMutationKind LastMutationKind,
    long Sequence,
    DateTimeOffset UpdatedAtUtc,
    string IntegrityHash);
