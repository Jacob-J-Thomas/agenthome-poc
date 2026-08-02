namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Requests the proved current lifecycle projection for one stable contextual role.</summary>
/// <param name="RoleId">The stable contextual-role identifier.</param>
public sealed record ContextualRoleLifecycleReadRequest(string RoleId);
