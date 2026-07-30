using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Represents a custom loop definition mutation operation record.
/// </summary>
/// <param name="SchemaVersion">The schema version.</param>
/// <param name="Kind">The kind.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="LoopId">The loop ID.</param>
/// <param name="RoleId">The role ID.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="PlannedDefinition">The planned definition.</param>
/// <param name="PriorDefinition">The prior definition.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
/// <param name="UpdatedAtUtc">The updated at UTC.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="ResultDefinition">The result definition.</param>
/// <param name="ResultConflict">The result conflict.</param>
/// <param name="ResultTombstone">The result tombstone.</param>
/// <param name="OutcomeAuditRecorded">The outcome audit recorded.</param>
/// <param name="OriginalDefinition">The original definition.</param>
/// <param name="RecordedAtUtc">The recorded at UTC.</param>
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
    /// <summary>
    /// Converts the supplied value to public.
    /// </summary>
    /// <returns>The custom loop definition mutation operation.</returns>
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
