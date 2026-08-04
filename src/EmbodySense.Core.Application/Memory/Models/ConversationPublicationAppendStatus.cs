namespace EmbodySense.Core.Application.Memory.Models;

/// <summary>
/// Identifies the atomic outcome of one identity-bearing transcript publication.
/// </summary>
public enum ConversationPublicationAppendStatus
{
    /// <summary>No concrete outcome was selected.</summary>
    Unknown = 0,
    /// <summary>The exact publication was appended by this call.</summary>
    Appended,
    /// <summary>The exact publication identity was already present after the expected prefix.</summary>
    AlreadyPresent,
    /// <summary>The conversation identity, prefix, or publication ownership conflicted.</summary>
    Conflict
}
