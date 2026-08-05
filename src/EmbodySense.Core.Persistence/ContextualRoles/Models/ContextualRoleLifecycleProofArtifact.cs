using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

internal sealed record ContextualRoleLifecycleProofArtifact(
    int SchemaVersion,
    string WorkspaceAnchorHash,
    ContextualRoleLifecycleEvidence Evidence,
    string IntegrityHash);
