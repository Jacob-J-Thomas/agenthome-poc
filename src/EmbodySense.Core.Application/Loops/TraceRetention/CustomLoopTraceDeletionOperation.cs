namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    CustomLoopTraceDeletionRequest Request,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopTraceDeletionOperationState State,
    CustomLoopTraceDeletionStoreStatus Outcome,
    CustomLoopTraceTombstone? Tombstone,
    CustomLoopTraceDeletionIntegrity Integrity)
{
    public const int CurrentSchemaVersion = 1;

    public CustomLoopTraceDeletionStoreResult ToStoreResult()
    {
        if (State != CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A pending trace-deletion operation has no replayable store result.");
        }

        return new CustomLoopTraceDeletionStoreResult(Outcome, Tombstone, Integrity);
    }
}
