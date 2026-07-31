namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Identifies the supported agent runtime turn status values.
/// </summary>
public enum AgentRuntimeTurnStatus
{
    /// <summary>
    /// No concrete terminal status has been selected.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// A runtime command handled the input without a model turn.
    /// </summary>
    CommandHandled,
    /// <summary>
    /// A model turn completed with accepted assistant output.
    /// </summary>
    MessageCompleted,
    /// <summary>
    /// A model turn ended in failure.
    /// </summary>
    MessageFailed,
    /// <summary>
    /// A model turn ended through cancellation.
    /// </summary>
    MessageCancelled,
    /// <summary>
    /// A runtime command requested interface shutdown.
    /// </summary>
    ExitRequested
}
