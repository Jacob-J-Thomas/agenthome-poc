namespace EmbodySense.Core.Application.Runtime.Commands;

public enum RuntimeCommandId
{
    Unknown = 0,
    Help,
    VerboseStatus,
    VerboseEnable,
    VerboseDisable,
    Exit,
    NewSession,
    ConversationHistory,
    CancelPendingInput
}
