namespace EmbodySense.Core.Startup.Configuration.Models;

/// <summary>
/// Summarizes one active or archived transcript from the coordinated history snapshot.
/// </summary>
/// <param name="ConversationId">The persistence-derived logical conversation identifier.</param>
/// <param name="Path">The transcript source path.</param>
/// <param name="Exists">Whether the coordinated snapshot observed the source file.</param>
/// <param name="IsCurrent">Whether this is the active transcript rather than an archived transcript.</param>
/// <param name="MessageCount">The number of messages successfully parsed within the bounded source snapshot.</param>
/// <param name="FirstTimestampUtc">The earliest returned message timestamp, or null when no messages were returned.</param>
/// <param name="LastTimestampUtc">The latest returned message timestamp, or null when no messages were returned.</param>
/// <param name="FirstPrompt">The first returned user message, or an empty string when none was returned.</param>
/// <param name="Messages">Returned messages ordered by sequence and then timestamp.</param>
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
