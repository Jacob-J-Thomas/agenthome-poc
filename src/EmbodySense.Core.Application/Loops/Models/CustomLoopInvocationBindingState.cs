namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop invocation binding state values.
/// </summary>
public enum CustomLoopInvocationBindingState
{
    /// <summary>
    /// Identifies the unknown custom loop invocation binding state.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the unbound custom loop invocation binding state.
    /// </summary>
    Unbound = 1,
    /// <summary>
    /// Identifies the conversation not found custom loop invocation binding state.
    /// </summary>
    ConversationNotFound = 2,
    /// <summary>
    /// Identifies the conversation workspace execution busy custom loop invocation binding state.
    /// </summary>
    ConversationWorkspaceExecutionBusy = 3,
    /// <summary>
    /// Identifies the conversation invalid custom loop invocation binding state.
    /// </summary>
    ConversationInvalid = 4,
    /// <summary>
    /// Identifies the captured context custom loop invocation binding state.
    /// </summary>
    CapturedContext = 5,
    /// <summary>
    /// Identifies the captured context not found custom loop invocation binding state.
    /// </summary>
    CapturedContextNotFound = 6
}
