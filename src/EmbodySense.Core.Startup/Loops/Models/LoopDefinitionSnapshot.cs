namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Provides the complete server-owned projection of a custom loop definition.
/// </summary>
/// <param name="SchemaVersion">The custom definition schema version.</param>
/// <param name="Id">The stable loop identity.</param>
/// <param name="DefinitionVersion">The custom definition's optimistic-concurrency version.</param>
/// <param name="ContentHash">The custom definition's canonical content hash.</param>
/// <param name="CreatedAtUtc">The custom definition's durable creation timestamp.</param>
/// <param name="UpdatedAtUtc">The custom definition's durable last-update timestamp.</param>
/// <param name="DisplayName">The user-visible loop name.</param>
/// <param name="Description">The user-visible loop purpose.</param>
/// <param name="RoleId">The server-owned contextual role identity.</param>
/// <param name="TriggerPolicy">The persisted trigger input policy.</param>
/// <param name="ContextDefaults">The inherited inference and exit context policies.</param>
/// <param name="InferenceSteps">The ordered inference graph body.</param>
/// <param name="ToolAssignments">The immutable maximum requested for future admissions.</param>
/// <param name="ExitPolicy">The continuation ceiling, decision instruction, and exit context policy.</param>
/// <param name="LastMutationOperationId">The custom definition's last committed mutation identity.</param>
public sealed record LoopDefinitionSnapshot(
    int SchemaVersion,
    string Id,
    int DefinitionVersion,
    string ContentHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string DisplayName,
    string Description,
    string RoleId,
    LoopTriggerPolicy TriggerPolicy,
    LoopContextDefaults ContextDefaults,
    IReadOnlyList<LoopInferenceStep> InferenceSteps,
    IReadOnlyList<LoopToolAssignment> ToolAssignments,
    LoopExitPolicy ExitPolicy,
    string LastMutationOperationId);
