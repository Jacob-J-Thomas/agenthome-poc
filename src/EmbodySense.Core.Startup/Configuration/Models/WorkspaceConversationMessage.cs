namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides a bounded, user-visible conversation message from persisted history.
/// </summary>
/// <param name="Sequence">The persisted message sequence within the logical conversation.</param>
/// <param name="TimestampUtc">The persisted message timestamp.</param>
/// <param name="Role">The persisted conversation role.</param>
/// <param name="Content">Message content truncated to the configuration snapshot limit when necessary.</param>
public sealed record WorkspaceConversationMessage(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Role,
    string Content);
