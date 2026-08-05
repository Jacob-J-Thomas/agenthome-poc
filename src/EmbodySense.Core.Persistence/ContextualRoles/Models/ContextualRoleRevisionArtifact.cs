using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

internal sealed record ContextualRoleRevisionArtifact(
    int SchemaVersion,
    string WorkspaceAnchorHash,
    ContextualRoleRevision Revision,
    string IntegrityHash);
