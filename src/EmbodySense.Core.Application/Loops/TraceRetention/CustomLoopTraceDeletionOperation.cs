using EmbodySense.Core.Application.Loops.TraceRetention.Models;
namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Represents a custom loop trace deletion operation.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="Request">The request.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Tombstone">The tombstone.</param>
/// <param name="Integrity">The integrity.</param>
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
    /// <summary>
    /// Identifies the current schema version custom loop trace deletion operation.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Converts the supplied value to store result.
    /// </summary>
    /// <returns>The custom loop trace deletion store result.</returns>
    public CustomLoopTraceDeletionStoreResult ToStoreResult()
    {
        if (State != CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A pending trace-deletion operation has no replayable store result.");
        }

        return new CustomLoopTraceDeletionStoreResult(Outcome, Tombstone, Integrity);
    }
}
