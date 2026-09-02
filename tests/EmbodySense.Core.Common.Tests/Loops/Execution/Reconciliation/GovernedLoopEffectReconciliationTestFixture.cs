using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationTestFixture
{
    internal const string CaseId = "reconciliation-case-1";

    internal static GovernedLoopEffectAttempt CurrentAttempt()
    {
        var prepared = Effects.GovernedLoopEffectAttemptContractTests.Prepare();
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash('8'), prepared.Payload.UpdatedAtUtc.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(2));
        return GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, prepared.Payload.UpdatedAtUtc.AddSeconds(3));
    }

    internal static GovernedLoopEffectReconciliationBinding Binding(GovernedLoopEffectAttempt? attempt = null)
        => GovernedLoopEffectReconciliationContract.CreateBinding(GovernedLoopExecutionTestFixture.WorkspaceId, 0, 1, attempt ?? CurrentAttempt());

    internal static GovernedLoopEffectReconciliationContractMetadata Metadata(GovernedLoopEffectAttempt? attempt = null)
    {
        var current = attempt ?? CurrentAttempt();
        return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            1,
            "workspace-action-reconciliation",
            1,
            current.Capability,
            current.Implementation,
            current.ActuatorOperationId,
            current.OperationDescriptorHash,
            "workspace-state-probe",
            1,
            Hash('9'),
            string.Empty));
    }

    internal static GovernedLoopEffectReconciliationEvidenceSource Source(GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectReconciliationContractMetadata metadata, string sourceId = "authoritative-probe", GovernedLoopEffectReconciliationReliabilityPosture reliability = GovernedLoopEffectReconciliationReliabilityPosture.Authoritative)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            1,
            CaseId,
            binding.ContentHash,
            sourceId,
            reliability == GovernedLoopEffectReconciliationReliabilityPosture.Authoritative ? GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative : GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            reliability,
            metadata.ContractId,
            metadata.ContractVersion,
            metadata.ContentHash,
            Hash('a'),
            OpenedAtUtc,
            null,
            string.Empty));

    internal static GovernedLoopEffectReconciliationObservation Observation(
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationEvidenceSource source,
        GovernedLoopEffectReconciliationObservedOutcome outcome,
        string observationId = "observation-1",
        GovernedLoopEffectReconciliationObservationKind kind = GovernedLoopEffectReconciliationObservationKind.Evidence,
        DateTimeOffset? observedAtUtc = null,
        string? evidenceReference = null,
        string? evidenceHash = null)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            1,
            CaseId,
            binding.ContentHash,
            observationId,
            source.SourceId,
            source.ContentHash,
            kind,
            source.ReliabilityPosture,
            outcome,
            evidenceReference ?? (kind == GovernedLoopEffectReconciliationObservationKind.Evidence ? $"evidence-{observationId}" : null),
            evidenceHash ?? (kind == GovernedLoopEffectReconciliationObservationKind.Evidence ? Hash(observationId[^1] is >= '0' and <= '9' ? observationId[^1] : 'b') : null),
            observedAtUtc ?? (kind == GovernedLoopEffectReconciliationObservationKind.Evidence ? OpenedAtUtc.AddMinutes(1) : null),
            OpenedAtUtc.AddMinutes(2),
            null,
            string.Empty));

    internal static GovernedLoopEffectReconciliationAssessment Assessment(GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectReconciliationAssessmentKind kind, IReadOnlyList<GovernedLoopEffectReconciliationObservation> observations)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(
            1,
            CaseId,
            binding.ContentHash,
            "assessment-1",
            kind,
            observations.Select(value => value.ContentHash).Order(StringComparer.Ordinal).ToArray(),
            Hash('c'),
            OpenedAtUtc.AddMinutes(3),
            null,
            string.Empty));

    internal static GovernedLoopEffectReconciliationDisposition Disposition(GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectReconciliationAssessment assessment, GovernedLoopEffectReconciliationDispositionKind kind)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationDisposition(
            1,
            CaseId,
            binding.ContentHash,
            "disposition-1",
            kind,
            assessment.ContentHash,
            Hash('d'),
            OpenedAtUtc.AddMinutes(4),
            null,
            string.Empty));

    internal static GovernedLoopEffectReconciliationResolution Resolution(
        GovernedLoopEffectReconciliationBinding binding,
        GovernedLoopEffectReconciliationAssessment assessment,
        GovernedLoopEffectReconciliationDisposition disposition,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectReconciliationObservation? observation = null)
        => GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(
            1,
            CaseId,
            binding.ContentHash,
            "resolution-1",
            assessment.ContentHash,
            disposition.ContentHash,
            outcome,
            observation?.EvidenceReference,
            observation?.EvidenceHash,
            Hash('e'),
            OpenedAtUtc.AddMinutes(5),
            null,
            string.Empty));

    internal static GovernedLoopEffectReconciliationCase Case(
        GovernedLoopEffectReconciliationAssessmentKind kind,
        GovernedLoopEffectReconciliationDispositionKind? dispositionKind = null,
        bool includeResolution = false,
        GovernedLoopEffectAttempt? attempt = null)
    {
        var current = attempt ?? CurrentAttempt();
        var binding = Binding(current);
        var metadata = Metadata(current);
        var source = Source(binding, metadata);
        var observations = ObservationsFor(binding, source, kind);
        var assessment = Assessment(binding, kind, observations);
        GovernedLoopEffectReconciliationDisposition? disposition = null;
        GovernedLoopEffectReconciliationResolution? resolution = null;
        if (dispositionKind is { } selectedDisposition)
        {
            disposition = Disposition(binding, assessment, selectedDisposition);
            if (includeResolution)
            {
                var outcome = GovernedLoopEffectReconciliationStateMatrix.GetAcceptedOutcome(kind)!.Value;
                var observation = outcome == GovernedLoopEffectOutcome.NotApplied ? null : observations[0];
                resolution = Resolution(binding, assessment, disposition, outcome, observation);
            }
        }

        return GovernedLoopEffectReconciliationContract.Create(
            CaseId,
            1,
            binding,
            metadata,
            [source],
            observations,
            [assessment],
            assessment.ContentHash,
            disposition,
            resolution,
            [Hash('f')],
            null,
            OpenedAtUtc,
            includeResolution ? OpenedAtUtc.AddMinutes(5) : disposition is null ? OpenedAtUtc.AddMinutes(3) : OpenedAtUtc.AddMinutes(4));
    }

    internal static DateTimeOffset OpenedAtUtc { get; } = GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(1);

    internal static string Hash(char value) => new(value, 64);

    private static IReadOnlyList<GovernedLoopEffectReconciliationObservation> ObservationsFor(GovernedLoopEffectReconciliationBinding binding, GovernedLoopEffectReconciliationEvidenceSource source, GovernedLoopEffectReconciliationAssessmentKind kind)
    {
        return kind switch
        {
            GovernedLoopEffectReconciliationAssessmentKind.Inconclusive => [],
            GovernedLoopEffectReconciliationAssessmentKind.Conflicting =>
            [
                Observation(binding, source, GovernedLoopEffectReconciliationObservedOutcome.NotApplied, "observation-1"),
                Observation(binding, source, GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded, "observation-2")
            ],
            GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied => [Observation(binding, source, GovernedLoopEffectReconciliationObservedOutcome.NotApplied)],
            GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded => [Observation(binding, source, GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded)],
            GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed => [Observation(binding, source, GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed)],
            GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown => [Observation(binding, source, GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
