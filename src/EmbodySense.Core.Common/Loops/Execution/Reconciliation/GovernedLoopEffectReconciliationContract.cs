using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Creates and validates immutable value-free effect-reconciliation bindings and cases.</summary>
public static class GovernedLoopEffectReconciliationContract
{
    /// <summary>Creates a hash-bound identity for the exact authoritative reconciliation-required attempt.</summary>
    public static GovernedLoopEffectReconciliationBinding CreateBinding(string workspaceId, int activationOrdinal, int visitOrdinal, GovernedLoopEffectAttempt currentAttempt)
    {
        ArgumentNullException.ThrowIfNull(currentAttempt);
        if (GovernedLoopEffectAttemptContract.Validate(currentAttempt) is not null || currentAttempt.Payload.Phase != GovernedLoopEffectPhase.ReconciliationRequired)
        {
            throw new ArgumentException("The current attempt must be exact, valid, and reconciliation-required.", nameof(currentAttempt));
        }

        var candidate = new GovernedLoopEffectReconciliationBinding(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            workspaceId,
            currentAttempt.Binding,
            currentAttempt.NodeId,
            activationOrdinal,
            visitOrdinal,
            currentAttempt.NodeAttempt,
            currentAttempt.Payload.EffectId,
            currentAttempt.Payload.OperationId,
            currentAttempt.Payload.EffectGeneration,
            currentAttempt.Payload.IntentHash,
            currentAttempt.ContentHash,
            string.Empty);
        return GovernedLoopEffectReconciliationContractHash.Apply(candidate);
    }

    /// <summary>Creates a canonical first open case with no observations, assessment, disposition, or resolution.</summary>
    public static GovernedLoopEffectReconciliationCase Open(
        string caseId,
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationContractMetadata contractMetadata,
        IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource> evidenceSources,
        IReadOnlyList<string> caseReceiptHashes,
        DateTimeOffset openedAtUtc)
    {
        return Create(
            caseId,
            1,
            binding,
            contractMetadata,
            evidenceSources,
            [],
            [],
            null,
            null,
            null,
            caseReceiptHashes,
            null,
            openedAtUtc,
            openedAtUtc);
    }

    /// <summary>Creates one canonical case version from already ordinally ordered set-like histories and applies its content hash.</summary>
    public static GovernedLoopEffectReconciliationCase Create(
        string caseId,
        long caseVersion,
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationContractMetadata contractMetadata,
        IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSource> evidenceSources,
        IReadOnlyList<GovernedLoopEffectReconciliationObservation> observationHistory,
        IReadOnlyList<GovernedLoopEffectReconciliationAssessment> assessmentHistory,
        string? currentAssessmentHash,
        GovernedLoopEffectReconciliationDisposition? disposition,
        GovernedLoopEffectReconciliationResolution? resolution,
        IReadOnlyList<string> caseReceiptHashes,
        string? previousContentHash,
        DateTimeOffset openedAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(evidenceSources);
        ArgumentNullException.ThrowIfNull(observationHistory);
        ArgumentNullException.ThrowIfNull(assessmentHistory);
        ArgumentNullException.ThrowIfNull(caseReceiptHashes);
        var candidate = new GovernedLoopEffectReconciliationCase(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            caseId,
            caseVersion,
            binding,
            contractMetadata,
            evidenceSources,
            observationHistory,
            assessmentHistory,
            currentAssessmentHash,
            disposition,
            resolution,
            caseReceiptHashes,
            previousContentHash,
            openedAtUtc,
            updatedAtUtc,
            string.Empty);
        return GovernedLoopEffectReconciliationContractHash.Apply(candidate);
    }

    /// <summary>Validates one complete case without consulting external state.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationCase? reconciliationCase)
        => GovernedLoopEffectReconciliationContractValidator.Validate(reconciliationCase);

    /// <summary>Validates a complete case against the exact authoritative current attempt.</summary>
    public static GovernedLoopEffectReconciliationValidationResult Validate(GovernedLoopEffectReconciliationCase? reconciliationCase, GovernedLoopEffectAttempt? currentAttempt)
        => GovernedLoopEffectReconciliationContractValidator.Validate(reconciliationCase, currentAttempt);

    /// <summary>Validates a direct immutable case-version successor and its hash-chain edge.</summary>
    public static GovernedLoopEffectReconciliationValidationResult ValidateTransition(GovernedLoopEffectReconciliationCase? current, GovernedLoopEffectReconciliationCase? next)
        => GovernedLoopEffectReconciliationContractValidator.ValidateTransition(current, next);
}
