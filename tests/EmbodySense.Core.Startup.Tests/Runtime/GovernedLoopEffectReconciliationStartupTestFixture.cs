using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal static class GovernedLoopEffectReconciliationStartupTestFixture
{
    internal static async Task<(GovernedLoopEffectReconciliationCase Open, GovernedLoopEffectReconciliationCase Current, GovernedLoopEffectAttempt Attempt)> SeedAsync(
        string workspaceRoot,
        string suffix = "one",
        bool resolve = false,
        GovernedLoopEffectReconciliationEvidenceSourceKind sourceKind = GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
        GovernedLoopEffectReconciliationReliabilityPosture reliabilityPosture = GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
        GovernedLoopEffectReconciliationObservationKind observationKind = GovernedLoopEffectReconciliationObservationKind.Evidence,
        GovernedLoopEffectReconciliationObservedOutcome observedOutcome = GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
        GovernedLoopEffectReconciliationObservedOutcome? secondaryObservedOutcome = null,
        GovernedLoopEffectReconciliationAssessmentKind? assessmentKind = null)
    {
        if (resolve && assessmentKind is not null)
        {
            throw new ArgumentException("Choose either the canonical resolved fixture or an assessed projection fixture.", nameof(assessmentKind));
        }

        var paths = new WorkspacePaths(workspaceRoot);
        var effectStore = new GovernedLoopEffectAttemptStore(paths);
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(fixture.Request.InputJson, out var input, out _));
        var execution = GovernedLoopExecutionBinding.Create(1, "run-reconciliation-" + suffix, fixture.Request.ExecutionBinding.Revision, 1);
        var request = fixture.Request with
        {
            ExecutionBinding = execution,
            EffectId = "effect-reconciliation-" + suffix,
            IdempotencyOperationId = "effect-operation-reconciliation-" + suffix,
            CorrelationId = "effect-correlation-reconciliation-" + suffix,
        };
        var prepared = GovernedLoopEffectAttemptTestFixture.Prepare(request, fixture.Descriptor, input!);
        var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(prepared, Hash("dispatch-authority-" + suffix), GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(1));
        var crossed = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(2));
        var attempt = GovernedLoopEffectAttemptContract.Advance(crossed, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(3));
        var created = await effectStore.BeginAsync(prepared);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, created.Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(prepared.ContentHash, authorized, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(authorized.ContentHash, crossed, created.Lease!)).Status);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, (await effectStore.CompareExchangeAsync(crossed.ContentHash, attempt, created.Lease!)).Status);
        created.Lease!.Dispose();

        var binding = GovernedLoopEffectReconciliationContract.CreateBinding(CapabilityWorkspaceScopeId.Create(workspaceRoot), 1, 1, attempt);
        var metadata = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationContractMetadata(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "contract-reconciliation-" + suffix,
            1,
            fixture.Descriptor.Capability,
            fixture.Descriptor.Implementation,
            fixture.Descriptor.OperationId,
            fixture.Descriptor.ContentHash,
            "probe-reconciliation-" + suffix,
            1,
            Hash("probe-contract-" + suffix),
            string.Empty));
        var openedAtUtc = GovernedLoopEffectAttemptTestFixture.Now.AddSeconds(4);
        var source = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationEvidenceSource(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            "case-reconciliation-" + suffix,
            binding.ContentHash,
            "source-reconciliation-" + suffix,
            sourceKind,
            reliabilityPosture,
            metadata.ContractId,
            metadata.ContractVersion,
            metadata.ContentHash,
            Hash("private-registration-authority-" + suffix),
            openedAtUtc,
            null,
            string.Empty));
        var sources = new List<GovernedLoopEffectReconciliationEvidenceSource> { source };
        var observations = new List<GovernedLoopEffectReconciliationObservation>
        {
            CreateObservation(source, suffix, observationKind, observedOutcome, openedAtUtc),
        };
        if (secondaryObservedOutcome is { } secondaryOutcome)
        {
            var secondarySource = GovernedLoopEffectReconciliationContractHash.Apply(source with
            {
                SourceId = source.SourceId + "-secondary",
                RegistrationEvidenceHash = Hash("private-registration-authority-secondary-" + suffix),
                ContentHash = string.Empty,
            });
            sources.Add(secondarySource);
            observations.Add(CreateObservation(secondarySource, suffix + "-secondary", GovernedLoopEffectReconciliationObservationKind.Evidence, secondaryOutcome, openedAtUtc));
        }
        var open = GovernedLoopEffectReconciliationContract.Create(
            source.CaseId,
            1,
            binding,
            metadata,
            sources,
            observations,
            [],
            null,
            null,
            null,
            [Hash("case-receipt-" + suffix)],
            null,
            openedAtUtc,
            observations[^1].RecordedAtUtc);
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(open, attempt).IsValid);

        var caseStore = new GovernedLoopEffectReconciliationCaseStore(effectStore);
        await PersistAsync(caseStore, open, "open-reconciliation-" + suffix, "open", null, null, null);
        if (assessmentKind is { } projectedAssessmentKind)
        {
            var projectedAssessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(
                GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
                open.CaseId,
                open.Binding.ContentHash,
                "assessment-reconciliation-" + suffix,
                projectedAssessmentKind,
                observations.Select(item => item.ContentHash).Order(StringComparer.Ordinal).ToArray(),
                Hash("private-assessment-authority-" + suffix),
                open.UpdatedAtUtc.AddSeconds(1),
                "private assessment annotation " + suffix,
                string.Empty));
            var assessedProjection = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 2, open.Binding, open.ContractMetadata, open.EvidenceSources, open.ObservationHistory, [projectedAssessment], projectedAssessment.ContentHash, null, null, open.CaseReceiptHashes, open.ContentHash, open.OpenedAtUtc, projectedAssessment.AssessedAtUtc);
            await PersistAsync(caseStore, assessedProjection, "assess-reconciliation-" + suffix, "assess", open.CaseVersion, open.ContentHash, null);
            return (open, assessedProjection, attempt);
        }
        if (!resolve)
        {
            return (open, open, attempt);
        }

        var assessment = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationAssessment(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "assessment-reconciliation-" + suffix,
            GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied,
            [observations[0].ContentHash],
            Hash("private-assessment-authority-" + suffix),
            open.UpdatedAtUtc.AddSeconds(1),
            "private assessment annotation " + suffix,
            string.Empty));
        var assessed = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 2, open.Binding, open.ContractMetadata, open.EvidenceSources, open.ObservationHistory, [assessment], assessment.ContentHash, null, null, open.CaseReceiptHashes, open.ContentHash, open.OpenedAtUtc, assessment.AssessedAtUtc);
        await PersistAsync(caseStore, assessed, "assess-reconciliation-" + suffix, "assess", open.CaseVersion, open.ContentHash, null);

        var disposition = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationDisposition(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "disposition-reconciliation-" + suffix,
            GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied,
            assessment.ContentHash,
            Hash("private-disposition-authority-" + suffix),
            assessed.UpdatedAtUtc.AddSeconds(1),
            "private disposition annotation " + suffix,
            string.Empty));
        var disposed = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 3, open.Binding, open.ContractMetadata, open.EvidenceSources, open.ObservationHistory, assessed.AssessmentHistory, assessment.ContentHash, disposition, null, open.CaseReceiptHashes, assessed.ContentHash, open.OpenedAtUtc, disposition.DisposedAtUtc);
        await PersistAsync(caseStore, disposed, "dispose-reconciliation-" + suffix, "dispose", assessed.CaseVersion, assessed.ContentHash, null);

        var resolution = GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationResolution(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            open.CaseId,
            open.Binding.ContentHash,
            "resolution-reconciliation-" + suffix,
            assessment.ContentHash,
            disposition.ContentHash,
            GovernedLoopEffectOutcome.NotApplied,
            null,
            null,
            Hash("private-resolution-authority-" + suffix),
            disposed.UpdatedAtUtc.AddSeconds(1),
            "private resolution annotation " + suffix,
            string.Empty));
        var resolved = GovernedLoopEffectReconciliationContract.Create(open.CaseId, 4, open.Binding, open.ContractMetadata, open.EvidenceSources, open.ObservationHistory, disposed.AssessmentHistory, assessment.ContentHash, disposition, resolution, open.CaseReceiptHashes, disposed.ContentHash, open.OpenedAtUtc, resolution.ResolvedAtUtc);
        var successor = GovernedLoopEffectReconciliationAttemptContract.CreateSuccessor(attempt, resolved);
        await PersistAsync(caseStore, resolved, "resolve-reconciliation-" + suffix, "resolve", disposed.CaseVersion, disposed.ContentHash, successor);
        return (open, resolved, attempt);
    }

    internal static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static GovernedLoopEffectReconciliationObservation CreateObservation(
        GovernedLoopEffectReconciliationEvidenceSource source,
        string suffix,
        GovernedLoopEffectReconciliationObservationKind kind,
        GovernedLoopEffectReconciliationObservedOutcome outcome,
        DateTimeOffset recordedAtUtc)
    {
        var hasExactEvidence = kind == GovernedLoopEffectReconciliationObservationKind.Evidence;
        return GovernedLoopEffectReconciliationContractHash.Apply(new GovernedLoopEffectReconciliationObservation(
            GovernedLoopEffectReconciliationContractLimits.CurrentSchemaVersion,
            source.CaseId,
            source.BindingHash,
            "observation-reconciliation-" + suffix,
            source.SourceId,
            source.ContentHash,
            kind,
            source.ReliabilityPosture,
            outcome,
            hasExactEvidence ? "evidence-reconciliation-" + suffix : null,
            hasExactEvidence ? Hash("evidence-reconciliation-" + suffix) : null,
            hasExactEvidence ? recordedAtUtc.AddSeconds(1) : null,
            recordedAtUtc.AddSeconds(2),
            "private observation annotation " + suffix,
            string.Empty));
    }

    private static async Task PersistAsync(
        GovernedLoopEffectReconciliationCaseStore store,
        GovernedLoopEffectReconciliationCase replacement,
        string operationId,
        string purpose,
        long? expectedVersion,
        string? expectedHash,
        GovernedLoopEffectAttempt? successor)
    {
        var result = await store.CompareExchangeAsync(new GovernedLoopEffectReconciliationCaseMutationRequest(
            operationId,
            Hash(operationId + "\n" + purpose),
            purpose,
            expectedVersion,
            expectedHash,
            replacement.Binding,
            replacement,
            successor));
        Assert.Equal(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, result.Status);
        Assert.Equal(replacement.ContentHash, result.Case?.ContentHash);
    }
}
