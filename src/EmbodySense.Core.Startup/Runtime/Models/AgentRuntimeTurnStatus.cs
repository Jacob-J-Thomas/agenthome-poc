namespace EmbodySense.Core.Startup.Runtime.Models;

public enum AgentRuntimeTurnStatus
{
    Unknown = 0,
    CommandHandled,
    MessageCompleted,
    MessageFailed,
    MessageCancelled,
    ExitRequested
}
