namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one exact value-free durable retry-series posture for shared interfaces.</summary>
public sealed record LoopRunRetryStateSnapshot(
    string SeriesId,
    string PolicyId,
    string PolicyHash,
    string NodeId,
    int ActivationOrdinal,
    int VisitOrdinal,
    long StateVersion,
    string Disposition,
    int CurrentAttempt,
    string CurrentAttemptOperationId,
    int? NextAttempt,
    string? AttemptOperationId,
    LoopRunRetryBudgetSnapshot Budget,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset? NextRetryAtUtc,
    string? WakeCheckpointId,
    string? WakeCheckpointHash,
    string FailureEvidenceId,
    string FailureEvidenceHash,
    DateTimeOffset RecordedAtUtc,
    string ContentHash);
