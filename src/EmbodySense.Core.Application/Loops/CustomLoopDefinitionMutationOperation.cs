using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopDefinitionMutationOperation(
    int SchemaVersion,
    CustomLoopDefinitionMutationKind Kind,
    string OperationId,
    string RequestHash,
    string LoopId,
    string RoleId,
    int? ExpectedDefinitionVersion,
    CustomLoopDefinition? PlannedDefinition,
    CustomLoopDefinition? PriorDefinition,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopDefinitionMutationState State,
    CustomLoopDefinitionStoreStatus Outcome,
    CustomLoopDefinition? ResultDefinition,
    CustomLoopDefinitionConflict? ResultConflict,
    CustomLoopDefinitionTombstone? ResultTombstone,
    bool OutcomeAuditRecorded)
{
    public const int CurrentSchemaVersion = 1;

    public CustomLoopOperationIntegrity Integrity => State == CustomLoopDefinitionMutationState.PendingMutation
        ? CustomLoopOperationIntegrity.PendingMutation
        : OutcomeAuditRecorded ? CustomLoopOperationIntegrity.Complete : CustomLoopOperationIntegrity.PendingOutcomeAudit;

    public CustomLoopDefinitionStoreResult ToStoreResult()
    {
        if (State != CustomLoopDefinitionMutationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A pending mutation operation has no replayable store result.");
        }

        return new CustomLoopDefinitionStoreResult(Outcome, ResultDefinition, ResultConflict, ResultTombstone, Integrity);
    }
}
