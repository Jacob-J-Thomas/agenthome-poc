namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Registers one value-free evidence source before its observations can inform reconciliation.</summary>
/// <param name="SchemaVersion">The source schema, which must be 1.</param>
/// <param name="CaseId">The exact reconciliation case identity.</param>
/// <param name="BindingHash">The exact reconciliation binding hash.</param>
/// <param name="SourceId">The stable source identity.</param>
/// <param name="Kind">Whether this registration is authoritative or informational.</param>
/// <param name="ReliabilityPosture">The registered reliability posture.</param>
/// <param name="ReconciliationContractId">The exact versioned reconciliation contract identity.</param>
/// <param name="ReconciliationContractVersion">The exact positive reconciliation contract version.</param>
/// <param name="ReconciliationContractHash">The exact reconciliation metadata hash.</param>
/// <param name="RegistrationEvidenceHash">The exact authority or configuration receipt that registered the source.</param>
/// <param name="RegisteredAtUtc">The trusted UTC registration boundary.</param>
/// <param name="RetiredAtUtc">The optional trusted UTC retirement boundary.</param>
/// <param name="ContentHash">The canonical hash of this registration except this field.</param>
public sealed record GovernedLoopEffectReconciliationEvidenceSource(
    int SchemaVersion,
    string CaseId,
    string BindingHash,
    string SourceId,
    GovernedLoopEffectReconciliationEvidenceSourceKind Kind,
    GovernedLoopEffectReconciliationReliabilityPosture ReliabilityPosture,
    string ReconciliationContractId,
    int ReconciliationContractVersion,
    string ReconciliationContractHash,
    string RegistrationEvidenceHash,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? RetiredAtUtc,
    string ContentHash);
