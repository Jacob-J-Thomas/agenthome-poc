namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

public enum CustomLoopTraceDeletionLookupStatus
{
    NotFound = 1,
    PendingMutation = 2,
    OutcomeCommitted = 3
}
