namespace EmbodySense.Core.Persistence.Memory.Models;

/// <summary>
/// Captures one transcript file while conversation append and rotation are excluded by the persistence lease.
/// </summary>
/// <param name="ConversationId">The configuration-facing current, saved, or archive transcript identity.</param>
/// <param name="Path">The absolute persisted transcript path.</param>
/// <param name="IsCurrent">Whether the snapshot represents the active conversation path.</param>
/// <param name="Exists">Whether the transcript existed at the coordinated snapshot point.</param>
/// <param name="Lines">The complete detached line snapshot when the transcript existed.</param>
public sealed record ConversationTranscriptFileSnapshot(
    string ConversationId,
    string Path,
    bool IsCurrent,
    bool Exists,
    IReadOnlyList<string> Lines);
