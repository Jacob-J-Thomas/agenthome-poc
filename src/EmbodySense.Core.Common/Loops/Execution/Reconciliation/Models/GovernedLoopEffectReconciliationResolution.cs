using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Records the optional accepted resolution that can authorize one typed reconciled successor.</summary>
/// <param name="SchemaVersion">The resolution schema, which must be 1.</param>
/// <param name="CaseId">The exact reconciliation case identity.</param>
/// <param name="BindingHash">The exact reconciliation binding hash.</param>
/// <param name="ResolutionId">The stable reconciliation-evidence identity retained on the successor.</param>
/// <param name="AssessmentHash">The exact accepted current assessment hash.</param>
/// <param name="DispositionHash">The exact accepted disposition hash.</param>
/// <param name="Outcome">The typed successor outcome.</param>
/// <param name="OutcomeEvidenceId">The exact outcome evidence identity for applied effects; absent for proved not-applied.</param>
/// <param name="OutcomeEvidenceHash">The exact outcome evidence hash for applied effects; absent for proved not-applied.</param>
/// <param name="AuthorityEvidenceHash">The exact authority receipt for resolving the quarantined attempt.</param>
/// <param name="ResolvedAtUtc">The trusted UTC resolution instant and successor update time.</param>
/// <param name="SafeDetail">Optional bounded operator-safe context; it never supplies proof.</param>
/// <param name="ContentHash">The canonical hash of this resolution except this field.</param>
public sealed record GovernedLoopEffectReconciliationResolution(
    int SchemaVersion,
    string CaseId,
    string BindingHash,
    string ResolutionId,
    string AssessmentHash,
    string DispositionHash,
    GovernedLoopEffectOutcome Outcome,
    string? OutcomeEvidenceId,
    string? OutcomeEvidenceHash,
    string AuthorityEvidenceHash,
    DateTimeOffset ResolvedAtUtc,
    string? SafeDetail,
    string ContentHash);
