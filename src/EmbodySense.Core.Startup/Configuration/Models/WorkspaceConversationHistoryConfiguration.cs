namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Provides the coordinated and bounded conversation-history portion of a configuration snapshot.
/// </summary>
/// <param name="DirectoryPath">The canonical conversation-history directory.</param>
/// <param name="CurrentPath">The canonical active transcript path.</param>
/// <param name="ArchivePath">The canonical archived-transcript directory.</param>
/// <param name="DirectoryExists">Whether the conversation-history directory existence probe reported true.</param>
/// <param name="Transcripts">Bounded transcript projections returned by the coordinated persistence snapshot.</param>
/// <param name="ReadProblems">Bounded parse, truncation, and omission diagnostics.</param>
public sealed record WorkspaceConversationHistoryConfiguration(
    string DirectoryPath,
    string CurrentPath,
    string ArchivePath,
    bool DirectoryExists,
    IReadOnlyList<WorkspaceConversationTranscript> Transcripts,
    IReadOnlyList<string> ReadProblems);
