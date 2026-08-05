namespace EmbodySense.Core.Application.Memory;

/// <summary>
/// Reports a superseded experimental transcript shape that requires explicit owner cleanup.
/// </summary>
public sealed class ConversationTranscriptCleanupRequiredException : FormatException
{
    /// <summary>
    /// Initializes an actionable cleanup boundary for one unsupported transcript file.
    /// </summary>
    /// <param name="transcriptPath">The exact transcript file that must be backed up and removed.</param>
    public ConversationTranscriptCleanupRequiredException(string transcriptPath)
        : base($"Conversation transcript `{transcriptPath}` uses the superseded pre-release shape without stable message and publication identities. Automatic migration and compatibility reads are intentionally unsupported. Back up and remove this transcript file, then start EmbodySense again to create a fresh version-1 transcript.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        TranscriptPath = transcriptPath;
    }

    /// <summary>Gets the exact transcript file requiring explicit cleanup.</summary>
    public string TranscriptPath { get; }
}
