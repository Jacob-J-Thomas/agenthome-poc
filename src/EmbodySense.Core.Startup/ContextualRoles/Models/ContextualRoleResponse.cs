namespace EmbodySense.Core.Startup.ContextualRoles.Models;

/// <summary>Returns exact current role and registered-source inspection posture.</summary>
/// <param name="Status">The stable closed inspection token.</param>
/// <param name="Role">The redacted current posture when exact role evidence was proved.</param>
/// <param name="Error">The value-free error when the role is not ready.</param>
public sealed record ContextualRoleResponse(string Status, ContextualRoleSnapshot? Role, ContextualRoleError? Error);
