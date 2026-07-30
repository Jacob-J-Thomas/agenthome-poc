namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop definition mutation kind values.
/// </summary>
public enum CustomLoopDefinitionMutationKind
{
    /// <summary>
    /// Identifies the unknown custom loop definition mutation kind.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the create custom loop definition mutation kind.
    /// </summary>
    Create = 1,
    /// <summary>
    /// Identifies the update custom loop definition mutation kind.
    /// </summary>
    Update = 2,
    /// <summary>
    /// Identifies the delete custom loop definition mutation kind.
    /// </summary>
    Delete = 3
}
