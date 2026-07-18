namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopRunSummary(
    string Id,
    string LoopId,
    string AdmissionOperationId,
    int DefinitionVersion,
    CustomLoopRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Iteration,
    int NextStepIndex,
    string? FailureCode,
    bool IsDeleted);
