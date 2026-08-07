using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

internal sealed record ContextualRoleMutationResultArtifact(
    int SchemaVersion,
    string WorkspaceAnchorHash,
    ContextualRoleRevisionMutationStatus Status,
    string OperationId,
    string RequestHash,
    ContextualRoleRevisionMutationKind Kind,
    ContextualRoleRevision? Revision,
    ContextualRoleLifecycleEvidence Evidence,
    string IntegrityHash);
