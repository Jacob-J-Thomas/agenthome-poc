using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed record CustomLoopDefinitionMutationOperationRecord(
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
    bool OutcomeAuditRecorded,
    CustomLoopDefinition? OriginalDefinition,
    DateTimeOffset RecordedAtUtc)
{
    public CustomLoopDefinitionMutationOperation ToPublic()
    {
        return new CustomLoopDefinitionMutationOperation(
            SchemaVersion,
            Kind,
            OperationId,
            RequestHash,
            LoopId,
            RoleId,
            ExpectedDefinitionVersion,
            PlannedDefinition,
            PriorDefinition,
            RequestedAtUtc,
            UpdatedAtUtc,
            State,
            Outcome,
            ResultDefinition,
            ResultConflict,
            ResultTombstone,
            OutcomeAuditRecorded);
    }
}
