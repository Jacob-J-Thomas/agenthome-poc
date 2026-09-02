namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Records one immutable authoritative assessment over exact observation-version hashes.</summary>
/// <param name="SchemaVersion">The assessment schema, which must be 1.</param>
/// <param name="CaseId">The exact reconciliation case identity.</param>
/// <param name="BindingHash">The exact reconciliation binding hash.</param>
/// <param name="AssessmentId">The stable assessment identity.</param>
/// <param name="Kind">The closed assessment conclusion.</param>
/// <param name="ObservationHashes">The canonically ordered exact observation content hashes considered.</param>
/// <param name="AuthorityEvidenceHash">The exact authority receipt for this assessment.</param>
/// <param name="AssessedAtUtc">The trusted UTC assessment instant.</param>
/// <param name="SafeDetail">Optional bounded operator-safe context; it never proves an outcome.</param>
/// <param name="ContentHash">The canonical hash of this assessment except this field.</param>
public sealed record GovernedLoopEffectReconciliationAssessment(
    int SchemaVersion,
    string CaseId,
    string BindingHash,
    string AssessmentId,
    GovernedLoopEffectReconciliationAssessmentKind Kind,
    IReadOnlyList<string> ObservationHashes,
    string AuthorityEvidenceHash,
    DateTimeOffset AssessedAtUtc,
    string? SafeDetail,
    string ContentHash)
{
    /// <summary>Gets a bounded defensive snapshot of exact observation hashes.</summary>
    public IReadOnlyList<string> ObservationHashes { get; } = ObservationHashes is null
        ? null!
        : Array.AsReadOnly(ObservationHashes.Take(GovernedLoopEffectReconciliationContractLimits.MaxObservationReferences + 1).ToArray());
}
