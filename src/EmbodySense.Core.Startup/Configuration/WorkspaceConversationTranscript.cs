namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConversationTranscript(
    string ConversationId,
    string Path,
    bool Exists,
    bool IsCurrent,
    int MessageCount,
    DateTimeOffset? FirstTimestampUtc,
    DateTimeOffset? LastTimestampUtc,
    string FirstPrompt,
    IReadOnlyList<WorkspaceConversationMessage> Messages);
