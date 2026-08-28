using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Policies;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

/// <summary>Exercises Human Input admission through the canonical ordered-runner boundary.</summary>
public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Human_input_admission_atomically_persists_the_waiting_frontier_and_published_checkpoint_before_any_delivery()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        CustomLoopRunRecord? observedCheckpointCandidate = null;
        store.BeforeUpdate = (candidate, _) =>
        {
            if (candidate.HumanInputWaitingCheckpoints.Count != 1)
            {
                return;
            }

            observedCheckpointCandidate = candidate;
            var checkpoint = Assert.Single(candidate.HumanInputWaitingCheckpoints);
            var frontier = Assert.IsType<GovernedLoopFrontierPosture>(candidate.Frontier);
            var activation = frontier.Payload.Nodes[checkpoint.Binding.ActivationOrdinal];
            Assert.Equal(CustomLoopRunStatus.Waiting, candidate.Status);
            Assert.Equal(GovernedLoopFrontierStatus.Waiting, frontier.Payload.Status);
            Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, activation.Status);
            Assert.Equal(checkpoint.Binding.FrontierVersion, frontier.Payload.FrontierVersion);
            Assert.Equal(checkpoint.Binding.FrontierHash, frontier.Payload.ContentHash);
            Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, checkpoint.Posture);
            Assert.Equal(GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, Assert.Single(checkpoint.Evidence).Kind);
            Assert.Empty(executor.Requests);
            Assert.Empty(publisher.Requests);
        };

        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));
        var parked = await runtime.RunAsync(Request(context));

        Assert.True(parked.Status == CustomLoopOrderedRunStatus.Waiting, parked.Detail);
        Assert.NotNull(observedCheckpointCandidate);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        var checkpoint = Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(context.Anchor.AdapterBinding.ExecutionBinding.RunId, checkpoint.Binding.Execution.RunId);
        Assert.Equal(context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration, checkpoint.Binding.Execution.ExecutionGeneration);
        Assert.Equal(context.Anchor.AdapterBinding.ExecutionBinding.Revision, checkpoint.Binding.Execution.Revision);
        Assert.Equal(context.Anchor.AdapterBinding.AdmissionReceiptHash, checkpoint.Binding.AdmissionReceiptHash);
        Assert.True(ContextualRoleWorkspaceId.IsValid(context.Anchor.AdapterBinding.WorkspaceId));
        Assert.Equal(context.Anchor.AdapterBinding.WorkspaceId, checkpoint.Binding.WorkspaceId);
        Assert.Equal(context.Anchor.AdapterBinding.WorkspaceId, checkpoint.Request.Binding.WorkspaceId);
        Assert.Equal(context.Anchor.AdapterBinding.WorkspaceId, checkpoint.ResolvedPolicy.WorkspaceId);
        Assert.Equal("human-input", checkpoint.Binding.NodeId);
        Assert.Equal(context.Run.AdmissionActor, checkpoint.ResolvedPolicy.ActorId);
        Assert.Equal("timeout-policy-one@revision-one", checkpoint.ResolvedPolicy.TimeoutPolicy.Reference.ToString());
        Assert.Equal("failure-policy-one@revision-one", checkpoint.ResolvedPolicy.FailurePolicy.Reference.ToString());
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);

        var writesBeforeReplay = store.Writes.Count;
        var replay = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, replay.Status);
        Assert.Equal(writesBeforeReplay, store.Writes.Count);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
    }

    [Fact]
    public async Task Human_input_admission_reconciles_an_uncertain_post_commit_checkpoint_write_without_republication()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var threwAfterCommit = false;
        store.AfterUpdate = run =>
        {
            if (!threwAfterCommit && run.HumanInputWaitingCheckpoints.Count == 1)
            {
                threwAfterCommit = true;
                throw new InvalidOperationException("The checkpoint write committed before the client observed its acknowledgement.");
            }

            return Task.CompletedTask;
        };

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.True(threwAfterCommit);
        Assert.True(result.Status == CustomLoopOrderedRunStatus.Waiting, result.Detail);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Single(store.Writes, run => run.HumanInputWaitingCheckpoints.Count == 1);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_reconciles_a_conflict_when_the_exact_checkpoint_is_already_durable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run)
        {
            RawConflictSuccessorFactory = (_, candidate) => candidate.HumanInputWaitingCheckpoints.Count == 1 ? candidate : null,
        };
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Waiting, result.Detail);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Single(store.Writes, run => run.HumanInputWaitingCheckpoints.Count == 1);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_does_not_assume_an_uncertain_checkpoint_write_when_exact_reconciliation_is_unavailable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run)
        {
            GetException = new IOException("The durable checkpoint cannot be re-read."),
        };
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        store.AfterUpdate = run => run.HumanInputWaitingCheckpoints.Count == 1
            ? throw new IOException("The checkpoint acknowledgement was lost after persistence.")
            : Task.CompletedTask;

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_reconciles_a_durable_checkpoint_superseded_by_cancellation_without_delivery()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run)
        {
            RawConflictSuccessorFactory = (_, candidate) => candidate.HumanInputWaitingCheckpoints.Count == 1
                ? CreatePureCancellationTerminalSuccessor(candidate, includePause: false, includeTerminalWarning: false)
                : null,
        };
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_never_persists_a_frontier_before_its_trusted_policy_resolution_time()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var resolutionAtUtc = _now.AddMinutes(1);

        var result = await Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context, resolutionAtUtc),
            new FixedTimeProvider(_now)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, result.Status);
        Assert.Equal(resolutionAtUtc, store.Current.UpdatedAtUtc);
        var checkpoint = Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(resolutionAtUtc, checkpoint.ResolvedPolicy.ResolvedAtUtc);
        Assert.Equal(resolutionAtUtc, checkpoint.Request.Timing.RequestedAtUtc);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_the_exact_policy_authority_is_unavailable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, null).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_input_admission_fails_closed_for_missing_or_stale_exact_policy_revisions(bool staleRevision)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var source = new HumanInputPolicyResolutionTestSource();
        if (staleRevision)
        {
            var binding = context.Anchor.AdapterBinding;
            source.Results.Add(
                new HumanInputPolicyReference("timeout-policy-one", "revision-one"),
                new HumanInputPolicySourceReadResult(
                    HumanInputPolicySourceReadStatus.Ready,
                    HumanInputPolicyArtifactHash.Apply(TimeoutPolicy(binding, context.Run.AdmissionActor) with
                    {
                        RevisionId = "revision-two",
                        ContentHash = string.Empty,
                    }),
                    1));
            source.Results.Add(
                new HumanInputPolicyReference("failure-policy-one", "revision-one"),
                new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, FailurePolicy(binding, context.Run.AdmissionActor), 1));
        }

        var result = await Runtime(context, store, new QueueExecutor(), new RecordingPublisher(), new HumanInputPolicyResolutionService(source, new FixedTimeProvider(_now))).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(staleRevision ? "human_input_policy_resolution_divergent" : "human_input_policy_resolution_notfound", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.DoesNotContain(store.Writes, run => run.HumanInputWaitingCheckpoints.Count > 0);
    }

    [Fact]
    public async Task Restart_recovery_preserves_an_exact_pending_human_input_checkpoint_without_delivery_or_continuation()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var retained = store.Current;
        var checkpoint = Assert.Single(retained.HumanInputWaitingCheckpoints);
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(retained.Frontier);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.True(recovery.Status == CustomLoopRecoveryStatus.Unchanged, recovery.Detail);
        Assert.Same(retained, store.Current);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(checkpoint.CheckpointHash, Assert.Single(store.Current.HumanInputWaitingCheckpoints).CheckpointHash);
        Assert.Equal(frontier.Payload.FrontierVersion, store.Current.Frontier!.Payload.FrontierVersion);
        Assert.Equal(frontier.Payload.ContentHash, store.Current.Frontier.Payload.ContentHash);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_recovery_fails_closed_for_malformed_or_future_divergent_pending_human_input_checkpoint(bool futureDivergence)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var malformed = MutateHumanInputWaitingCheckpoint(store.Current, futureDivergence);
        store.ReplaceCurrent(malformed, validate: false);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.False(CustomLoopRunValidator.Validate(store.Current).IsValid);
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, recovery.Status);
        Assert.Same(malformed, store.Current);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_preserves_parallel_pending_human_input_checkpoints_across_aggregate_frontier_evolution()
    {
        var parked = await ParkParallelHumanInputAsync();
        var retained = parked.Store.Current;
        var checkpoints = retained.HumanInputWaitingCheckpoints.OrderBy(checkpoint => checkpoint.Binding.FrontierVersion).ToArray();
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(retained.Frontier);

        Assert.Equal(2, checkpoints.Length);
        Assert.True(checkpoints[0].Binding.FrontierVersion < checkpoints[1].Binding.FrontierVersion);
        Assert.True(checkpoints[1].Binding.FrontierVersion == frontier.Payload.FrontierVersion);
        Assert.NotEqual(frontier.Payload.ContentHash, checkpoints[0].Binding.FrontierHash);
        var inferenceDispatches = parked.Executor.Requests.Count;
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            parked.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(_now.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.True(recovery.Status == CustomLoopRecoveryStatus.Unchanged, recovery.Detail);
        Assert.Same(retained, parked.Store.Current);
        Assert.Equal(checkpoints.Select(checkpoint => checkpoint.CheckpointHash), parked.Store.Current.HumanInputWaitingCheckpoints.Select(checkpoint => checkpoint.CheckpointHash));
        Assert.Equal(inferenceDispatches, parked.Executor.Requests.Count);
        Assert.Empty(parked.Publisher.Requests);
    }

    [Fact]
    public async Task Pause_and_resume_rearm_an_exact_pending_human_input_checkpoint_without_ordered_dispatch()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var operationStore = new FakeControlOperationStore();
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var service = new CustomLoopLifecycleService(
            store,
            operationStore,
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "pause-durable-human-input-before-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var checkpoint = Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(store.Current.Frontier);

        var request = new CustomLoopResumeRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "resume-durable-human-input",
            AuditSchema.Actors.Web);
        var resumed = await service.ResumeAsync(request);
        var replayed = await service.ResumeAsync(request);

        Assert.Equal(CustomLoopControlStatus.Waiting, resumed.Status);
        Assert.Equal(CustomLoopControlStatus.Waiting, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Waiting, store.Current.Frontier!.Payload.Status);
        Assert.Equal(checkpoint.CheckpointHash, Assert.Single(store.Current.HumanInputWaitingCheckpoints).CheckpointHash);
        Assert.Equal(frontier.Payload.FrontierVersion, store.Current.Frontier.Payload.FrontierVersion);
        Assert.Equal(frontier.Payload.ContentHash, store.Current.Frontier.Payload.ContentHash);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_rejects_a_malformed_or_future_divergent_pending_human_input_checkpoint_without_ordered_dispatch(bool futureDivergence)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var service = new CustomLoopLifecycleService(
            store,
            new FakeControlOperationStore(),
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            "pause-human-input-before-invalid-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var malformed = MutateHumanInputWaitingCheckpoint(store.Current, futureDivergence);
        store.ReplaceCurrent(malformed, validate: false);
        var writesBeforeResume = store.Writes.Count;

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            futureDivergence ? "resume-future-human-input" : "resume-malformed-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.InvalidState, result.Status);
        Assert.Same(malformed, store.Current);
        Assert.Equal(writesBeforeResume, store.Writes.Count);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Pause_and_resume_rearm_parallel_pending_human_input_checkpoints_across_aggregate_frontier_evolution()
    {
        var parked = await ParkParallelHumanInputAsync();
        var operationStore = new FakeControlOperationStore();
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(parked.Store.Current);
        var service = new CustomLoopLifecycleService(
            parked.Store,
            operationStore,
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            new FixedTimeProvider(_now.AddSeconds(1)));

        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            parked.Store.Current.Id,
            parked.Store.Current.LifecycleVersion,
            "pause-parallel-human-input-before-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var checkpoints = parked.Store.Current.HumanInputWaitingCheckpoints.OrderBy(checkpoint => checkpoint.Binding.FrontierVersion).ToArray();
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(parked.Store.Current.Frontier);
        Assert.NotEqual(frontier.Payload.ContentHash, checkpoints[0].Binding.FrontierHash);
        var inferenceDispatches = parked.Executor.Requests.Count;

        var resumed = await service.ResumeAsync(new CustomLoopResumeRequest(
            parked.Store.Current.Id,
            parked.Store.Current.LifecycleVersion,
            "resume-parallel-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.Waiting, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, parked.Store.Current.Status);
        Assert.Equal(checkpoints.Select(checkpoint => checkpoint.CheckpointHash), parked.Store.Current.HumanInputWaitingCheckpoints.Select(checkpoint => checkpoint.CheckpointHash));
        Assert.Equal(frontier.Payload.FrontierVersion, parked.Store.Current.Frontier!.Payload.FrontierVersion);
        Assert.Equal(frontier.Payload.ContentHash, parked.Store.Current.Frontier.Payload.ContentHash);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Equal(inferenceDispatches, parked.Executor.Requests.Count);
        Assert.Empty(parked.Publisher.Requests);
        Assert.True(CustomLoopRunValidator.Validate(parked.Store.Current).IsValid);
    }

    private static CustomLoopRunRecord MutateHumanInputWaitingCheckpoint(CustomLoopRunRecord run, bool futureDivergence)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        if (!futureDivergence)
        {
            return run with { HumanInputWaitingCheckpoints = [checkpoint with { CheckpointHash = new string('F', 64) }] };
        }

        var futureBinding = checkpoint.Binding with
        {
            FrontierVersion = checkpoint.Binding.FrontierVersion + 1,
            FrontierHash = new string('a', 64),
        };
        var futureCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            futureBinding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            checkpoint.Posture,
            checkpoint.Evidence,
            string.Empty));
        return run with { HumanInputWaitingCheckpoints = [futureCheckpoint] };
    }

    private static async Task<(FakeRunStore Store, QueueExecutor Executor, RecordingPublisher Publisher)> ParkParallelHumanInputAsync()
    {
        var context = await ParallelHumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("parallel Human Input source"));
        var publisher = new RecordingPublisher();

        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(2, store.Current.HumanInputWaitingCheckpoints.Count);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        return (store, executor, publisher);
    }

    private static async Task<SequentialTestContext> HumanInputContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> ParallelHumanInputContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = ParallelHumanInputArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static GovernedLoopSequentialOrderedRuntimeAdapter Runtime(
        SequentialTestContext context,
        FakeRunStore store,
        QueueExecutor executor,
        RecordingPublisher publisher,
        HumanInputPolicyResolutionService? resolver,
        TimeProvider? runnerTimeProvider = null)
    {
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        return new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, publisher, timeProvider: runnerTimeProvider ?? new FixedTimeProvider(_now), humanInputPolicyResolutionService: resolver),
            evidence,
            evidence);
    }

    private static GovernedLoopSequentialOrderedRunRequest Request(SequentialTestContext context)
        => new(GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web);

    private static GovernedLoopGraphRevisionArtifact HumanInputArtifact(ContextualRoleRevisionPin role)
    {
        var configuration = HumanInputConfiguration();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-exit", "human-input", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            role,
            bindings: [new GovernedLoopBindingDefinition("response-to-exit", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact ParallelHumanInputArtifact(ContextualRoleRevisionPin role)
    {
        var source = GovernedLoopSequentialApplicationTestFixture.ParallelAllJoinArtifact(role).Graph;
        var configuration = HumanInputConfiguration();
        var nodes = source.Nodes.Select(node => node.Id is "branch-a" or "branch-b"
            ? new GovernedLoopNodeDefinition(
                node.Id,
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>(),
                null,
                null,
                null,
                configuration)
            : node)
            .ToArray();
        var bindings = source.Bindings
            .Where(binding => binding.ToNodeId is not "branch-a" and not "branch-b")
            .ToArray();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            source.ControlEdges,
            source.TerminalNodeIds,
            role,
            bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private static GovernedLoopHumanInputNodeConfiguration HumanInputConfiguration()
        => new(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "text",
            "Collect bounded untrusted data.",
            "Provide the requested bounded response.",
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one@revision-one",
            "failure-policy-one@revision-one");

    private static HumanInputPolicyResolutionService HumanInputPolicyResolver(SequentialTestContext context, DateTimeOffset? resolvedAtUtc = null)
    {
        var source = new HumanInputPolicyResolutionTestSource();
        var binding = context.Anchor.AdapterBinding;
        var timeout = TimeoutPolicy(binding, context.Run.AdmissionActor);
        var failure = FailurePolicy(binding, context.Run.AdmissionActor);
        source.Results.Add(timeout.Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, timeout, 1));
        source.Results.Add(failure.Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, failure, 1));
        return new HumanInputPolicyResolutionService(source, new FixedTimeProvider(resolvedAtUtc ?? _now));
    }

    private static HumanInputPolicyArtifact TimeoutPolicy(GovernedLoopSequentialAdapterBinding binding, string actorId)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            "timeout-policy-one",
            "revision-one",
            HumanInputPolicyKind.ResponseWindow,
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            actorId,
            60_000,
            HumanInputTerminalDisposition.Unknown,
            string.Empty));

    private static HumanInputPolicyArtifact FailurePolicy(GovernedLoopSequentialAdapterBinding binding, string actorId)
        => HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(
            HumanInputPolicyArtifact.CurrentSchemaVersion,
            "failure-policy-one",
            "revision-one",
            HumanInputPolicyKind.DeadlineDisposition,
            binding.WorkspaceId,
            binding.ExecutionBinding.Revision.GraphId,
            actorId,
            null,
            HumanInputTerminalDisposition.Expired,
            string.Empty));
}
