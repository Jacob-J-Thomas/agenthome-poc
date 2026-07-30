using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Represents a custom loop definition mutation operation.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="Kind">The kind.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="PlannedDefinition">The planned definition.</param>
/// <param name="PriorDefinition">The prior definition.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="ResultDefinition">The result definition.</param>
/// <param name="ResultConflict">The result conflict.</param>
/// <param name="ResultTombstone">The result tombstone.</param>
/// <param name="OutcomeAuditRecorded">The outcome audit recorded.</param>
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
    /// <summary>
    /// Identifies the current schema version custom loop definition mutation operation.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Gets a value indicating whether the value has applied mutation artifact.
    /// </summary>
    /// <value><see langword="true"/> when the value has applied mutation artifact; otherwise, <see langword="false"/>.</value>
    public bool HasAppliedMutationArtifact { get; init; }

    /// <summary>
    /// Gets the custom loop operation integrity.
    /// </summary>
    /// <value>The custom loop operation integrity.</value>
    public CustomLoopOperationIntegrity Integrity => State == CustomLoopDefinitionMutationState.PendingMutation
        ? CustomLoopOperationIntegrity.PendingMutation
        : OutcomeAuditRecorded ? CustomLoopOperationIntegrity.Complete : CustomLoopOperationIntegrity.PendingOutcomeAudit;

    /// <summary>
    /// Converts the supplied value to store result.
    /// </summary>
    /// <returns>The custom loop definition store result.</returns>
    public CustomLoopDefinitionStoreResult ToStoreResult()
    {
        if (State != CustomLoopDefinitionMutationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A pending mutation operation has no replayable store result.");
        }

        return new CustomLoopDefinitionStoreResult(Outcome, ResultDefinition, ResultConflict, ResultTombstone, Integrity);
    }
}
