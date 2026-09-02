namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Records the one authoritative disposition of the exact current assessment.</summary>
/// <param name="SchemaVersion">The disposition schema, which must be 1.</param>
/// <param name="CaseId">The exact reconciliation case identity.</param>
/// <param name="BindingHash">The exact reconciliation binding hash.</param>
/// <param name="DispositionId">The stable disposition identity.</param>
/// <param name="Kind">The closed disposition.</param>
/// <param name="AssessmentHash">The exact content hash of the current assessment.</param>
/// <param name="AuthorityEvidenceHash">The exact authority receipt for accepting or quarantining the assessment.</param>
/// <param name="DisposedAtUtc">The trusted UTC disposition instant.</param>
/// <param name="SafeDetail">Optional bounded operator-safe context; it never supplies proof.</param>
/// <param name="ContentHash">The canonical hash of this disposition except this field.</param>
public sealed record GovernedLoopEffectReconciliationDisposition(
    int SchemaVersion,
    string CaseId,
    string BindingHash,
    string DispositionId,
    GovernedLoopEffectReconciliationDispositionKind Kind,
    string AssessmentHash,
    string AuthorityEvidenceHash,
    DateTimeOffset DisposedAtUtc,
    string? SafeDetail,
    string ContentHash);
