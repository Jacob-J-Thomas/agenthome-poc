namespace EmbodySense.Core.Application.Runtime.Models;

public enum RuntimeContextSource
{
    Unknown = 0,
    StartupContext,
    RestoredConversationHistory,
    SessionTranscript,
    CurrentTurnInput
}
