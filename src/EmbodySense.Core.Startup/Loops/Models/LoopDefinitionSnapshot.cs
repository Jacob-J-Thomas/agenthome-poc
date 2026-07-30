namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Provides the complete server-owned projection of a system or custom loop definition.
/// </summary>
/// <remarks>
/// Custom-loop metadata is loaded from the durable definition. The synthesized system-default projection instead uses definition
/// version 1, empty content-hash and mutation-operation values, and <see cref="DateTimeOffset.MinValue"/> timestamps. Those system
/// sentinels are presentation placeholders, not persisted integrity, concurrency, or chronology evidence.
/// </remarks>
/// <param name="SchemaVersion">The definition schema version; the system projection copies its authority definition's schema.</param>
/// <param name="Id">The stable loop identity.</param>
/// <param name="DefinitionVersion">The custom definition's optimistic-concurrency version, or the system projection's fixed placeholder value.</param>
/// <param name="ContentHash">The custom definition's canonical content hash, or an empty system placeholder.</param>
/// <param name="CreatedAtUtc">The custom definition's durable creation timestamp, or the system placeholder timestamp.</param>
/// <param name="UpdatedAtUtc">The custom definition's durable last-update timestamp, or the system placeholder timestamp.</param>
/// <param name="DisplayName">The user-visible loop name.</param>
/// <param name="Description">The user-visible loop purpose.</param>
/// <param name="RoleId">The server-owned contextual role identity.</param>
/// <param name="TriggerPolicy">The persisted trigger input policy.</param>
/// <param name="ContextDefaults">The inherited inference and exit context policies.</param>
/// <param name="InferenceSteps">The ordered inference graph body.</param>
/// <param name="ToolAssignments">The immutable maximum requested for future admissions.</param>
/// <param name="ExitPolicy">The continuation ceiling, decision instruction, and exit context policy.</param>
/// <param name="LastMutationOperationId">The custom definition's last committed mutation identity, or an empty system placeholder.</param>
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
