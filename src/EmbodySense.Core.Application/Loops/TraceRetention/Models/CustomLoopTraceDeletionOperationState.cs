namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Identifies the supported custom loop trace deletion operation state values.
/// </summary>
public enum CustomLoopTraceDeletionOperationState
{
    // Persisted zero sentinel: strict readers reject default-initialized or unknown operation state instead of treating it as a valid transition.
    /// <summary>
    /// Identifies the unknown custom loop trace deletion operation state.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the pending mutation custom loop trace deletion operation state.
    /// </summary>
    PendingMutation = 1,
    /// <summary>
    /// Identifies the outcome committed custom loop trace deletion operation state.
    /// </summary>
    OutcomeCommitted = 2
}
