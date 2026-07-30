namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop definition mutation lookup status values.
/// </summary>
public enum CustomLoopDefinitionMutationLookupStatus
{
    /// <summary>
    /// Identifies the not found custom loop definition mutation lookup status.
    /// </summary>
    NotFound = 1,
    /// <summary>
    /// Identifies the pending mutation custom loop definition mutation lookup status.
    /// </summary>
    PendingMutation = 2,
    /// <summary>
    /// Identifies the outcome committed custom loop definition mutation lookup status.
    /// </summary>
    OutcomeCommitted = 3
}
