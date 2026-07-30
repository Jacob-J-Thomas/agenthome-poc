namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Projects the durable identity, lifecycle cursor, terminal state, and deletion state needed for run discovery.
/// </summary>
/// <param name="Id">The stable artifact identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="AdmissionOperationId">The idempotency identity of the admission operation.</param>
/// <param name="DefinitionVersion">The monotonically increasing definition version.</param>
/// <param name="LifecycleVersion">The monotonically increasing lifecycle version.</param>
/// <param name="Status">The status.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="CompletedAtUtc">The UTC terminal time, or <see langword="null"/> while nonterminal.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="NextStepIndex">The next step index.</param>
/// <param name="FailureCode">The stable terminal failure code, or <see langword="null"/> for non-failed runs.</param>
/// <param name="IsDeleted">Whether only a durable deletion tombstone remains.</param>
public sealed record CustomLoopRunSummary(
    string Id,
    string LoopId,
    string AdmissionOperationId,
    int DefinitionVersion,
    int LifecycleVersion,
    CustomLoopRunStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Iteration,
    int NextStepIndex,
    string? FailureCode,
    bool IsDeleted);
