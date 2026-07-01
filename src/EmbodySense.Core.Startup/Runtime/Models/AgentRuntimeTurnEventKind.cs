namespace EmbodySense.Core.Startup.Runtime.Models;

public enum AgentRuntimeTurnEventKind
{
    Unknown = 0,
    CommandOutput,
    Prompt,
    TranscriptReplacement,
    AssistantMessage,
    Failure,
    Cancellation,
    ExitRequested
}
