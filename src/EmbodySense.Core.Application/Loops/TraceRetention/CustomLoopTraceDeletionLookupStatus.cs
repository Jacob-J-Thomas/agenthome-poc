namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionLookupStatus
{
    Unknown = 0,
    NotFound = 1,
    PendingMutation = 2,
    OutcomeCommitted = 3
}
