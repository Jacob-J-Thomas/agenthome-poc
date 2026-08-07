namespace EmbodySense.Core.Persistence.ContextualRoles.Models;

internal sealed record ContextualRoleWorkspaceAnchor(
    int SchemaVersion,
    string WorkspaceId,
    string CanonicalRootHash,
    long RootCreationTimeUtcTicks,
    DateTimeOffset CreatedAtUtc,
    string IntegrityHash);
