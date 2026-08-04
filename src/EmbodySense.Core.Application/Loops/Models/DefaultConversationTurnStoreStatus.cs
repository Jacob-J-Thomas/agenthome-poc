namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the result of one optimistic durable turn-record mutation.
/// </summary>
public enum DefaultConversationTurnStoreStatus
{
    /// <summary>No concrete result.</summary>
    Unknown = 0,
    /// <summary>A new record was created.</summary>
    Created,
    /// <summary>An existing record advanced by one lifecycle version.</summary>
    Updated,
    /// <summary>The exact mutation had already committed.</summary>
    Replay,
    /// <summary>The durable identity, version, or append-only history conflicted.</summary>
    Conflict
}
