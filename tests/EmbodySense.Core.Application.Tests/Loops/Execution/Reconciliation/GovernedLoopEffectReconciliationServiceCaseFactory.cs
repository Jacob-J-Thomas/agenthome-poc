using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationServiceCaseFactory
{
    internal static (GovernedLoopEffectReconciliationCase Case, GovernedLoopEffectAttempt Attempt, GovernedActuatorInputEvidence Input) OpenCaseWithNotAppliedEvidence()
        => OpenCaseWithEvidence(GovernedLoopEffectReconciliationObservedOutcome.NotApplied);

    internal static (GovernedLoopEffectReconciliationCase Case, GovernedLoopEffectAttempt Attempt, GovernedActuatorInputEvidence Input) OpenCaseWithEvidence(GovernedLoopEffectReconciliationObservedOutcome outcome, bool conflicting = false)
    {
        var (open, attempt, input) = GovernedLoopEffectReconciliationApplicationTestFixture.OpenCase();
        var source = Source(open, "source-1", open.OpenedAtUtc);
        var observation = Observation(open, source, "observation-1", outcome, open.OpenedAtUtc.AddMinutes(1), "evidence-1", Hash('2'));
        var observations = conflicting
            ? new[] { observation, Observation(open, source, "observation-2", Opposite(outcome), open.OpenedAtUtc.AddMinutes(2), "evidence-2", Hash('3')) }
            : new[] { observation };
        var value = GovernedLoopEffectReconciliationContract.Create(
            open.CaseId,
            open.CaseVersion,
            open.Binding,
            open.ContractMetadata,
            [source],
            observations,
            [],
            null,
            null,
            null,
            open.CaseReceiptHashes,
            null,
            open.OpenedAtUtc,
            observations[^1].RecordedAtUtc);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(value, attempt).IsValid);
        return (value, attempt, input);
    }

    private static GovernedLoopEffectReconciliationEvidenceSource Source(GovernedLoopEffectReconciliationCase open, string sourceId, DateTimeOffset registeredAtUtc)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            sourceId,
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            open.ContractMetadata.ContractId,
            open.ContractMetadata.ContractVersion,
            open.ContractMetadata.ContentHash,
            Hash('1'),
            registeredAtUtc,
            null,
            string.Empty));

    private static GovernedLoopEffectReconciliationObservation Observation(
        GovernedLoopEffectReconciliationCase open,
        GovernedLoopEffectReconciliationEvidenceSource source,
        string observationId,
        GovernedLoopEffectReconciliationObservedOutcome outcome,
        DateTimeOffset observedAtUtc,
        string evidenceReference,
        string evidenceHash)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            observationId,
            source.SourceId,
            source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            source.ReliabilityPosture,
            outcome,
            evidenceReference,
            evidenceHash,
            observedAtUtc,
            observedAtUtc.AddMinutes(1),
            "No external effect was observed.",
            string.Empty));

    private static GovernedLoopEffectReconciliationObservedOutcome Opposite(GovernedLoopEffectReconciliationObservedOutcome outcome)
        => outcome == GovernedLoopEffectReconciliationObservedOutcome.NotApplied
            ? GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded
            : GovernedLoopEffectReconciliationObservedOutcome.NotApplied;

    private static string Hash(char value) => GovernedLoopEffectAttemptTestFixture.Hash(value);
}
