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
        var run = Run(context.Binding.ExecutionBinding.RunId, context.Binding, context.Invocation, context.Plan);
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
        var exact = Run(context.Binding.ExecutionBinding.RunId, context.Binding, context.Invocation, context.Plan);
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
        var runStore = new TestRunStore(Run(context.Binding.ExecutionBinding.RunId, context.Binding, context.Invocation, context.Plan));
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

    [Fact]
    public async Task Missing_run_returns_not_found_without_reading_canonical_evidence_or_dispatching()
    {
        var evidence = new TestRunEvidenceSource(null);
        var admission = GovernedLoopAdmissionTestHarness.Create();
        var runtime = new RecordingOrderedRuntime();
        var legacy = new RecordingLegacyExecutor();
        var service = new GovernedLoopSequentialResumeExecutor(new TestRunStore(null), evidence, admission, admission, runtime, legacy);

        var result = await service.ResumeAsync(new CustomLoopResumeExecutionRequest("missing-run", 1, "resume-missing", AuditSchema.Actors.Web, false));

        Assert.Equal(CustomLoopOrderedRunStatus.NotFound, result.Status);
        Assert.Equal(0, evidence.CallCount);
        Assert.Empty(runtime.ResumeRequests);
        Assert.Empty(legacy.Requests);
    }

    [Fact]
    public async Task Canonical_evidence_read_preserves_caller_cancellation_and_contains_adapter_failures()
    {
        var cancelledContext = await ContextAsync();
        var cancelledRun = Run(cancelledContext.Binding.ExecutionBinding.RunId, cancelledContext.Binding, cancelledContext.Invocation, cancelledContext.Plan);
        using var cancellation = new CancellationTokenSource();
        var cancellingEvidence = new TestRunEvidenceSource(null) { BeforeResolve = cancellation.Cancel };
        var cancelledRuntime = new RecordingOrderedRuntime();
        var cancelledService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(cancelledRun),
            cancellingEvidence,
            cancelledContext.Store,
            cancelledContext.Store,
            cancelledRuntime,
            new RecordingLegacyExecutor());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledService.ResumeAsync(
            new CustomLoopResumeExecutionRequest(cancelledRun.Id, 2, "resume-cancel-evidence", AuditSchema.Actors.Web, false),
            cancellation.Token));
        Assert.Empty(cancelledRuntime.ResumeRequests);

        var failedContext = await ContextAsync();
        var failedRun = Run(failedContext.Binding.ExecutionBinding.RunId, failedContext.Binding, failedContext.Invocation, failedContext.Plan);
        var failedEvidence = new TestRunEvidenceSource(null) { Exception = new IOException("evidence unavailable") };
        var failedRuntime = new RecordingOrderedRuntime();
        var failedService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(failedRun),
            failedEvidence,
            failedContext.Store,
            failedContext.Store,
            failedRuntime,
            new RecordingLegacyExecutor());

        var failed = await failedService.ResumeAsync(new CustomLoopResumeExecutionRequest(failedRun.Id, 2, "resume-failed-evidence", AuditSchema.Actors.Web, false));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, failed.Status);
        Assert.Contains(nameof(IOException), failed.Detail, StringComparison.Ordinal);
        Assert.Empty(failedRuntime.ResumeRequests);
    }

    [Fact]
    public async Task Canonical_resume_rejects_missing_evidence_admission_and_request_identity_without_dispatch()
    {
        var missingEvidenceContext = await ContextAsync();
        var exactRun = Run(missingEvidenceContext.Binding.ExecutionBinding.RunId, missingEvidenceContext.Binding, missingEvidenceContext.Invocation, missingEvidenceContext.Plan);
        var runtime = new RecordingOrderedRuntime();
        var missingEvidenceService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(exactRun),
            new TestRunEvidenceSource(null),
            missingEvidenceContext.Store,
            missingEvidenceContext.Store,
            runtime,
            new RecordingLegacyExecutor());

        var missingEvidence = await missingEvidenceService.ResumeAsync(new CustomLoopResumeExecutionRequest(exactRun.Id, 2, "resume-missing-evidence", AuditSchema.Actors.Web, false));
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, missingEvidence.Status);

        var missingAdmissionContext = await ContextAsync();
        missingAdmissionContext.Store.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.NotFound, 3, null);
        var missingAdmissionRun = Run(missingAdmissionContext.Binding.ExecutionBinding.RunId, missingAdmissionContext.Binding, missingAdmissionContext.Invocation, missingAdmissionContext.Plan);
        var missingAdmissionService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(missingAdmissionRun),
            new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(missingAdmissionContext.Binding, missingAdmissionContext.Invocation)),
            missingAdmissionContext.Store,
            missingAdmissionContext.Store,
            runtime,
            new RecordingLegacyExecutor());

        var missingAdmission = await missingAdmissionService.ResumeAsync(new CustomLoopResumeExecutionRequest(missingAdmissionRun.Id, 2, "resume-missing-admission", AuditSchema.Actors.Web, false));
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, missingAdmission.Status);

        var mismatchContext = await ContextAsync();
        var substitutedAdmissionContext = await ContextAsync("run-substituted-admission");
        mismatchContext.Store.StoreReadResult = substitutedAdmissionContext.Store.StoreReadResult;
        var mismatchRun = Run(mismatchContext.Binding.ExecutionBinding.RunId, mismatchContext.Binding, mismatchContext.Invocation, mismatchContext.Plan);
        var mismatchService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(mismatchRun),
            new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(mismatchContext.Binding, mismatchContext.Invocation)),
            mismatchContext.Store,
            mismatchContext.Store,
            runtime,
            new RecordingLegacyExecutor());

        var mismatch = await mismatchService.ResumeAsync(new CustomLoopResumeExecutionRequest(mismatchRun.Id, 2, "resume-request-mismatch", AuditSchema.Actors.Web, false));
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, mismatch.Status);
        Assert.Empty(runtime.ResumeRequests);
    }

    [Fact]
    public async Task Canonical_graph_read_preserves_cancellation_contains_failures_and_requires_a_frontier()
    {
        var cancelledContext = await ContextAsync();
        var cancelledRun = Run(cancelledContext.Binding.ExecutionBinding.RunId, cancelledContext.Binding, cancelledContext.Invocation, cancelledContext.Plan);
        using var cancellation = new CancellationTokenSource();
        cancelledContext.Store.AfterMutableRead = phase =>
        {
            if (phase == "graph")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        var cancelledRuntime = new RecordingOrderedRuntime();
        var cancelledService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(cancelledRun),
            new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(cancelledContext.Binding, cancelledContext.Invocation)),
            cancelledContext.Store,
            cancelledContext.Store,
            cancelledRuntime,
            new RecordingLegacyExecutor());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledService.ResumeAsync(
            new CustomLoopResumeExecutionRequest(cancelledRun.Id, 2, "resume-cancel-graph", AuditSchema.Actors.Web, false),
            cancellation.Token));
        Assert.Empty(cancelledRuntime.ResumeRequests);

        var failedContext = await ContextAsync();
        var failedRun = Run(failedContext.Binding.ExecutionBinding.RunId, failedContext.Binding, failedContext.Invocation, failedContext.Plan);
        failedContext.Store.AfterMutableRead = phase =>
        {
            if (phase == "graph")
            {
                throw new IOException("graph unavailable");
            }
        };
        var failedRuntime = new RecordingOrderedRuntime();
        var failedService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(failedRun),
            new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(failedContext.Binding, failedContext.Invocation)),
            failedContext.Store,
            failedContext.Store,
            failedRuntime,
            new RecordingLegacyExecutor());

        var failed = await failedService.ResumeAsync(new CustomLoopResumeExecutionRequest(failedRun.Id, 2, "resume-failed-graph", AuditSchema.Actors.Web, false));
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, failed.Status);
        Assert.Contains(nameof(IOException), failed.Detail, StringComparison.Ordinal);
        Assert.Empty(failedRuntime.ResumeRequests);

        var noFrontierContext = await ContextAsync();
        var noFrontierRun = Run(noFrontierContext.Binding.ExecutionBinding.RunId, noFrontierContext.Binding, noFrontierContext.Invocation);
        var noFrontierRuntime = new RecordingOrderedRuntime();
        var noFrontierService = new GovernedLoopSequentialResumeExecutor(
            new TestRunStore(noFrontierRun),
            new TestRunEvidenceSource(new GovernedLoopSequentialRunEvidence(noFrontierContext.Binding, noFrontierContext.Invocation)),
            noFrontierContext.Store,
            noFrontierContext.Store,
            noFrontierRuntime,
            new RecordingLegacyExecutor());

        var noFrontier = await noFrontierService.ResumeAsync(new CustomLoopResumeExecutionRequest(noFrontierRun.Id, 2, "resume-no-frontier", AuditSchema.Actors.Web, false));
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, noFrontier.Status);
        Assert.Empty(noFrontierRuntime.ResumeRequests);
    }

    private static async Task<TestContext> ContextAsync(string runId = "run-sequential-resume")
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
        var execution = GovernedLoopExecutionBinding.Create(1, runId, publication.Revision, 1);
        var grantBoundary = new AuthorityGrantBoundary(
            _now.AddHours(-1),
            _now.AddHours(1),
            seedReceipt.Evidence.GrantBoundary.CompletionConstraint);
        var evidence = GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(
            intent,
            execution,
            seedReceipt.Evidence.GrantProfile,
            grantBoundary,
            seedReceipt.Evidence.GrantDependencyEvidenceHash,
            seedReceipt.Evidence.EffectiveAuthority,
            seedReceipt.Evidence.CapabilityAdmission,
            _now);
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
            [],
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(outcome).IsValid);
        store.StoreReadResult = new GovernedLoopAdmissionStoreReadResult(GovernedLoopAdmissionStoreReadStatus.Found, 2, outcome);
        store.GraphReadResult = new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ready, 2, artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        return new TestContext(store, artifact, binding, invocation, plan);
    }

    private static CustomLoopRunRecord Run(
        string runId,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialInvocationSnapshot? invocation,
        GovernedLoopSequentialPlan? plan = null)
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
            Frontier = binding is not null && plan is not null
                ? Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Initialize(
                    binding,
                    plan,
                    "event-trigger",
                    "event-trigger",
                    new string('a', 64),
                    _now).Frontier)
                : null,
        };
    }

    private sealed record TestContext(
        GovernedLoopAdmissionTestHarness Store,
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialAdapterBinding Binding,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopSequentialPlan Plan);

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
        public Action? BeforeResolve { get; init; }

        public Exception? Exception { get; init; }

        public int CallCount { get; private set; }

        public Task<GovernedLoopSequentialRunEvidence?> ResolveAsync(string runId, CancellationToken cancellationToken = default)
        {
            BeforeResolve?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
            {
                throw Exception;
            }

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
