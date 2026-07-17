using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Authoring;

public sealed record CustomLoopInferenceStepInput(
    string? Id,
    string Name,
    string Instruction,
    CustomLoopNodeContextPolicy ContextPolicy);

public sealed record CustomLoopDefinitionInput(
    string DisplayName,
    string Description,
    CustomLoopTriggerPolicy TriggerPolicy,
    CustomLoopInferenceStepInput[] InferenceSteps,
    CustomLoopToolAssignment[] ToolAssignments,
    CustomLoopExitPolicy ExitPolicy);

public enum CustomLoopAuthoringStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    Deleted = 3,
    Replayed = 4,
    Invalid = 5,
    Conflict = 6,
    NotFound = 7,
    LimitExceeded = 8,
    AuditUnavailable = 9,
    CommittedWithAuditWarning = 10,
    ActiveRunExists = 11
}

public sealed record CustomLoopAuthoringResult(
    CustomLoopAuthoringStatus Status,
    CustomLoopDefinition? Definition,
    IReadOnlyList<CustomLoopValidationError> ValidationErrors,
    CustomLoopDefinitionConflict? Conflict,
    string? Detail)
{
    public bool IsCommitted => Status is CustomLoopAuthoringStatus.Created or CustomLoopAuthoringStatus.Updated or CustomLoopAuthoringStatus.Deleted or CustomLoopAuthoringStatus.Replayed or CustomLoopAuthoringStatus.CommittedWithAuditWarning;

    public static CustomLoopAuthoringResult Invalid(IReadOnlyList<CustomLoopValidationError> errors) => new(CustomLoopAuthoringStatus.Invalid, null, errors, null, "The loop definition is invalid.");

    public static CustomLoopAuthoringResult AuditUnavailable() => new(CustomLoopAuthoringStatus.AuditUnavailable, null, [], null, "The mutation was not attempted because its audit intent could not be recorded.");

    public static CustomLoopAuthoringResult ActiveRun(CustomLoopDefinition? definition) => new(CustomLoopAuthoringStatus.ActiveRunExists, definition, [], null, "Finish or cancel the loop's nonterminal run before editing or deleting its definition.");
}
