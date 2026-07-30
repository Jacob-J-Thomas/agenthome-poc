namespace EmbodySense.Core.Startup.Loops.Models;

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
