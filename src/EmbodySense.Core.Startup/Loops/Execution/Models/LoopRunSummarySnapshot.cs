namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunSummarySnapshot(
    string Id,
    string LoopId,
    string AdmissionOperationId,
    int DefinitionVersion,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Iteration,
    int NextStepIndex,
    string? FailureCode,
    bool IsDeleted);
