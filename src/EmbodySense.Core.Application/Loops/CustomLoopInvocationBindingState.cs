namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopInvocationBindingState
{
    Unknown = 0,
    Unbound = 1,
    ConversationNotFound = 2,
    ConversationWorkspaceExecutionBusy = 3,
    ConversationInvalid = 4,
    CapturedContext = 5,
    CapturedContextNotFound = 6
}
