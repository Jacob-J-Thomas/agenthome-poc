using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationApplicationTestFixture
{
    internal static (GovernedLoopEffectReconciliationCase Case, GovernedLoopEffectAttempt Attempt, GovernedActuatorInputEvidence Input) OpenCase()
    {
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var prepared = GovernedLoopEffectAttemptTestFixture.Prepare(fixture.Request, fixture.Descriptor, input!);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, GovernedLoopEffectAttemptTestFixture.Hash('f'), GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(2));
        var attempt = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(3));
        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(fixture.Request.AdmissionReceipt.Intent.WorkspaceId, 1, 1, attempt);
        var metadata = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "contract-1",
            1,
            fixture.Request.CapabilityPin.DescriptorIdentity,
            fixture.Request.CapabilityPin.Implementation,
            fixture.Descriptor.OperationId,
            fixture.Descriptor.ContentHash,
            "probe-1",
            1,
            GovernedLoopEffectAttemptTestFixture.Hash('8'),
            string.Empty));
        var opened = GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(4);
        var reconciliationCase = GovernedLoopEffectReconciliationContract.Open("case-1", binding, metadata, [], [], opened);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(reconciliationCase, attempt).IsValid);
        return (reconciliationCase, attempt, input!);
    }

    internal static (GovernedLoopEffectReconciliationCase Open, GovernedLoopEffectReconciliationCase Assessed, GovernedLoopEffectAttempt Attempt) AssessedCase()
    {
        var (open, attempt, _) = OpenCase();
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "assessment-1",
            GovernedLoopEffectReconciliationAssessmentKind.Inconclusive,
            [],
            GovernedLoopEffectAttemptTestFixture.Hash('9'),
            open.UpdatedAtUtc.AddSeconds(1),
            "Awaiting conclusive evidence.",
            string.Empty));
        var assessed = GovernedLoopEffectReconciliationContract.Create(
            open.CaseId,
            open.CaseVersion + 1,
            open.Binding,
            open.ContractMetadata,
            open.EvidenceSources,
            open.ObservationHistory,
            [assessment],
            assessment.ContentHash,
            null,
            null,
            open.CaseReceiptHashes,
            open.ContentHash,
            open.OpenedAtUtc,
            assessment.AssessedAtUtc.AddSeconds(1));
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(assessed, attempt).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContract.ValidateTransition(open, assessed).IsValid);
        return (open, assessed, attempt);
    }

    internal static (GovernedLoopEffectReconciliationCase Open, GovernedLoopEffectReconciliationCase Resolved, GovernedLoopEffectAttempt Current, GovernedLoopEffectAttempt Successor, GovernedActuatorInputEvidence Input) ResolvedCase()
    {
        var (open, attempt, input) = OpenCase();
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "source-1",
            GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            open.ContractMetadata.ContractId,
            open.ContractMetadata.ContractVersion,
            open.ContractMetadata.ContentHash,
            GovernedLoopEffectAttemptTestFixture.Hash('1'),
            open.OpenedAtUtc,
            null,
            string.Empty));
        var observation = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "observation-1",
            source.SourceId,
            source.ContentHash,
            GovernedLoopEffectReconciliationObservationKind.Evidence,
            source.ReliabilityPosture,
            GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            "evidence-1",
            GovernedLoopEffectAttemptTestFixture.Hash('2'),
            open.OpenedAtUtc.AddMinutes(1),
            open.OpenedAtUtc.AddMinutes(2),
            "No matching external effect exists.",
            string.Empty));
        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "assessment-1",
            GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied,
            [observation.ContentHash],
            GovernedLoopEffectAttemptTestFixture.Hash('3'),
            open.OpenedAtUtc.AddMinutes(3),
            "Authoritative evidence proves absence.",
            string.Empty));
        var assessed = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 2, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, null, null, [], open.ContentHash, open.OpenedAtUtc, assessment.AssessedAtUtc);
        var disposition = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationDisposition(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "disposition-1",
            GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied,
            assessment.ContentHash,
            GovernedLoopEffectAttemptTestFixture.Hash('4'),
            open.OpenedAtUtc.AddMinutes(4),
            "Accept exact absence proof.",
            string.Empty));
        var disposed = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 3, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, disposition, null, [], assessed.ContentHash, open.OpenedAtUtc, disposition.DisposedAtUtc);
        var resolution = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "resolution-1",
            assessment.ContentHash,
            disposition.ContentHash,
            GovernedLoopEffectOutcome.NotApplied,
            null,
            null,
            GovernedLoopEffectAttemptTestFixture.Hash('5'),
            open.OpenedAtUtc.AddMinutes(5),
            "Resolve as not applied.",
            string.Empty));
        var resolved = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 4, open.Binding, open.ContractMetadata, [source], [observation], [assessment], assessment.ContentHash, disposition, resolution, [], disposed.ContentHash, open.OpenedAtUtc, resolution.ResolvedAtUtc);
        var successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(attempt, resolved);

        Assert.True(GovernedLoopEffectReconciliationContract.ValidateTransition(open, assessed).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContract.ValidateTransition(assessed, disposed).IsValid);
        Assert.True(GovernedLoopEffectReconciliationContract.ValidateTransition(disposed, resolved).IsValid);
        Assert.True(GovernedLoopEffectReconciliationAttemptContract.IsDirectSuccessor(attempt, successor, resolved));
        return (open, resolved, attempt, successor, input);
    }

    internal static GovernedLoopFrontierPosture ReviewBlockedFrontier(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectAttempt attempt)
    {
        var trigger = GovernedLoopNodeExecutionEvidence.CreateActivation(0, 0, 1, "trigger", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [], [], GovernedLoopNodeExecutionStatus.Completed, 1, "trigger-operation-1", "trigger-complete", GovernedLoopEffectAttemptTestFixture.Hash('a'));
        var action = GovernedLoopNodeExecutionEvidence.CreateActivation(1, 1, 1, attempt.NodeId, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Action, "probe-action", 1), [], [], GovernedLoopNodeExecutionStatus.ReviewBlocked, attempt.NodeAttempt, attempt.Payload.OperationId);
        return GovernedLoopFrontierPosture.Create(attempt.Binding, value.Binding.WorkspaceId, GovernedLoopEffectAttemptTestFixture.Hash('b'), GovernedLoopEffectAttemptTestFixture.Hash('c'), GovernedLoopEffectAttemptTestFixture.Hash('d'), 1, 1, GovernedLoopFrontierStatus.ReviewBlocked, [trigger, action], attempt.Payload.UpdatedAtUtc.AddSeconds(1), string.Empty);
    }
}
