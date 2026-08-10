namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Identifies one bounded read-only dependent of an exact contextual-role revision.</summary>
/// <param name="Kind">The closed dependent-family token.</param>
/// <param name="Identity">The stable dependent identity.</param>
/// <param name="Revision">The exact positive dependent revision.</param>
public sealed record ContextualRoleDependencyImpact(string Kind, string Identity, long Revision);
