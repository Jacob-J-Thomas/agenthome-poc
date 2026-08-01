namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>
/// Identifies the active-turn-set operation currently holding the workspace coordination lease.
/// </summary>
public enum DefaultConversationTurnStoreOperation
{
    /// <summary>Creates a new default-conversation turn artifact.</summary>
    Create,

    /// <summary>Updates or archives a default-conversation turn artifact.</summary>
    Update,

    /// <summary>Loads one default-conversation turn artifact.</summary>
    Load,

    /// <summary>Lists and reconciles active default-conversation turn artifacts.</summary>
    List
}
