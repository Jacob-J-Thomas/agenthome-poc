namespace EmbodySense.Core.Startup.ContextualRoles.Models;

/// <summary>Projects one bounded read-only contextual-role dependent.</summary>
/// <param name="Kind">The closed dependent-family token.</param>
/// <param name="Identity">The stable dependent identity.</param>
/// <param name="Revision">The exact dependent revision.</param>
public sealed record ContextualRoleDependentSnapshot(string Kind, string Identity, long Revision);
