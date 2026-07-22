namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionOperationState
{
    // Persisted zero sentinel: strict readers reject default-initialized or unknown operation state instead of treating it as a valid transition.
    Unknown = 0,
    PendingMutation = 1,
    OutcomeCommitted = 2
}
