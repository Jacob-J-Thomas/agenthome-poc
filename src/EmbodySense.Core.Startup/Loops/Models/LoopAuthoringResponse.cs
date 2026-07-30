namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopAuthoringResponse(
    string Status,
    bool IsCommitted,
    LoopDefinitionSnapshot? Definition,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    LoopDefinitionConflict? Conflict,
    string? Detail);
