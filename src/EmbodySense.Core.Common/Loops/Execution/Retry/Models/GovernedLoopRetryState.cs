namespace EmbodySense.Core.Common.Loops.Execution.Retry.Models;

/// <summary>Retains one immutable append-only retry-series state version.</summary>
/// <param name="SchemaVersion">The state schema version, which must be 1.</param>
/// <param name="Identity">The exact immutable retry-series binding.</param>
/// <param name="StateVersion">The positive contiguous state version.</param>
/// <param name="Disposition">The closed durable retry posture.</param>
/// <param name="CurrentAttempt">The positive attempt that most recently produced retained evidence.</param>
/// <param name="CurrentAttemptOperationId">The exact operation identity of <paramref name="CurrentAttempt"/>.</param>
/// <param name="NextAttempt">The optional exact next attempt ordinal.</param>
/// <param name="AttemptOperationId">The optional distinct idempotency identity reserved for <paramref name="NextAttempt"/>.</param>
/// <param name="Budget">The monotonic consumed or reserved resource totals.</param>
/// <param name="NextRetryAtUtc">The optional exact timestamp wake eligibility boundary.</param>
/// <param name="WakeCheckpointId">The optional exact durable sleep-checkpoint identity.</param>
/// <param name="WakeCheckpointHash">The optional exact durable sleep-checkpoint digest.</param>
/// <param name="FailureEvidenceId">The latest exact classified failure identity.</param>
/// <param name="FailureEvidenceHash">The latest exact classified failure digest.</param>
/// <param name="RecordedAtUtc">The trusted UTC time of this state version.</param>
/// <param name="ContentHash">The canonical lowercase SHA-256 digest over every preceding field.</param>
public sealed record GovernedLoopRetryState(
    int SchemaVersion,
    GovernedLoopRetrySeriesIdentity Identity,
    long StateVersion,
    GovernedLoopRetryStateDisposition Disposition,
    int CurrentAttempt,
    string CurrentAttemptOperationId,
    int? NextAttempt,
    string? AttemptOperationId,
    GovernedLoopRetryBudgetSnapshot Budget,
    DateTimeOffset? NextRetryAtUtc,
    string? WakeCheckpointId,
    string? WakeCheckpointHash,
    string FailureEvidenceId,
    string FailureEvidenceHash,
    DateTimeOffset RecordedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental state schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
