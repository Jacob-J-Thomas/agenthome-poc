namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Reports the durable outcome of an idempotent loop authoring mutation.
/// </summary>
/// <param name="Status">The canonical authoring status name.</param>
/// <param name="IsCommitted">Whether the requested definition mutation durably committed.</param>
/// <param name="Definition">The resulting or current definition when the outcome provides one.</param>
/// <param name="ValidationErrors">Structured validation failures; empty when validation succeeded.</param>
/// <param name="Conflict">Optimistic-concurrency evidence when the expected version was stale.</param>
/// <param name="Detail">Optional audit or outcome detail suitable for interface display.</param>
public sealed record LoopAuthoringResponse(
    string Status,
    bool IsCommitted,
    LoopDefinitionSnapshot? Definition,
    IReadOnlyList<LoopValidationError> ValidationErrors,
    LoopDefinitionConflict? Conflict,
    string? Detail);
