namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopAuthoringResponse(
    string Status,
    bool IsCommitted,
    LoopDefinitionSnapshot? Definition,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    LoopDefinitionConflict? Conflict,
    string? Detail);
