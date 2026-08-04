namespace EmbodySense.Core.Application.Runtime.Commands.Models;

/// <summary>
/// Identifies the supported runtime command ID values.
/// </summary>
public enum RuntimeCommandId
{
    /// <summary>
    /// Identifies the unknown runtime command ID.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the help runtime command ID.
    /// </summary>
    Help,
    /// <summary>
    /// Identifies the verbose status runtime command ID.
    /// </summary>
    VerboseStatus,
    /// <summary>
    /// Identifies the verbose enable runtime command ID.
    /// </summary>
    VerboseEnable,
    /// <summary>
    /// Identifies the verbose disable runtime command ID.
    /// </summary>
    VerboseDisable,
    /// <summary>
    /// Identifies the exit runtime command ID.
    /// </summary>
    Exit,
    /// <summary>
    /// Identifies the new session runtime command ID.
    /// </summary>
    NewSession,
    /// <summary>
    /// Identifies the conversation history runtime command ID.
    /// </summary>
    ConversationHistory,
    /// <summary>
    /// Identifies default-conversation review inspection and resolution.
    /// </summary>
    DefaultConversationReview,
    /// <summary>
    /// Identifies the cancel pending input runtime command ID.
    /// </summary>
    CancelPendingInput
}
