namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceDeletionLookupStatus
{
    NotFound = 1,
    PendingMutation = 2,
    OutcomeCommitted = 3
}
