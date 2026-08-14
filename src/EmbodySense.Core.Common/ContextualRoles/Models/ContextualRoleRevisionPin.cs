namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Binds a consumer to one exact immutable contextual-role revision and canonical content hash.</summary>
/// <param name="Identity">The stable role and exact revision.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 semantic content hash.</param>
public sealed record ContextualRoleRevisionPin(ContextualRoleRevisionIdentity Identity, string ContentHash);
