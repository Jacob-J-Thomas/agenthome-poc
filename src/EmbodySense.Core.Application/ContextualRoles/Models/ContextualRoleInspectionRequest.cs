namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Requests validation of one exact caller-observed contextual-role revision.</summary>
/// <param name="RoleId">The stable contextual-role identity.</param>
/// <param name="Revision">The exact positive immutable revision.</param>
/// <param name="ContentHash">The exact canonical semantic revision hash observed by the caller.</param>
public sealed record ContextualRoleInspectionRequest(string RoleId, int Revision, string ContentHash);
