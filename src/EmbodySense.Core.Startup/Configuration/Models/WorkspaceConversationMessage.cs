namespace EmbodySense.Core.Startup.Configuration.Models;

public sealed record WorkspaceConversationMessage(
    int Sequence,
    DateTimeOffset TimestampUtc,
    string Role,
    string Content);
