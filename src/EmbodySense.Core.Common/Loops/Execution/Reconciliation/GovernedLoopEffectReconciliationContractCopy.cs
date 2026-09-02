using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Creates bounded defensive copies at reconciliation contract boundaries.</summary>
public static class GovernedLoopEffectReconciliationContractCopy
{
    /// <summary>Copies an exact reconciliation binding and its execution coordinates.</summary>
    public static GovernedLoopEffectReconciliationBinding Copy(GovernedLoopEffectReconciliationBinding? value)
        => value is null
            ? null!
            : new GovernedLoopEffectReconciliationBinding(value.SchemaVersion, value.WorkspaceId, value.Execution, value.NodeId, value.ActivationOrdinal, value.VisitOrdinal, value.NodeAttempt, value.EffectId, value.OperationId, value.EffectGeneration, value.IntentHash, value.CurrentAttemptHash, value.ContentHash);

    /// <summary>Copies versioned actuator reconciliation contract metadata.</summary>
    public static GovernedLoopEffectReconciliationContractMetadata Copy(GovernedLoopEffectReconciliationContractMetadata? value)
        => value is null
            ? null!
            : new GovernedLoopEffectReconciliationContractMetadata(value.SchemaVersion, value.ContractId, value.ContractVersion, value.Capability, value.Implementation, value.ActuatorOperationId, value.OperationDescriptorHash, value.ProbeContractId, value.ProbeContractVersion, value.ProbeContractHash, value.ContentHash);

    /// <summary>Copies one exact source registration.</summary>
    public static GovernedLoopEffectReconciliationEvidenceSource Copy(GovernedLoopEffectReconciliationEvidenceSource? value)
        => value is null
            ? null!
            : new GovernedLoopEffectReconciliationEvidenceSource(value.SchemaVersion, value.CaseId, value.BindingHash, value.SourceId, value.Kind, value.ReliabilityPosture, value.ReconciliationContractId, value.ReconciliationContractVersion, value.ReconciliationContractHash, value.RegistrationEvidenceHash, value.RegisteredAtUtc, value.RetiredAtUtc, value.ContentHash);

    /// <summary>Copies one exact observation.</summary>
    public static GovernedLoopEffectReconciliationObservation Copy(GovernedLoopEffectReconciliationObservation? value)
        => value is null
            ? null!
            : new GovernedLoopEffectReconciliationObservation(value.SchemaVersion, value.CaseId, value.BindingHash, value.ObservationId, value.SourceId, value.SourceRegistrationHash, value.Kind, value.ReliabilityPosture, value.ObservedOutcome, value.EvidenceReference, value.EvidenceHash, value.ObservedAtUtc, value.RecordedAtUtc, value.SafeSummary, value.ContentHash);

    /// <summary>Copies one exact assessment and its bounded observation references.</summary>
    public static GovernedLoopEffectReconciliationAssessment Copy(GovernedLoopEffectReconciliationAssessment? value)
        => value is null
            ? null!
            : new GovernedLoopEffectReconciliationAssessment(value.SchemaVersion, value.CaseId, value.BindingHash, value.AssessmentId, value.Kind, CopyHashes(value.ObservationHashes, GovernedLoopEffectReconciliationContractLimits.MaxObservationReferences), value.AuthorityEvidenceHash, value.AssessedAtUtc, value.SafeDetail, value.ContentHash);

    /// <summary>Copies the optional single disposition.</summary>
    public static GovernedLoopEffectReconciliationDisposition? Copy(GovernedLoopEffectReconciliationDisposition? value)
        => value is null
            ? null
            : new GovernedLoopEffectReconciliationDisposition(value.SchemaVersion, value.CaseId, value.BindingHash, value.DispositionId, value.Kind, value.AssessmentHash, value.AuthorityEvidenceHash, value.DisposedAtUtc, value.SafeDetail, value.ContentHash);

    /// <summary>Copies the optional accepted resolution.</summary>
    public static GovernedLoopEffectReconciliationResolution? Copy(GovernedLoopEffectReconciliationResolution? value)
        => value is null
            ? null
            : new GovernedLoopEffectReconciliationResolution(value.SchemaVersion, value.CaseId, value.BindingHash, value.ResolutionId, value.AssessmentHash, value.DispositionHash, value.Outcome, value.OutcomeEvidenceId, value.OutcomeEvidenceHash, value.AuthorityEvidenceHash, value.ResolvedAtUtc, value.SafeDetail, value.ContentHash);

    /// <summary>Copies one complete case and every bounded child collection.</summary>
    public static GovernedLoopEffectReconciliationCase Copy(GovernedLoopEffectReconciliationCase? value)
        => value is null
            ? null!
            : new GovernedLoopEffectReconciliationCase(value.SchemaVersion, value.CaseId, value.CaseVersion, value.Binding, value.ContractMetadata, value.EvidenceSources, value.ObservationHistory, value.AssessmentHistory, value.CurrentAssessmentHash, value.Disposition, value.Resolution, value.CaseReceiptHashes, value.PreviousContentHash, value.OpenedAtUtc, value.UpdatedAtUtc, value.ContentHash);

    /// <summary>Copies a bounded evidence-source collection, retaining one overflow sentinel for validation.</summary>
    public static IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource> CopySources(IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource>? values)
        => values is null ? null! : Array.AsReadOnly(values.Take(GovernedLoopEffectReconciliationContractLimits.MaxEvidenceSources + 1).Select(Copy).ToArray());

    /// <summary>Copies a bounded observation collection, retaining one overflow sentinel for validation.</summary>
    public static IReadOnlyList<GovernedLoopEffectReconciliationObservation> CopyObservations(IReadOnlyList<GovernedLoopEffectReconciliationObservation>? values)
        => values is null ? null! : Array.AsReadOnly(values.Take(GovernedLoopEffectReconciliationContractLimits.MaxObservations + 1).Select(Copy).ToArray());

    /// <summary>Copies a bounded assessment collection, retaining one overflow sentinel for validation.</summary>
    public static IReadOnlyList<GovernedLoopEffectReconciliationAssessment> CopyAssessments(IReadOnlyList<GovernedLoopEffectReconciliationAssessment>? values)
        => values is null ? null! : Array.AsReadOnly(values.Take(GovernedLoopEffectReconciliationContractLimits.MaxAssessments + 1).Select(Copy).ToArray());

    /// <summary>Copies a bounded hash-reference collection, retaining one overflow sentinel for validation.</summary>
    public static IReadOnlyList<string> CopyHashes(IReadOnlyList<string>? values, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        return values is null ? null! : Array.AsReadOnly(values.Take(maximum + 1).ToArray());
    }
}
