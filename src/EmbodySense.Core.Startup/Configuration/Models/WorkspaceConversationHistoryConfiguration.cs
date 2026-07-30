namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceConversationHistoryConfiguration(
    string DirectoryPath,
    string CurrentPath,
    string ArchivePath,
    bool DirectoryExists,
    IReadOnlyList<WorkspaceConversationTranscript> Transcripts,
    IReadOnlyList<string> ReadProblems);
