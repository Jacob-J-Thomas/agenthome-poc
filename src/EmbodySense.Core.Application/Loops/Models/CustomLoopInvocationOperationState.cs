namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the supported custom loop invocation operation state values.
/// </summary>
public enum CustomLoopInvocationOperationState
{
    /// <summary>
    /// Identifies the unknown custom loop invocation operation state.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pending custom loop invocation operation state.
    /// </summary>
    Pending = 1,
    /// <summary>
    /// Identifies the complete custom loop invocation operation state.
    /// </summary>
    Complete = 2
}
