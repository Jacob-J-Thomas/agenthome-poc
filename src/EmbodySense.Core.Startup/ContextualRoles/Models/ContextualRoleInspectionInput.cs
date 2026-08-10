namespace EmbodySense.Core.Startup.ContextualRoles.Models;

/// <summary>Supplies one exact caller-observed contextual-role revision identity.</summary>
/// <param name="RoleId">The stable role identity.</param>
/// <param name="Revision">The exact positive immutable revision.</param>
/// <param name="ContentHash">The exact canonical semantic revision hash.</param>
public sealed record ContextualRoleInspectionInput(string RoleId, int Revision, string ContentHash);
