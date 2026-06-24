namespace EmbodySense.Core.Startup.Configuration;

public sealed record WorkspaceConversationMessage(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Role,
    string Content);
