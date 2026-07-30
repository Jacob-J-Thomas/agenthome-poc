namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop definition mutation state values.
/// </summary>
public enum CustomLoopDefinitionMutationState
{
    /// <summary>
    /// Identifies the unknown custom loop definition mutation state.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pending mutation custom loop definition mutation state.
    /// </summary>
    PendingMutation = 1,
    /// <summary>
    /// Identifies the outcome committed custom loop definition mutation state.
    /// </summary>
    OutcomeCommitted = 2
}
