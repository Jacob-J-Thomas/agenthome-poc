namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConversationHistoryConfiguration(
    string DirectoryPath,
    string CurrentPath,
    string ArchivePath,
    bool DirectoryExists,
    IReadOnlyList<WorkspaceConversationTranscript> Transcripts,
    IReadOnlyList<string> ReadProblems);
