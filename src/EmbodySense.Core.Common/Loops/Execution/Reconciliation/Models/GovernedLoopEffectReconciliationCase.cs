namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Retains one immutable value-free case over exact registered sources, observations, assessments, disposition, and optional resolution.</summary>
/// <param name="SchemaVersion">The case schema, which must be 1.</param>
/// <param name="CaseId">The stable reconciliation case identity.</param>
/// <param name="CaseVersion">The positive contiguous case version.</param>
/// <param name="Binding">The exact workspace, execution visit, effect intent, and current-attempt version.</param>
/// <param name="ContractMetadata">The exact versioned actuator reconciliation and probe contract.</param>
/// <param name="EvidenceSources">The canonically ordered registered evidence-source versions.</param>
/// <param name="ObservationHistory">The canonically ordered immutable observation history.</param>
/// <param name="AssessmentHistory">The canonically ordered immutable assessment history.</param>
/// <param name="CurrentAssessmentHash">The optional exact current assessment hash, present with a disposition.</param>
/// <param name="Disposition">The optional single disposition, present with a current assessment.</param>
/// <param name="Resolution">The optional accepted resolution; unresolved dispositions must omit it.</param>
/// <param name="CaseReceiptHashes">Canonically ordered value-free receipt hashes associated with the case.</param>
/// <param name="PreviousContentHash">The exact preceding case-version hash; absent only at version 1.</param>
/// <param name="OpenedAtUtc">The trusted UTC boundary after which outcome observations are fresh.</param>
/// <param name="UpdatedAtUtc">The trusted UTC instant at which this complete case version was recorded.</param>
/// <param name="ContentHash">The canonical hash of this case except this field.</param>
public sealed record GovernedLoopEffectReconciliationCase(
    int SchemaVersion,
    string CaseId,
    long CaseVersion,
    GovernedLoopEffectReconciliationBinding Binding,
    GovernedLoopEffectReconciliationContractMetadata ContractMetadata,
    IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource> EvidenceSources,
    IReadOnlyList<GovernedLoopEffectReconciliationObservation> ObservationHistory,
    IReadOnlyList<GovernedLoopEffectReconciliationAssessment> AssessmentHistory,
    string? CurrentAssessmentHash,
    GovernedLoopEffectReconciliationDisposition? Disposition,
    GovernedLoopEffectReconciliationResolution? Resolution,
    IReadOnlyList<string> CaseReceiptHashes,
    string? PreviousContentHash,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string ContentHash)
{
    /// <summary>Gets a defensive copy of the exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationContractCopy.Copy(Binding);

    /// <summary>Gets a defensive copy of the pinned actuator reconciliation contract metadata.</summary>
    public GovernedLoopEffectReconciliationContractMetadata ContractMetadata { get; } = GovernedLoopEffectReconciliationContractCopy.Copy(ContractMetadata);

    /// <summary>Gets a bounded defensive snapshot of source registrations.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource> EvidenceSources { get; } = GovernedLoopEffectReconciliationContractCopy.CopySources(EvidenceSources);

    /// <summary>Gets a bounded defensive snapshot of immutable observations.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationObservation> ObservationHistory { get; } = GovernedLoopEffectReconciliationContractCopy.CopyObservations(ObservationHistory);

    /// <summary>Gets a bounded defensive snapshot of immutable assessments.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationAssessment> AssessmentHistory { get; } = GovernedLoopEffectReconciliationContractCopy.CopyAssessments(AssessmentHistory);

    /// <summary>Gets a defensive copy of the single authoritative disposition.</summary>
    public GovernedLoopEffectReconciliationDisposition? Disposition { get; } = GovernedLoopEffectReconciliationContractCopy.Copy(Disposition);

    /// <summary>Gets a defensive copy of the optional accepted resolution.</summary>
    public GovernedLoopEffectReconciliationResolution? Resolution { get; } = GovernedLoopEffectReconciliationContractCopy.Copy(Resolution);

    /// <summary>Gets a bounded defensive snapshot of case receipt hashes.</summary>
    public IReadOnlyList<string> CaseReceiptHashes { get; } = GovernedLoopEffectReconciliationContractCopy.CopyHashes(CaseReceiptHashes, GovernedLoopEffectReconciliationContractLimits.MaxCaseReceipts);

}
