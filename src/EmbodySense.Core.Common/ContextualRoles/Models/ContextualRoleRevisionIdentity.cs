namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Identifies one immutable revision of a stable contextual role.</summary>
/// <param name="RoleId">The stable role identifier.</param>
/// <param name="Revision">The positive revision number, which is never reinterpreted after attribution.</param>
public sealed record ContextualRoleRevisionIdentity(string RoleId, int Revision);
