namespace EmbodySense.Core.Persistence.Memory.Models;

/// <summary>
/// Captures a detached, lease-coordinated view of the persisted conversation transcript files.
/// </summary>
/// <param name="Transcripts">The bounded transcript-file snapshots in current, saved, then newest-archive order.</param>
/// <param name="AdditionalFilesOmitted">Whether the configured file bound excluded one or more persisted transcripts.</param>
public sealed record ConversationHistorySnapshot(
    IReadOnlyList<ConversationTranscriptFileSnapshot> Transcripts,
    bool AdditionalFilesOmitted);
