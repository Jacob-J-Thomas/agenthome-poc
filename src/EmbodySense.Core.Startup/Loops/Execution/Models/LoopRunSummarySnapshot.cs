namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Provides lightweight lifecycle and execution-position evidence for one retained run.
/// </summary>
/// <param name="Id">The value identifier.</param>
/// <param name="LoopId">The loop identifier.</param>
/// <param name="AdmissionOperationId">The admission operation identifier.</param>
/// <param name="DefinitionVersion">The definition version.</param>
/// <param name="LifecycleVersion">The lifecycle version.</param>
/// <param name="Status">The status.</param>
/// <param name="CreatedAtUtc">The created at utc.</param>
/// <param name="UpdatedAtUtc">The updated at utc.</param>
/// <param name="CompletedAtUtc">The completed at utc.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="NextStepIndex">The next step index.</param>
/// <param name="FailureCode">The failure code.</param>
/// <param name="IsDeleted">The is deleted.</param>
public sealed record LoopRunSummarySnapshot(
    string Id,
    string LoopId,
    string AdmissionOperationId,
    int DefinitionVersion,
    int LifecycleVersion,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Iteration,
    int NextStepIndex,
    string? FailureCode,
    bool IsDeleted);
