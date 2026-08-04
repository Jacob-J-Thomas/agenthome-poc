using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Requests one exact immutable contextual-role revision.</summary>
/// <param name="Identity">The stable role and immutable revision identity to read.</param>
public sealed record ContextualRoleRevisionReadRequest(ContextualRoleRevisionIdentity Identity);
