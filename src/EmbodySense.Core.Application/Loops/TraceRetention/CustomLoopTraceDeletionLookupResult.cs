namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionLookupResult(CustomLoopTraceDeletionLookupStatus Status, CustomLoopTraceDeletionOperation? Operation)
{
    public static CustomLoopTraceDeletionLookupResult NotFound() => new(CustomLoopTraceDeletionLookupStatus.NotFound, null);

    public static CustomLoopTraceDeletionLookupResult Found(CustomLoopTraceDeletionOperation operation)
    {
        var status = operation.State == CustomLoopTraceDeletionOperationState.PendingMutation
            ? CustomLoopTraceDeletionLookupStatus.PendingMutation
            : CustomLoopTraceDeletionLookupStatus.OutcomeCommitted;
        return new CustomLoopTraceDeletionLookupResult(status, operation);
    }
}
