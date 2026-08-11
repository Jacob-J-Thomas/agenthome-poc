using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialResumeExecutorTests
{
    private static readonly DateTimeOffset _now = GovernedLoopSequentialApplicationTestFixture.Now;

    [Fact]
    public async Task Legacy_run_delegates_the_exact_resume_request_without_canonical_reads()
    {
        var request = new CustomLoopResumeExecutionRequest("run-legacy", 7, "resume-legacy", AuditSchema.Actors.Web, true);
        var runStore = new TestRunStore(Run(request.RunId, null, null));
        var evidence = new TestRunEvidenceSource(null);
        var admission = GovernedLoopAdmissionTestHarness.Create();
        var runtime = new RecordingOrderedRuntime();
        var legacy = new RecordingLegacyExecutor();
        var service = new GovernedLoopSequentialResumeExecutor(runStore, evidence, admission, admission, runtime, legacy);

        var result = await service.ResumeAsync(request);

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        Assert.Equal(request, Assert.Single(legacy.Requests));
        Assert.Empty(runtime.ResumeRequests);
        Assert.Equal(0, evidence.CallCount);
    }

    [Fact]
    public async Task Canonical_resume_rebuilds_only_the_exact_receipt_pinned_artifact_and_forwards_lifecycle_coordinates()
    {
        var context = await ContextAsync();
        var run = Run(context.Binding.ExecutionBinding.RunId, context.Binding, context.Invocation);
        var runStore = new TestRunStore(run);
        var evidence = new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(context.Binding, context.Invocation));
        var runtime = new RecordingOrderedRuntime();
        var legacy = new RecordingLegacyExecutor();
        var service = new GovernedLoopSequentialResumeExecutor(runStore, evidence, context.Store, context.Store, runtime, legacy);
        var request = new CustomLoopResumeExecutionRequest(run.Id, 11, "resume-canonical", AuditSchema.Actors.Cli, true);

        var result = await service.ResumeAsync(request);

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, result.Status);
        var forwarded = Assert.Single(runtime.ResumeRequests);
        Assert.Equal(context.Binding.ContentHash, forwarded.Anchor.AdapterBinding.ContentHash);
        Assert.Equal(context.Invocation.ContentHash, forwarded.Anchor.InvocationSnapshot.ContentHash);
        Assert.Equal(context.Artifact.ArtifactHash, forwarded.Artifact.ArtifactHash);
        Assert.Equal(context.Binding.ExecutionBinding.Revision, forwarded.Plan.Revision);
        Assert.Equal(request.RunningLifecycleVersion, forwarded.RunningLifecycleVersion);
        Assert.Equal(request.ResumeOperationId, forwarded.ResumeOperationId);
        Assert.Equal(request.Actor, forwarded.Actor);
        Assert.True(forwarded.ActiveRunAlreadyRegistered);
        Assert.Empty(legacy.Requests);
        Assert.Equal(1, evidence.CallCount);
    }

    [Fact]
    public async Task Canonical_resume_rejects_partial_handoff_and_pinned_artifact_substitution_without_dispatch()
    {
        var context = await ContextAsync();
        var runtime = new RecordingOrderedRuntime();
        var legacy = new RecordingLegacyExecutor();
        var partial = Run(context.Binding.ExecutionBinding.RunId, context.Binding, null);
        var partialService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(partial),
            new TestRunEvidenceSource(null),
            context.Store,
            context.Store,
            runtime,
            legacy);

        var partialResult = await partialService.ResumeAsync(new CustomLoopResumeExecutionRequest(partial.Id, 3, "resume-partial", AuditSchema.Actors.Web, false));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, partialResult.Status);
        Assert.Empty(runtime.ResumeRequests);
        Assert.Empty(legacy.Requests);

        var otherArtifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2, owningRole: context.Artifact.Graph.OwningRole);
        context.Store.GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ready, 2, otherArtifact);
        var exact = Run(context.Binding.ExecutionBinding.RunId, context.Binding, context.Invocation);
        var substitutedService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(exact),
            new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(context.Binding, context.Invocation)),
            context.Store,
            context.Store,
            runtime,
            legacy);

        var substituted = await substitutedService.ResumeAsync(new CustomLoopResumeExecutionRequest(exact.Id, 4, "resume-substituted-artifact", AuditSchema.Actors.Web, false));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, substituted.Status);
        Assert.Empty(runtime.ResumeRequests);
        Assert.Empty(legacy.Requests);
    }

    [Fact]
    public async Task Canonical_resume_preserves_cancellation_during_the_first_durable_read()
    {
        var context = await ContextAsync();
        var runStore = new TestRunStore(Run(context.Binding.ExecutionBinding.RunId, context.Binding, context.Invocation));
        var service = new GovernedLoopSequentialResumeExecutor(
            runStore,
            new TestRunEvidenceSource(null),
            context.Store,
            context.Store,
            new RecordingOrderedRuntime(),
            new RecordingLegacyExecutor());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResumeAsync(
            new CustomLoopResumeExecutionRequest(context.Binding.ExecutionBinding.RunId, 2, "resume-cancelled", AuditSchema.Actors.Web, false),
            cancellation.Token));

        Assert.Equal(1, runStore.GetCount);
    }

    private static async Task<TestContext> ContextAsync()
    {
        var store = GovernedLoopAdmissionTestHarness.Create();
        var seedOutcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>((await store.CreateService().AdmitAsync(store.Request)).Outcome);
        var seedReceipt = Assert.IsType<GovernedLoopAdmissionReceipt>(seedOutcome.Receipt);
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(owningRole: seedReceipt.Intent.Role);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-resume", new string('7', 64));
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Resume the exact canonical invocation.",
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            _now,
            CustomLoopContextSnapshot.CreateEmpty(_now).SourceManifest,
            string.Empty));
        var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            1,
            "admit-resume",
            invocation.ContentHash,
            string.Empty,
            publication,
            seedReceipt.Intent.AuthorityGrant,
            seedReceipt.Intent.ActorId,
            "web"));
        var intent = new GovernedLoopAdmissionIntent(
            1,
            seedReceipt.Intent.WorkspaceId,
            request.OperationId,
            request.RequestHash,
            publication,
            request.AuthorityGrant,
            artifact.Graph.OwningRole,
            request.ActorId,
            request.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var execution = GovernedLoopExecutionBinding.Create(1, "run-sequential-resume", publication.Revision, 1);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            1,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            seedReceipt.Evidence.GrantProfile,
            new AuthorityGrantBoundary(
                _now.AddHours(-1),
                _now.AddHours(1),
                seedReceipt.Evidence.GrantBoundary.CompletionConstraint),
            seedReceipt.Evidence.GrantDependencyEvidenceHash,
            seedReceipt.Evidence.EffectiveAuthority,
            seedReceipt.Evidence.CapabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, seedReceipt.Evidence.EffectiveAuthority, seedReceipt.Evidence.CapabilityAdmission),
            _now,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(1, intent, evidence, _now, string.Empty));
        var outcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            1,
            intent,
            GovernedLoopAdmissionDisposition.Admitted,
            receipt,
            null,
            _now,
            string.Empty));
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            1,
            intent.WorkspaceId,
            execution,
            request.OperationId,
            receipt,
            receipt.ContentHash,
            request.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        store.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Found, 2, outcome);
        store.GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ready, 2, artifact);
        return new TestContext(store, artifact, binding, invocation);
    }

    private static CustomLoopRunRecord Run(
        string runId,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialInvocationSnapshot? invocation)
    {
        var definition = CustomLoopDefinition.CreateSeed("sequential-loop", "sequential-role", "infer-01", "create-test-run", _now);
        return new CustomLoopRunRecord(
            1,
            runId,
            definition.Id,
            2,
            CustomLoopRunStatus.Running,
            _now,
            _now,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            binding?.AdmissionOperationId ?? "admit-legacy",
            AuditSchema.Actors.Web,
            binding?.AdmissionRequestHash ?? new string('0', 64),
            definition,
            "Resume the exact canonical invocation.",
            null,
            CustomLoopContextSnapshot.CreateEmpty(_now),
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [],
            null,
            null,
            null)
        {
            SequentialAdapterBinding = binding,
            SequentialInvocationSnapshot = invocation,
        };
    }

    private sealed record TestContext(
        GovernedLoopAdmissionTestHarness Store,
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialAdapterBinding Binding,
        GovernedLoopSequentialInvocationSnapshot Invocation);

    private sealed class TestRunStore(CustomLoopRunRecord? run) : ICustomLoopRunStore
    {
        public int GetCount { get; private set; }

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            GetCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(string.Equals(run?.Id, runId, StringComparison.Ordinal) ? run : null);
        }

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord value, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord value, int expectedLifecycleVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestRunEvidenceSource(GovernedLoopSequentialRunEvidence? evidence) : IGovernedLoopSequentialRunEvidenceSource
    {
        public int CallCount { get; private set; }

        public Task<GovernedLoopSequentialRunEvidence?> ResolveAsync(string runId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(evidence);
        }
    }

    private sealed class RecordingOrderedRuntime : IGovernedLoopSequentialOrderedRuntime
    {
        public List<GovernedLoopSequentialOrderedResumeRequest> ResumeRequests { get; } = [];

        public Task<CustomLoopOrderedRunResult> RunAsync(GovernedLoopSequentialOrderedRunRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopOrderedRunResult> ResumeAsync(GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeRequests.Add(request);
            return Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Completed, null, "Canonical resume recorded."));
        }
    }

    private sealed class RecordingLegacyExecutor : ICustomLoopResumeExecutor
    {
        public List<CustomLoopResumeExecutionRequest> Requests { get; } = [];

        public Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new CustomLoopOrderedRunResult(CustomLoopOrderedRunStatus.Completed, null, "Legacy resume recorded."));
        }
    }
}
