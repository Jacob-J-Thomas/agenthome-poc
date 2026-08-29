using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Publication.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Failures;
using EmbodySense.Core.Application.Loops.Failures.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Policies;
using EmbodySense.Core.Application.Tests.HumanInput.Continuations;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.HumanInput.Responses;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.PureNodes;
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
        var requestPublication = new RecordingHumanInputRequestPublicationService();
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
            Assert.Empty(requestPublication.Requests);
        };

        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context), humanInputRequestPublicationService: requestPublication);
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
        var publishedRequest = Assert.Single(requestPublication.Requests);
        Assert.Equal(store.Current.Id, publishedRequest.RunId);
        Assert.Equal(checkpoint.Binding.CheckpointId, publishedRequest.CheckpointId);
        Assert.Equal(checkpoint.CheckpointHash, publishedRequest.CheckpointHash);

        var writesBeforeReplay = store.Writes.Count;
        var replay = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, replay.Status);
        Assert.Equal(writesBeforeReplay, store.Writes.Count);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Single(requestPublication.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_before_checkpoint_when_publication_port_is_missing()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context),
            composeHumanInputRequestPublicationService: false).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_request_publication_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Durable_human_input_checkpoint_publication_creates_once_and_replays_after_grant_expiry()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var publication = Publication(context, runs, lifecycle);
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: publication).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var request = new HumanInputRequestPublicationRequest(runs.Current.Id, checkpoint.Binding.CheckpointId, checkpoint.CheckpointHash);

        context.Store.GrantResolution = context.Store.GrantResolution with
        {
            Status = AuthorityGrantResolutionStatus.Unavailable,
            Grant = null,
            DependencyEvidenceHash = string.Empty,
            EvaluatedAtUtc = default,
        };
        var replayed = await publication.PublishAsync(request);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Replayed, replayed.Status);
        var commit = Assert.Single(lifecycle.Commits);
        Assert.NotNull(commit.Mutation.RequestToAppend);
        Assert.Equal(checkpoint.Request.RequestId, commit.Mutation.RequestToAppend.RequestId);
        Assert.Equal(checkpoint.Request.RequestVersionId, commit.Mutation.RequestToAppend.RequestVersionId);
        Assert.Equal(checkpoint.Request.Binding, commit.Mutation.RequestToAppend.Binding);
        Assert.Equal(checkpoint.Request.RequestHash, commit.Mutation.RequestToAppend.RequestHash);
        Assert.Equal(runs.Current.AdmissionActor, commit.Mutation.Operation.ActorId.Value);
        Assert.Equal(context.Anchor.AdapterBinding.AdmissionReceipt.Intent.AuthorityGrant, commit.Mutation.Operation.GrantReference);
        Assert.Equal(context.Anchor.AdapterBinding.AdmissionReceiptHash, commit.Mutation.Operation.AuthorityEvidenceHash);
        Assert.Equal(CancellationToken.None, commit.CancellationToken);
        Assert.Equal(2, lifecycle.MutationReads.Count);
        Assert.All(lifecycle.MutationReads, read =>
        {
            Assert.Equal(commit.Mutation.Operation.OperationId, read.OperationId);
            Assert.Equal(commit.Mutation.Operation.RequestHash, read.RequestHash);
        });
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, snapshot.Head.Status);
        Assert.Single(snapshot.RequestVersions);
        Assert.Single(snapshot.Operations);
    }

    [Fact]
    public async Task Publication_health_probe_distinguishes_a_clean_empty_ledger_from_unavailable_or_ambiguous_evidence()
    {
        var context = await HumanInputPublicationContextAsync();
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var publication = Publication(context, new FakeRunStore(context.Run), lifecycle);

        var healthy = await publication.ProbeAsync();
        lifecycle.ReadOverride = (_, _) => Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
            HumanInputRequestLifecycleStoreReadStatus.Unavailable,
            0,
            null,
            null,
            null));
        var unavailable = await publication.ProbeAsync();
        lifecycle.ReadOverride = (_, _) => Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
            HumanInputRequestLifecycleStoreReadStatus.Ambiguous,
            0,
            null,
            null,
            null));
        var corrupt = await publication.ProbeAsync();
        lifecycle.ReadOverride = (_, _) => Task.FromException<HumanInputRequestLifecycleStoreReadResult>(new IOException("simulated publication-ledger outage"));
        var faulted = await publication.ProbeAsync();

        Assert.Equal(HumanInputRequestPublicationHealthStatus.Ready, healthy.Status);
        Assert.Equal(HumanInputRequestPublicationHealthStatus.Unavailable, unavailable.Status);
        Assert.Equal(HumanInputRequestPublicationHealthStatus.Corrupt, corrupt.Status);
        Assert.Equal(HumanInputRequestPublicationHealthStatus.Unavailable, faulted.Status);
        Assert.All(lifecycle.Reads, read => Assert.Equal("human-input-publication-health", read.RequestId));
    }

    [Fact]
    public async Task Parallel_active_human_input_checkpoint_is_published_before_the_other_ready_branch_advances()
    {
        var context = await ParallelHumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();

        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(Result("parallel Human Input source")),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var checkpoints = runs.Current.HumanInputWaitingCheckpoints.OrderBy(checkpoint => checkpoint.Binding.FrontierVersion).ToArray();
        Assert.Equal(2, checkpoints.Length);
        Assert.Equal(
            checkpoints.Select(checkpoint => checkpoint.Request.RequestId),
            lifecycle.Commits.Select(commit => commit.Mutation.Operation.TargetRequestId));
        Assert.All(checkpoints, checkpoint =>
        {
            var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));
            Assert.Equal(HumanInputRequestLifecycleStatus.Pending, snapshot.Head.Status);
            Assert.Single(snapshot.RequestVersions);
            Assert.Single(snapshot.Operations);
        });
    }

    [Fact]
    public async Task Parallel_active_human_input_checkpoint_keeps_the_independent_ready_branch_running_when_publication_is_unavailable()
    {
        var context = await ParallelHumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var publication = new RecordingHumanInputRequestPublicationService(HumanInputRequestPublicationStatus.Unavailable);

        var result = await Runtime(
            context,
            runs,
            new QueueExecutor(Result("parallel Human Input source")),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: publication).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, result.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, runs.Current.Status);
        var checkpoints = runs.Current.HumanInputWaitingCheckpoints.OrderBy(checkpoint => checkpoint.Binding.FrontierVersion).ToArray();
        Assert.Equal(2, checkpoints.Length);
        Assert.Equal(checkpoints.Select(checkpoint => checkpoint.Binding.CheckpointId), publication.Requests.Select(request => request.CheckpointId));
        Assert.Equal(2, publication.Requests.Count);
    }

    [Fact]
    public async Task Ambiguous_post_commit_request_publication_recovers_exactly_once_before_the_runner_returns_waiting()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        lifecycle.CommitOverride = (mutation, _) =>
        {
            lifecycle.CommitOverride = null;
            lifecycle.CommitDurably(mutation);
            throw new IOException("simulated lost request-publication acknowledgement");
        };
        var publication = Publication(context, runs, lifecycle);

        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: publication).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        context.Store.GrantResolution = context.Store.GrantResolution with
        {
            Status = AuthorityGrantResolutionStatus.Unavailable,
            Grant = null,
            DependencyEvidenceHash = string.Empty,
            EvaluatedAtUtc = default,
        };

        var replayed = await publication.PublishAsync(new HumanInputRequestPublicationRequest(
            runs.Current.Id,
            checkpoint.Binding.CheckpointId,
            checkpoint.CheckpointHash));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Replayed, replayed.Status);
        Assert.Single(lifecycle.Commits);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, snapshot.Head.Status);
        Assert.Single(snapshot.RequestVersions);
        Assert.Single(snapshot.Operations);
    }

    [Theory]
    [InlineData(AuthorityGrantResolutionStatus.NotFound)]
    [InlineData(AuthorityGrantResolutionStatus.Expired)]
    [InlineData(AuthorityGrantResolutionStatus.CeilingExceeded)]
    [InlineData(AuthorityGrantResolutionStatus.Unavailable)]
    public async Task First_request_publication_fails_closed_when_the_exact_admission_grant_is_not_active(
        AuthorityGrantResolutionStatus grantStatus)
    {
        var context = await HumanInputPublicationContextAsync();
        context.Store.GrantResolution = context.Store.GrantResolution with
        {
            Status = grantStatus,
            Grant = null,
            EffectiveCeiling = AuthorityCeilingIntersection.EmptyCeiling(),
            DependencyEvidenceHash = string.Empty,
            EvaluatedAtUtc = default,
        };
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();

        var result = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, result.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, runs.Current.Status);
        Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(lifecycle.Commits);
    }

    [Fact]
    public async Task Durable_nonpending_human_input_checkpoint_without_its_exact_create_is_corrupt()
    {
        var context = await HumanInputContextAsync();
        var runs = new FakeRunStore(context.Run);
        var parked = await Runtime(context, runs, new QueueExecutor(), new RecordingPublisher(), HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var answered = AnswerHumanInputCheckpointForOrderedReentry(runs.Current, _now.AddMinutes(1));
        runs.ReplaceCurrent(answered);
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);

        var result = await Publication(context, runs, new InMemoryHumanInputRequestLifecycleStore()).PublishAsync(
            new HumanInputRequestPublicationRequest(runs.Current.Id, checkpoint.Binding.CheckpointId, checkpoint.CheckpointHash));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task Human_input_checkpoint_publication_maps_stale_corrupt_and_unavailable_canonical_reads_closed()
    {
        var context = await HumanInputContextAsync();
        var runs = new FakeRunStore(context.Run);
        var parked = await Runtime(context, runs, new QueueExecutor(), new RecordingPublisher(), HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var publication = Publication(context, runs, new InMemoryHumanInputRequestLifecycleStore());
        var request = new HumanInputRequestPublicationRequest(runs.Current.Id, checkpoint.Binding.CheckpointId, checkpoint.CheckpointHash);

        var stale = await publication.PublishAsync(request with { CheckpointHash = new string('b', 64) });
        runs.ReplaceCurrent(MutateHumanInputWaitingCheckpoint(runs.Current, futureDivergence: false), validate: false);
        var corrupt = await publication.PublishAsync(request);
        runs.GetException = new IOException("simulated canonical run read failure");
        var unavailable = await publication.PublishAsync(request);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Stale, stale.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Corrupt, corrupt.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Unavailable, unavailable.Status);
    }

    [Fact]
    public async Task Cancellation_that_is_durable_before_first_request_publication_exposes_no_request_lifecycle()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var parked = await Runtime(context, runs, new QueueExecutor(), new RecordingPublisher(), HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        runs.ReplaceCurrent(CreatePureCancellationTerminalSuccessor(runs.Current, includePause: false, includeTerminalWarning: false));
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();

        var result = await Publication(context, runs, lifecycle).PublishAsync(new HumanInputRequestPublicationRequest(
            runs.Current.Id,
            checkpoint.Binding.CheckpointId,
            checkpoint.CheckpointHash));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.Equal(HumanInputRequestPublicationStatus.Corrupt, result.Status);
        Assert.Empty(lifecycle.Commits);
    }

    [Fact]
    public async Task Cancellation_before_human_input_publication_retires_the_checkpoint_without_creating_or_continuing()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(context, runs, new QueueExecutor(), new RecordingPublisher(), HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var controls = new FakeControlOperationStore();
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, controls);

        var cancelled = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-before-human-input-publication",
            AuditSchema.Actors.Web));
        var publication = await Publication(context, runs, lifecycle).PublishAsync(new HumanInputRequestPublicationRequest(
            runs.Current.Id,
            checkpoint.Binding.CheckpointId,
            checkpoint.CheckpointHash));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, cancelled.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(HumanInputRequestPublicationStatus.Corrupt, publication.Status);
        Assert.Empty(lifecycle.Commits);
    }

    [Fact]
    public async Task Cancellation_after_human_input_publication_commits_one_deterministic_cancel_and_replays_without_duplication()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var controls = new FakeControlOperationStore();
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, controls);
        var request = new CustomLoopCancelRequest(runs.Current.Id, runs.Current.LifecycleVersion, "cancel-after-human-input-publication", AuditSchema.Actors.Web);

        var cancelled = await cancellation.CancelAsync(request);
        var replayed = await cancellation.CancelAsync(request);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, cancelled.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, snapshot.Head.Status);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            lifecycle.Commits.Select(commit => commit.Mutation.Operation.Kind));
        Assert.Single(lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("malformed")]
    [InlineData("ambiguous")]
    [InlineData("divergent")]
    public async Task Cancellation_fails_closed_when_pending_request_read_evidence_is_not_one_exact_canonical_snapshot(string readFailure)
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        var published = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(scenario.Lifecycle.Snapshot(scenario.Checkpoint.Request.RequestId));
        scenario.Lifecycle.ReadOverride = (_, _) => readFailure switch
        {
            "throw" => Task.FromException<HumanInputRequestLifecycleStoreReadResult>(new IOException("simulated Human Input lifecycle read outage")),
            "malformed" => Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                -1,
                null,
                null,
                null)),
            "ambiguous" => Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ambiguous,
                1,
                null,
                null,
                null)),
            "divergent" => Task.FromResult(new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                1,
                new HumanInputRequestLifecycleStoreSnapshot(published.Head, [], published.Operations),
                null,
                null)),
            _ => throw new InvalidOperationException("The test read-failure case is unsupported."),
        };
        var cancellation = HumanInputCancellationLifecycle(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore());

        var result = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            scenario.Runs.Current.Id,
            scenario.Runs.Current.LifecycleVersion,
            "cancel-fails-closed-request-read-" + readFailure,
            AuditSchema.Actors.Web));

        Assert.NotEqual(CustomLoopControlStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        var retained = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(scenario.Lifecycle.Snapshot(scenario.Checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, retained.Head.Status);
        Assert.Equal([HumanInputRequestLifecycleOperationKind.Create], retained.Operations.Select(operation => operation.Kind));
        Assert.DoesNotContain(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleStoreCommitStatus.OperationConflict)]
    [InlineData(HumanInputRequestLifecycleStoreCommitStatus.Unavailable)]
    [InlineData(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous)]
    public async Task Cancellation_fails_closed_when_the_deterministic_child_cancel_cannot_prove_a_terminal_request(
        HumanInputRequestLifecycleStoreCommitStatus commitStatus)
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        scenario.Lifecycle.CommitOverride = (mutation, _) => Task.FromResult(new HumanInputRequestLifecycleStoreCommitResult(
            commitStatus,
            mutation.ExpectedStoreGeneration,
            null,
            null,
            null));
        var cancellation = HumanInputCancellationLifecycle(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore());

        var result = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            scenario.Runs.Current.Id,
            scenario.Runs.Current.LifecycleVersion,
            "cancel-fails-closed-child-" + commitStatus.ToString().ToLowerInvariant(),
            AuditSchema.Actors.Web));

        Assert.NotEqual(CustomLoopControlStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        var retained = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(scenario.Lifecycle.Snapshot(scenario.Checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, retained.Head.Status);
        Assert.Equal([HumanInputRequestLifecycleOperationKind.Create], retained.Operations.Select(operation => operation.Kind));
        Assert.Single(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Cancellation_preserves_the_committed_child_cancel_when_its_post_commit_readback_is_unavailable()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        var originalRead = await scenario.Lifecycle.ReadAsync(scenario.Checkpoint.Request.RequestId);
        var readCount = 0;
        scenario.Lifecycle.ReadOverride = (_, _) => Interlocked.Increment(ref readCount) == 1
            ? Task.FromResult(originalRead)
            : Task.FromException<HumanInputRequestLifecycleStoreReadResult>(new IOException("simulated child Cancel proof readback outage"));
        var cancellation = HumanInputCancellationLifecycle(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore());

        var result = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            scenario.Runs.Current.Id,
            scenario.Runs.Current.LifecycleVersion,
            "cancel-fails-closed-post-commit-readback",
            AuditSchema.Actors.Web));

        Assert.NotEqual(CustomLoopControlStatus.Cancelled, result.Status);
        Assert.Equal(2, readCount);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        var retained = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(scenario.Lifecycle.Snapshot(scenario.Checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, retained.Head.Status);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            retained.Operations.Select(operation => operation.Kind));
    }

    [Theory]
    [InlineData(CustomLoopRunStoreStatus.Conflict)]
    [InlineData(CustomLoopRunStoreStatus.TerminalImmutable)]
    [InlineData(CustomLoopRunStoreStatus.NotFound)]
    public async Task Cancellation_preserves_the_child_cancel_when_checkpoint_retirement_cannot_commit(
        CustomLoopRunStoreStatus retirementStatus)
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        scenario.Runs.AfterUpdate = run =>
        {
            if (run.Status == CustomLoopRunStatus.CancelRequested
                && Assert.Single(run.HumanInputWaitingCheckpoints).Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending)
            {
                scenario.Runs.UpdateResultOverride = CheckpointRetirementResult(retirementStatus, scenario.Runs.Current);
            }

            return Task.CompletedTask;
        };
        var cancellation = HumanInputCancellationLifecycle(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore());
        var request = new CustomLoopCancelRequest(
            scenario.Runs.Current.Id,
            scenario.Runs.Current.LifecycleVersion,
            "cancel-fails-closed-retirement-" + retirementStatus.ToString().ToLowerInvariant(),
            AuditSchema.Actors.Web);

        var result = await cancellation.CancelAsync(request);

        Assert.NotEqual(CustomLoopControlStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        var retained = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(scenario.Lifecycle.Snapshot(scenario.Checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, retained.Head.Status);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            retained.Operations.Select(operation => operation.Kind));

        scenario.Runs.UpdateResultOverride = null;
        var replayed = await cancellation.CancelAsync(request);

        Assert.Equal(CustomLoopControlStatus.Cancelled, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Single(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Human_input_cancellation_convergence_returns_unavailable_when_the_shared_authority_boundary_fails()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        var authority = new StubCapabilityAuthorityTransaction { Exception = new IOException("simulated authority boundary failure") };
        var convergence = HumanInputCancellationConvergence(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore(), authority);

        var result = await convergence.ConvergeAsync(scenario.Runs.Current, "cancel-convergence-authority-failure");

        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Unavailable, result.Status);
        Assert.Equal(scenario.Runs.Current, result.Run);
        Assert.Equal(1, authority.Executions);
        Assert.DoesNotContain(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Human_input_cancellation_convergence_propagates_caller_cancellation_from_the_shared_authority_boundary()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var authority = new StubCapabilityAuthorityTransaction { Exception = new OperationCanceledException(callerCancellation.Token) };
        var convergence = HumanInputCancellationConvergence(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore(), authority);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => convergence.ConvergeAsync(
            scenario.Runs.Current,
            "cancel-convergence-authority-cancelled",
            callerCancellation.Token));

        Assert.Equal(1, authority.Executions);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Runs.Current.Status);
        Assert.DoesNotContain(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Human_input_cancellation_convergence_propagates_caller_cancellation_from_the_canonical_control_and_run_reads()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        var controls = new FakeControlOperationStore();
        var authority = new StubCapabilityAuthorityTransaction();
        var convergence = HumanInputCancellationConvergence(scenario.Context, scenario.Runs, scenario.Lifecycle, controls, authority);
        using var controlCancellation = new CancellationTokenSource();
        controlCancellation.Cancel();
        controls.GetException = new OperationCanceledException(controlCancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => convergence.ConvergeAsync(
            scenario.Runs.Current,
            "cancel-convergence-control-read-cancelled",
            controlCancellation.Token));

        controls.GetException = null;
        var operation = PendingCancellationControl(scenario.Runs.Current, "cancel-convergence-run-read-cancelled");
        await controls.BeginAsync(operation);
        using var runCancellation = new CancellationTokenSource();
        runCancellation.Cancel();
        scenario.Runs.GetException = new OperationCanceledException(runCancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => convergence.ConvergeAsync(
            scenario.Runs.Current,
            operation.OperationId,
            runCancellation.Token));

        scenario.Runs.GetException = null;
        var corrupt = await convergence.ConvergeAsync(scenario.Runs.Current, operation.OperationId);

        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, corrupt.Status);
        Assert.Equal(scenario.Runs.Current, corrupt.Run);
        Assert.Equal(3, authority.Executions);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Runs.Current.Status);
        Assert.DoesNotContain(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Human_input_cancellation_convergence_propagates_caller_cancellation_from_the_canonical_request_read()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        var controls = new FakeControlOperationStore();
        scenario.Lifecycle.ReadOverride = (_, _) => Task.FromException<HumanInputRequestLifecycleStoreReadResult>(new IOException("retain CancelRequested before request-read cancellation"));
        var cancellation = HumanInputCancellationLifecycle(scenario.Context, scenario.Runs, scenario.Lifecycle, controls);
        var parent = new CustomLoopCancelRequest(
            scenario.Runs.Current.Id,
            scenario.Runs.Current.LifecycleVersion,
            "cancel-convergence-request-read-cancelled",
            AuditSchema.Actors.Web);

        var retained = await cancellation.CancelAsync(parent);

        Assert.NotEqual(CustomLoopControlStatus.Cancelled, retained.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        var writesBeforeDirectConvergence = scenario.Runs.Writes.Count;
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();
        scenario.Lifecycle.ReadOverride = (_, _) => Task.FromException<HumanInputRequestLifecycleStoreReadResult>(new OperationCanceledException(requestCancellation.Token));
        var authority = new StubCapabilityAuthorityTransaction();
        var convergence = HumanInputCancellationConvergence(scenario.Context, scenario.Runs, scenario.Lifecycle, controls, authority);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => convergence.ConvergeAsync(
            scenario.Runs.Current,
            parent.OperationId,
            requestCancellation.Token));

        Assert.Equal(1, authority.Executions);
        Assert.Equal(writesBeforeDirectConvergence, scenario.Runs.Writes.Count);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.DoesNotContain(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Human_input_cancellation_convergence_fails_closed_for_invalid_control_and_run_read_evidence()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        var controls = new FakeControlOperationStore();
        var authority = new StubCapabilityAuthorityTransaction();
        var convergence = HumanInputCancellationConvergence(scenario.Context, scenario.Runs, scenario.Lifecycle, controls, authority);

        var invalidInput = await convergence.ConvergeAsync(scenario.Runs.Current, string.Empty);
        var missingControl = await convergence.ConvergeAsync(scenario.Runs.Current, "cancel-convergence-missing-control");
        controls.GetException = new IOException("simulated control receipt read failure");
        var unavailableControl = await convergence.ConvergeAsync(scenario.Runs.Current, "cancel-convergence-missing-control");
        controls.GetException = null;

        var operation = PendingCancellationControl(scenario.Runs.Current, "cancel-convergence-run-read");
        await controls.BeginAsync(operation);
        scenario.Runs.GetException = new IOException("simulated canonical run read failure");
        var unavailableRun = await convergence.ConvergeAsync(scenario.Runs.Current, operation.OperationId);
        scenario.Runs.GetException = null;
        scenario.Runs.ReturnMissing = true;
        var missingRun = await convergence.ConvergeAsync(scenario.Runs.Current, operation.OperationId);

        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, invalidInput.Status);
        Assert.Equal(scenario.Runs.Current, invalidInput.Run);
        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, missingControl.Status);
        Assert.Null(missingControl.Run);
        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Corrupt, unavailableControl.Status);
        Assert.Null(unavailableControl.Run);
        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Unavailable, unavailableRun.Status);
        Assert.Null(unavailableRun.Run);
        Assert.Equal(CustomLoopHumanInputCancellationConvergenceStatus.Unavailable, missingRun.Status);
        Assert.Null(missingRun.Run);
        Assert.Equal(4, authority.Executions);
        Assert.DoesNotContain(scenario.Lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
    }

    [Fact]
    public async Task Cancellation_preserves_the_child_cancel_when_the_retirement_timestamp_exceeds_the_request_window()
    {
        var scenario = await PublishedHumanInputCancellationAsync();
        scenario.Runs.AfterUpdate = run =>
        {
            if (run.Status == CustomLoopRunStatus.CancelRequested
                && Assert.Single(run.HumanInputWaitingCheckpoints).Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending)
            {
                scenario.Runs.ReplaceCurrent(run with { UpdatedAtUtc = scenario.Checkpoint.Request.Timing.ExpiresAtUtc.AddTicks(1) });
            }

            return Task.CompletedTask;
        };
        var cancellation = HumanInputCancellationLifecycle(scenario.Context, scenario.Runs, scenario.Lifecycle, new FakeControlOperationStore());

        var result = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            scenario.Runs.Current.Id,
            scenario.Runs.Current.LifecycleVersion,
            "cancel-fails-closed-retirement-window",
            AuditSchema.Actors.Web));

        Assert.NotEqual(CustomLoopControlStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        var retained = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(scenario.Lifecycle.Snapshot(scenario.Checkpoint.Request.RequestId));
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, retained.Head.Status);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            retained.Operations.Select(operation => operation.Kind));
    }

    [Fact]
    public async Task Cancellation_replays_exactly_after_request_cancel_commits_but_its_acknowledgement_is_lost()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var lostAcknowledgement = false;
        lifecycle.CommitOverride = (mutation, _) =>
        {
            if (lostAcknowledgement || mutation.Operation.Kind != HumanInputRequestLifecycleOperationKind.Cancel)
            {
                return Task.FromResult(lifecycle.CommitDurably(mutation));
            }

            lostAcknowledgement = true;
            lifecycle.CommitDurably(mutation);
            throw new IOException("The request Cancel committed before the caller received its acknowledgement.");
        };
        var controls = new FakeControlOperationStore();
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, controls);
        var request = new CustomLoopCancelRequest(runs.Current.Id, runs.Current.LifecycleVersion, "cancel-lost-request-acknowledgement", AuditSchema.Actors.Web);

        var interrupted = await cancellation.CancelAsync(request);
        var replayed = await cancellation.CancelAsync(request);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.True(lostAcknowledgement);
        Assert.Equal(CustomLoopControlStatus.Cancelled, interrupted.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, snapshot.Head.Status);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            lifecycle.Commits.Select(commit => commit.Mutation.Operation.Kind));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(runs.Current.HumanInputWaitingCheckpoints).Posture);
    }

    [Fact]
    public async Task Cancellation_replays_without_duplicate_request_cancel_after_checkpoint_retirement_commits_but_run_acknowledgement_is_lost()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var acknowledgementLost = false;
        runs.AfterUpdate = run =>
        {
            if (!acknowledgementLost
                && run.Status == CustomLoopRunStatus.CancelRequested
                && run.HumanInputWaitingCheckpoints.Single().Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled)
            {
                acknowledgementLost = true;
                throw new IOException("The checkpoint retirement committed before its run-store acknowledgement was returned.");
            }

            return Task.CompletedTask;
        };
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, new FakeControlOperationStore());
        var request = new CustomLoopCancelRequest(runs.Current.Id, runs.Current.LifecycleVersion, "cancel-lost-checkpoint-retirement-acknowledgement", AuditSchema.Actors.Web);

        var interrupted = await cancellation.CancelAsync(request);
        var replayed = await cancellation.CancelAsync(request);
        var snapshot = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.True(acknowledgementLost);
        Assert.Equal(CustomLoopControlStatus.Failed, interrupted.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, snapshot.Head.Status);
        Assert.Equal(
            [HumanInputRequestLifecycleOperationKind.Create, HumanInputRequestLifecycleOperationKind.Cancel],
            lifecycle.Commits.Select(commit => commit.Mutation.Operation.Kind));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(runs.Current.HumanInputWaitingCheckpoints).Posture);
    }

    [Fact]
    public async Task Cancellation_retires_parallel_pending_human_input_checkpoints_in_canonical_order_once_each()
    {
        var context = await ParallelHumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(Result("parallel cancellation source")),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoints = runs.Current.HumanInputWaitingCheckpoints
            .OrderBy(checkpoint => checkpoint.Binding.ActivationOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.NodeVisitOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        var controls = new FakeControlOperationStore();
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, controls);
        var request = new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-parallel-human-input",
            AuditSchema.Actors.Web);

        var cancelled = await cancellation.CancelAsync(request);
        var replayed = await cancellation.CancelAsync(request);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, cancelled.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, replayed.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.All(runs.Current.HumanInputWaitingCheckpoints, checkpoint => Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, checkpoint.Posture));
        var cancellations = lifecycle.Commits.Where(commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel).ToArray();
        Assert.Equal(checkpoints.Select(checkpoint => checkpoint.Request.RequestId), cancellations.Select(commit => commit.Mutation.Operation.TargetRequestId));
        Assert.Equal(2, cancellations.Length);
        Assert.Equal(4, lifecycle.Commits.Count);
        Assert.All(checkpoints, checkpoint => Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, lifecycle.Snapshot(checkpoint.Request.RequestId)!.Head.Status));
    }

    [Fact]
    public async Task Cancellation_retires_the_historical_checkpoint_bound_in_canonical_order_once_each_and_terminalizes()
    {
        const int HistoricalCheckpointReconciliationAttemptBound = 32;

        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var publication = Publication(context, runs, lifecycle);
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: publication).RunAsync(Request(context));
        runs.ReplaceCurrent(CreateHistoricalBoundPendingHumanInputRun(runs.Current, HistoricalCheckpointReconciliationAttemptBound));
        var checkpoints = runs.Current.HumanInputWaitingCheckpoints
            .OrderBy(checkpoint => checkpoint.Binding.ActivationOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.NodeVisitOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        var pending = checkpoints.Where(checkpoint => checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending).ToArray();
        foreach (var checkpoint in pending)
        {
            if (lifecycle.Snapshot(checkpoint.Request.RequestId) is not null)
            {
                continue;
            }

            var published = await publication.PublishAsync(new HumanInputRequestPublicationRequest(
                runs.Current.Id,
                checkpoint.Binding.CheckpointId,
                checkpoint.CheckpointHash));
            Assert.Equal(HumanInputRequestPublicationStatus.Published, published.Status);
        }

        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, new FakeControlOperationStore());
        var cancelled = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-historical-human-input-checkpoint-bound",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(HistoricalCheckpointReconciliationAttemptBound, pending.Length);
        Assert.Equal(CustomLoopControlStatus.Cancelled, cancelled.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.All(runs.Current.HumanInputWaitingCheckpoints, checkpoint => Assert.True(checkpoint.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled or GovernedLoopHumanInputWaitingCheckpointPosture.Terminal));
        var cancellations = lifecycle.Commits.Where(commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel).ToArray();
        Assert.Equal(pending.Select(checkpoint => checkpoint.Request.RequestId), cancellations.Select(commit => commit.Mutation.Operation.TargetRequestId));
        Assert.Equal(pending.Length, cancellations.Length);
        Assert.All(pending, checkpoint => Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, lifecycle.Snapshot(checkpoint.Request.RequestId)!.Head.Status));
    }

    [Fact]
    public async Task Cancellation_fails_closed_when_answered_not_resumed_checkpoint_wins_before_request_cancel()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var answered = AnswerHumanInputCheckpointForOrderedReentry(runs.Current, _now.AddSeconds(2));
        runs.ReplaceCurrent(answered);
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, new FakeControlOperationStore());

        var result = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-after-human-input-answer",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopControlStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, runs.Current.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, lifecycle.Snapshot(checkpoint.Request.RequestId)!.Head.Status);
        Assert.Single(lifecycle.Commits);
        Assert.Equal(HumanInputRequestLifecycleOperationKind.Create, lifecycle.Commits[0].Mutation.Operation.Kind);
    }

    [Fact]
    public async Task Authenticated_response_winner_under_the_shared_authority_fence_remains_answered_without_loop_cancel()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoint = Assert.Single(runs.Current.HumanInputWaitingCheckpoints);
        var published = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));
        var responseStore = new InMemoryHumanInputResponseLifecycleStore(published);
        var responses = new HumanInputResponseLifecycleService(
            responseStore,
            new RecordingHumanInputResponseActorAuthenticator(),
            context.Store,
            context.Anchor.AdapterBinding.WorkspaceId,
            new FixedTimeProvider(_now.AddSeconds(2)));
        var response = await responses.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            checkpoint.Request,
            published.Head,
            "response-wins-before-loop-cancel",
            "response-wins-before-loop-cancel-value",
            HumanInputResponseLifecycleTestData.Text("accepted")));
        lifecycle.ReplaceSnapshot(checkpoint.Request.RequestId, responseStore.CurrentSnapshot!.Request);
        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, new FakeControlOperationStore());

        var result = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-after-response-winner",
            AuditSchema.Actors.Web));
        var answered = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(checkpoint.Request.RequestId));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, response.Status);
        Assert.Equal(CustomLoopControlStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, runs.Current.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, answered.Head.Status);
        Assert.Equal("response-wins-before-loop-cancel", answered.Head.LastOperationId);
        Assert.DoesNotContain(lifecycle.Commits, commit => commit.Mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Cancel);
        Assert.Single(responseStore.Commits);
    }

    [Fact]
    public async Task Queued_human_input_response_continuation_loses_its_waiting_cas_to_parent_cancellation_without_dispatch_or_pending_requests()
    {
        var context = await ParallelHumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(Result("queued continuation cancellation source")),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoints = runs.Current.HumanInputWaitingCheckpoints
            .OrderBy(checkpoint => checkpoint.Binding.ActivationOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.NodeVisitOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        var selectedCheckpoint = checkpoints[0];
        var published = Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(selectedCheckpoint.Request.RequestId));
        var responseStore = new InMemoryHumanInputResponseLifecycleStore(published);
        var currentPosture = new StubGovernedLoopSleepCurrentPosturePort
        {
            Result = new GovernedLoopSleepCurrentPostureReadResult(
                GovernedLoopSleepCurrentPostureReadStatus.Found,
                HumanInputContinuationPosture(context, runs.Current)),
        };
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var ordered = new CancellationBarrierRecordingOrderedRuntime();
        var continuation = new HumanInputResponseContinuationService(
            runs,
            responseStore,
            sleepStore,
            currentPosture,
            new HumanInputResponseContinuationBoundContextPort(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            ordered,
            new FixedTimeProvider(_now.AddSeconds(3)));
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            currentPosture,
            continuation,
            continuation,
            new FixedTimeProvider(_now.AddSeconds(3)));
        continuation.BindSleep(sleep);
        var waitingRereadReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAfterParentCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        responseStore.ReadOverride = async (request, cancellationToken) =>
        {
            waitingRereadReached.TrySetResult();
            await releaseAfterParentCancellation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return responseStore.ReadCurrent(request);
        };

        var writesBeforeWorker = runs.Writes.Count;
        var worker = continuation.WakeAsync(new HumanInputResponseContinuationCandidate(
            runs.Current.Id,
            selectedCheckpoint.Binding.CheckpointId,
            selectedCheckpoint.CheckpointHash));
        await waitingRereadReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, new FakeControlOperationStore());
        var cancelled = await cancellation.CancelAsync(new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-wins-over-queued-response-continuation",
            AuditSchema.Actors.Web));
        var writesAfterParentCancellation = runs.Writes.Count;
        responseStore.ReplaceLifecycle(Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(selectedCheckpoint.Request.RequestId)));
        releaseAfterParentCancellation.TrySetResult();
        var workerResult = await worker.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, cancelled.Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Retired, workerResult.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.Equal(writesAfterParentCancellation, runs.Writes.Count);
        Assert.Equal(0, ordered.ResumeHumanInputCount);
        Assert.Equal(0, ordered.ResumeHumanInputFailureCount);
        Assert.Equal(0, sleepStore.CheckpointCount);
        Assert.Equal(0, sleepStore.WakeCount);
        Assert.DoesNotContain(
            runs.Writes.Skip(writesBeforeWorker),
            run => run.HumanInputWaitingCheckpoints.Any(
                checkpoint => checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed));
        Assert.All(
            runs.Current.HumanInputWaitingCheckpoints,
            checkpoint => Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, checkpoint.Posture));
        Assert.All(
            checkpoints,
            checkpoint => Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, lifecycle.Snapshot(checkpoint.Request.RequestId)!.Head.Status));
        Assert.DoesNotContain(
            runs.Current.HumanInputWaitingCheckpoints,
            checkpoint => checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending);
        Assert.DoesNotContain(
            checkpoints,
            checkpoint => lifecycle.Snapshot(checkpoint.Request.RequestId)!.Head.Status == HumanInputRequestLifecycleStatus.Pending);
    }

    [Fact]
    public async Task Cancel_requested_run_refuses_a_queued_human_input_continuation_before_parent_checkpoint_retirement()
    {
        var context = await ParallelHumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(Result("cancel requested continuation refusal source")),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));
        var checkpoints = runs.Current.HumanInputWaitingCheckpoints
            .OrderBy(checkpoint => checkpoint.Binding.ActivationOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.NodeVisitOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        var selectedCheckpoint = checkpoints[0];
        var responseStore = new InMemoryHumanInputResponseLifecycleStore(
            Assert.IsType<HumanInputRequestLifecycleStoreSnapshot>(lifecycle.Snapshot(selectedCheckpoint.Request.RequestId)));
        var currentPosture = new StubGovernedLoopSleepCurrentPosturePort
        {
            Result = new GovernedLoopSleepCurrentPostureReadResult(
                GovernedLoopSleepCurrentPostureReadStatus.Found,
                HumanInputContinuationPosture(context, runs.Current)),
        };
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var ordered = new CancellationBarrierRecordingOrderedRuntime();
        var continuation = new HumanInputResponseContinuationService(
            runs,
            responseStore,
            sleepStore,
            currentPosture,
            new HumanInputResponseContinuationBoundContextPort(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            ordered,
            new FixedTimeProvider(_now.AddSeconds(3)));
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            currentPosture,
            continuation,
            continuation,
            new FixedTimeProvider(_now.AddSeconds(3)));
        continuation.BindSleep(sleep);
        var checkpointRetirementReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheckpointRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runs.BeforeUpdateAsync = async (candidate, _) =>
        {
            if (candidate.Status == CustomLoopRunStatus.CancelRequested
                && candidate.HumanInputWaitingCheckpoints.Any(
                    checkpoint => checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled))
            {
                checkpointRetirementReached.TrySetResult();
                await releaseCheckpointRetirement.Task.ConfigureAwait(false);
            }
        };

        var cancellation = HumanInputCancellationLifecycle(context, runs, lifecycle, new FakeControlOperationStore());
        var parent = cancellation.CancelAsync(new CustomLoopCancelRequest(
            runs.Current.Id,
            runs.Current.LifecycleVersion,
            "cancel-requested-refuses-queued-continuation",
            AuditSchema.Actors.Web));
        await checkpointRetirementReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var writesBeforeWake = runs.Writes.Count;

        var wake = await continuation.WakeAsync(new HumanInputResponseContinuationCandidate(
            runs.Current.Id,
            selectedCheckpoint.Binding.CheckpointId,
            selectedCheckpoint.CheckpointHash));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, runs.Current.Status);
        Assert.All(
            runs.Current.HumanInputWaitingCheckpoints,
            checkpoint => Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, checkpoint.Posture));
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Stale, wake.Status);
        Assert.Equal(writesBeforeWake, runs.Writes.Count);
        Assert.Equal(0, sleepStore.CheckpointCount);
        Assert.Equal(0, sleepStore.WakeCount);
        Assert.Equal(0, ordered.ResumeHumanInputCount);
        Assert.Equal(0, ordered.ResumeHumanInputFailureCount);

        releaseCheckpointRetirement.TrySetResult();
        var cancelled = await parent.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(CustomLoopControlStatus.Cancelled, cancelled.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, runs.Current.Status);
        Assert.All(
            runs.Current.HumanInputWaitingCheckpoints,
            checkpoint => Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, checkpoint.Posture));
        Assert.All(
            checkpoints,
            checkpoint => Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, lifecycle.Snapshot(checkpoint.Request.RequestId)!.Head.Status));
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
    public async Task Human_input_admission_fails_closed_when_a_post_commit_checkpoint_acknowledgement_cannot_be_reconciled()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var postCommitAcknowledgementLost = false;
        store.AfterUpdate = run =>
        {
            if (!postCommitAcknowledgementLost && run.HumanInputWaitingCheckpoints.Count == 1)
            {
                postCommitAcknowledgementLost = true;
                store.GetException = new IOException("The committed checkpoint cannot be read for acknowledgement reconciliation.");
                throw new IOException("The checkpoint acknowledgement was lost after the durable write.");
            }

            return Task.CompletedTask;
        };

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));

        Assert.True(postCommitAcknowledgementLost);
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
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
    public async Task Human_input_admission_fails_closed_when_exact_policy_resolution_source_throws()
    {
        var context = await HumanInputContextAsync();
        var source = new HumanInputPolicyResolutionTestSource
        {
            BeforeRead = (_, _) => throw new IOException("The exact policy source is unavailable."),
        };
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context, source: source)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_propagates_cancellation_during_exact_policy_resolution_without_checkpoint_or_delivery()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();

        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Empty(interrupted.Store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_propagates_cancellation_while_parking_an_interrupted_human_input_admission()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        using var cancellation = new CancellationTokenSource();
        interrupted.Store.BeforeUpdate = (candidate, _) =>
        {
            attemptedRecovery = candidate;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        var recovery = new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecoverAsync(AuditSchema.Actors.Web, cancellation.Token));

        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_fails_closed_when_parking_an_interrupted_human_input_admission_cannot_be_persisted()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        interrupted.Store.BeforeUpdate = (candidate, _) =>
        {
            attemptedRecovery = candidate;
            throw new IOException("The recovery write is unavailable.");
        };

        var result = Assert.Single(await new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Same(interrupted.Store.Current, result.Run);
        Assert.Contains("recovery transition failed", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_reports_conflict_when_an_interrupted_human_input_admission_changes_and_cannot_be_reloaded()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        interrupted.Store.RawConflictSuccessorFactory = (current, candidate) =>
        {
            attemptedRecovery = candidate;
            return current;
        };
        interrupted.Store.GetException = new IOException("The concurrent recovery state cannot be reloaded.");

        var result = Assert.Single(await new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Conflict, result.Status);
        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Same(interrupted.Store.Current, result.Run);
        Assert.Contains("changed concurrently", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery + 1, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Restart_recovery_fails_closed_when_parking_an_interrupted_human_input_admission_is_rejected()
    {
        var interrupted = await InterruptHumanInputDuringPolicyResolutionAsync();
        var writesBeforeRecovery = interrupted.Store.Writes.Count;
        CustomLoopRunRecord? attemptedRecovery = null;
        interrupted.Store.BeforeUpdate = (candidate, _) => attemptedRecovery = candidate;
        interrupted.Store.UpdateResultOverride = CustomLoopRunStoreResult.NotFound();

        var result = Assert.Single(await new CustomLoopRecoveryService(
            interrupted.Store,
            new RecordingAuditLog(),
            new FixedTimeProvider(interrupted.Store.Current.UpdatedAtUtc.AddSeconds(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        AssertInterruptedHumanInputRecoveryCandidate(attemptedRecovery);
        Assert.Same(interrupted.Store.Current, result.Run);
        Assert.Contains("transition was rejected", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CustomLoopRunStatus.Running, interrupted.Store.Current.Status);
        Assert.Equal(writesBeforeRecovery, interrupted.Store.Writes.Count);
        Assert.Empty(interrupted.Executor.Requests);
        Assert.Empty(interrupted.Publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_trusted_policy_resolution_time_is_unavailable()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context, timeProvider: new FixedTimeProvider(default))).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Fact]
    public async Task Human_input_admission_fails_closed_when_the_trusted_policy_clock_throws()
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        var result = await Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context, timeProvider: new ThrowingEffectAuthorityTimeProvider())).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_policy_resolution_unavailable", store.Current.FailureCode);
        Assert.Empty(store.Current.HumanInputWaitingCheckpoints);
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
    public async Task Human_input_terminal_resume_rehydrates_the_exact_response_for_a_non_retaining_condition_without_leaking_it_into_continuation_evidence()
    {
        const string PrivateResponse = "response-private-condition-token";
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value));
        var runtime = Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context),
            audit: audit,
            humanInputBindingSource: source);

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);

        var resumed = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(1, source.ReadCount);
        var condition = Assert.Single(store.Current.Frontier!.Payload.Nodes, node => node.NodeId == "response-condition");
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, condition.Status);
        Assert.Equal(GovernedLoopControlCondition.False, condition.ControlOutcome);
        Assert.Empty(executor.Requests);
        Assert.DoesNotContain(PrivateResponse, Assert.Single(store.Current.HumanInputWaitingCheckpoints).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, source.LastBinding!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, store.Current.FinalOutput ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, store.Current.Checkpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, store.Current.Events.Select(item => item.Detail ?? string.Empty));
        Assert.DoesNotContain(PrivateResponse, audit.Events.Select(item => item.Detail));
        Assert.DoesNotContain(PrivateResponse, publisher.Requests.Select(item => item.ToString()));
        Assert.DoesNotContain(PrivateResponse, store.Current.ContextSnapshot.ToString(), StringComparison.Ordinal);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_rejects_an_old_exit_binding_before_response_rehydration_or_publication()
    {
        const string PrivateResponse = "response-private-old-exit-token";
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var audit = new RecordingAuditLog();
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-old-exit-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value));
        var runtime = Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context),
            audit: audit,
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal(0, source.ReadCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
        Assert.DoesNotContain(PrivateResponse, store.Current.FinalOutput ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, store.Current.Checkpoint.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateResponse, store.Current.Events.Select(item => item.Detail ?? string.Empty));
        Assert.DoesNotContain(PrivateResponse, audit.Events.Select(item => item.Detail));
        Assert.DoesNotContain(PrivateResponse, publisher.Requests.Select(item => item.ToString()));
        Assert.DoesNotContain(PrivateResponse, store.Current.ContextSnapshot.ToString(), StringComparison.Ordinal);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_leaves_the_durable_run_unchanged_for_binding_source_unavailability_then_rereads_on_the_next_entry()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value)) { IsUnavailable = true };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeUnavailable = store.Writes.Count;
        var unavailable = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, unavailable.Status);
        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeUnavailable, store.Writes.Count);
        Assert.Equal(1, source.ReadCount);
        source.IsUnavailable = false;

        var recovered = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Completed, recovered.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(2, source.ReadCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_input_terminal_condition_escalates_to_review_when_the_exact_binding_is_invalid_or_malformed(bool malformed)
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value))
        {
            IsInvalid = !malformed,
            ReturnMalformedReady = malformed,
        };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("human_input_binding_invalid", store.Current.FailureCode);
        Assert.Equal(1, source.ReadCount);
        Assert.Null(source.LastBinding);
        Assert.DoesNotContain("response-private-condition-token", store.Current.Events.Select(item => item.Detail ?? string.Empty));
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_condition_leaves_the_run_unchanged_when_the_binding_source_throws()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value))
        {
            ResolveException = new IOException("simulated response-store outage"),
        };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeResolution = store.Writes.Count;

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeResolution, store.Writes.Count);
        Assert.Equal(1, source.ReadCount);
        Assert.Null(source.LastBinding);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_condition_propagates_caller_cancellation_from_the_exact_binding_source()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        Assert.True(GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            "\"response-private-condition-token\"",
            out var value,
            out _));
        using var cancellation = new CancellationTokenSource();
        var source = new ExactHumanInputBindingSource(Assert.IsType<GovernedLoopTypedValue>(value))
        {
            BeforeResolve = cancellation.Cancel,
            ResolveException = new OperationCanceledException("simulated caller cancellation", cancellation.Token),
        };
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputBindingSource: source);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeResolution = store.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ResumeHumanInputAsync(
            new GovernedLoopSequentialOrderedHumanInputResumeRequest(
                GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
                context.Anchor,
                context.Plan,
                context.Artifact,
                terminal.Checkpoint.Binding.CheckpointId,
                terminal.ReceiptHash,
                terminal.Run.AdmissionActor),
            cancellation.Token));

        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeResolution, store.Writes.Count);
        Assert.Equal(1, source.ReadCount);
        Assert.Null(source.LastBinding);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_condition_leaves_the_run_unchanged_without_a_configured_binding_source()
    {
        var context = await HumanInputConditionContextAsync();
        var store = new FakeRunStore(context.Run);
        var runtime = Runtime(
            context,
            store,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, (await runtime.RunAsync(Request(context))).Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writesBeforeResolution = store.Writes.Count;

        var result = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, result.Status);
        Assert.Same(terminal.Run, store.Current);
        Assert.Equal(writesBeforeResolution, store.Writes.Count);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_reenters_the_canonical_ordered_runner_and_completes_the_exit()
    {
        var context = await HumanInputSuccessFallbackContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        var terminalValidation = CustomLoopRunValidator.ValidateUpdate(store.Current, terminal.Run);
        Assert.True(terminalValidation.IsValid, string.Join(Environment.NewLine, terminalValidation.Errors));
        store.ReplaceCurrent(terminal.Run);

        var resumed = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            terminal.ReceiptHash,
            terminal.Run.AdmissionActor));

        Assert.True(resumed.Status == CustomLoopOrderedRunStatus.Completed, $"{resumed.Status}: {resumed.Detail}; {store.Current.FailureCode}: {store.Current.FailureDetail}");
        Assert.Equal(CustomLoopRunStatus.Completed, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Completed, store.Current.Frontier?.Payload.Status);
        Assert.Empty(executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_terminal_resume_rejects_a_receipt_mismatch_without_ordered_dispatch()
    {
        var context = await HumanInputSuccessFallbackContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        store.ReplaceCurrent(AnswerHumanInputCheckpointForOrderedReentry(store.Current, _now.AddMilliseconds(500)));
        var terminal = CompleteHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(terminal.Run);
        var writes = store.Writes.Count;

        var resumed = await runtime.ResumeHumanInputAsync(new GovernedLoopSequentialOrderedHumanInputResumeRequest(
            GovernedLoopSequentialOrderedHumanInputResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            terminal.Checkpoint.Binding.CheckpointId,
            new string('c', 64),
            terminal.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, resumed.Status);
        Assert.Equal(writes, store.Writes.Count);
        Assert.Empty(executor.Requests);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_failure_resume_reenters_the_canonical_ordered_runner_on_the_routed_failure_edge()
    {
        var context = await HumanInputFailureContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var retirement = RejectHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        Assert.True(CustomLoopRunValidator.ValidateUpdate(store.Current, retirement.Run).IsValid);
        store.ReplaceCurrent(retirement.Run);

        var resumed = await runtime.ResumeHumanInputFailureAsync(new GovernedLoopSequentialOrderedHumanInputFailureResumeRequest(
            GovernedLoopSequentialOrderedHumanInputFailureResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            retirement.Checkpoint.Binding.CheckpointId,
            retirement.RetirementEvidenceHash,
            retirement.EventId,
            retirement.FailureEvidenceHash,
            retirement.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.Failed, resumed.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, store.Current.Frontier?.Payload.Status);
        Assert.Empty(executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Human_input_failure_resume_rejects_a_failure_evidence_mismatch_without_ordered_dispatch()
    {
        var context = await HumanInputFailureContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var runtime = Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context));

        var parked = await runtime.RunAsync(Request(context));
        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var retirement = RejectHumanInputCheckpointForOrderedReentry(store.Current, context, _now.AddSeconds(1));
        store.ReplaceCurrent(retirement.Run);
        var writes = store.Writes.Count;

        var resumed = await runtime.ResumeHumanInputFailureAsync(new GovernedLoopSequentialOrderedHumanInputFailureResumeRequest(
            GovernedLoopSequentialOrderedHumanInputFailureResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            retirement.Checkpoint.Binding.CheckpointId,
            retirement.RetirementEvidenceHash,
            retirement.EventId,
            new string('c', 64),
            retirement.Run.AdmissionActor));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, resumed.Status);
        Assert.Equal(writes, store.Writes.Count);
        Assert.Empty(executor.Requests);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
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
        Assert.Equal(
            checkpoints.Select(checkpoint => checkpoint.Binding.CheckpointId),
            parked.RequestPublication.Requests.Select(request => request.CheckpointId));
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
    public async Task Resume_rejects_a_paused_human_input_checkpoint_with_a_malformed_unconverged_expired_posture_without_ordered_dispatch()
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
            "pause-before-expired-human-input-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var expired = ExpireHumanInputWaitingCheckpoint(store.Current);
        Assert.False(CustomLoopRunValidator.Validate(expired).IsValid);
        store.ReplaceCurrent(expired, validate: false);
        var writesBeforeResume = store.Writes.Count;

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(
            expired.Id,
            expired.LifecycleVersion,
            "resume-expired-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.InvalidState, result.Status);
        Assert.Same(expired, store.Current);
        Assert.Equal(writesBeforeResume, store.Writes.Count);
        Assert.Equal(0, resumeExecutor.ResumeCount);
        Assert.Empty(executor.Requests);
        Assert.Empty(publisher.Requests);
    }

    [Theory]
    [InlineData(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, CustomLoopControlStatus.WorkspaceHostUnavailable, 0)]
    [InlineData(CustomLoopExecutionLeaseStatus.WorkspaceBusy, CustomLoopControlStatus.WorkspaceExecutionBusy, 1)]
    [InlineData(CustomLoopExecutionLeaseStatus.OperationInProgress, CustomLoopControlStatus.OperationInProgress, 1)]
    [InlineData(CustomLoopExecutionLeaseStatus.OperationConflict, CustomLoopControlStatus.Conflict, 1)]
    public async Task Resume_fails_closed_when_a_paused_human_input_checkpoint_cannot_reacquire_execution_ownership(
        CustomLoopExecutionLeaseStatus leaseStatus,
        CustomLoopControlStatus expectedStatus,
        int expectedAcquisitionCount)
    {
        var context = await HumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();
        var parked = await Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context)).RunAsync(Request(context));
        var resumeExecutor = new NoopWaitLifecycleResumeExecutor(store.Current);
        var gate = new TestExecutionGate(leaseStatus);
        var service = new CustomLoopLifecycleService(
            store,
            new FakeControlOperationStore(),
            resumeExecutor,
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            gate,
            new FixedTimeProvider(_now.AddSeconds(1)));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        var pause = await service.PauseAsync(new CustomLoopPauseRequest(
            store.Current.Id,
            store.Current.LifecycleVersion,
            $"pause-before-{leaseStatus.ToString().ToLowerInvariant()}-human-input-resume",
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.Paused, pause.Status);
        var paused = store.Current;
        var writesBeforeResume = store.Writes.Count;

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(
            paused.Id,
            paused.LifecycleVersion,
            $"resume-{leaseStatus.ToString().ToLowerInvariant()}-human-input",
            AuditSchema.Actors.Web));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Same(paused, store.Current);
        Assert.Equal(writesBeforeResume, store.Writes.Count);
        Assert.Single(store.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(expectedAcquisitionCount, gate.AcquisitionCount);
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

    private static (CustomLoopRunRecord Run, GovernedLoopHumanInputWaitingCheckpoint Checkpoint, string ReceiptHash) CompleteHumanInputCheckpointForOrderedReentry(
        CustomLoopRunRecord run,
        SequentialTestContext context,
        DateTimeOffset completedAtUtc)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal));
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(context.Plan.Nodes.ElementAtOrDefault(activation.PlanOrdinal));
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, checkpoint.Posture);
        var receiptHash = new string('b', 64);
        var terminalEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized,
            completedAtUtc,
            null,
            null,
            null,
            "human-input-ordered-reentry-receipt",
            receiptHash,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var terminalCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Terminal,
            [.. checkpoint.Evidence, terminalEvidence],
            string.Empty));
        var baseEvent = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 2,
            "human-input-ordered-reentry-terminal",
            completedAtUtc,
            CustomLoopRunEventKind.NodeAttemptCompleted,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact accepted Human Input response terminalized before ordered re-entry.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var completed = GovernedLoopSequentialFrontierMachine.CompleteWaitingHumanInput(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt!.Value,
            activation.AttemptOperationId,
            baseEvent.EventId,
            CustomLoopSequentialOutcomeArtifactHash.Compute(baseEvent),
            completedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, completed.Status);
        var terminalActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(completed.Frontier?.Payload.Nodes.ElementAtOrDefault(activation.ActivationOrdinal));
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            context.Anchor.AdapterBinding.WorkspaceId,
            context.Anchor.AdapterBinding.ExecutionBinding.RunId,
            context.Anchor.AdapterBinding.ExecutionBinding.Revision,
            context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration,
            terminalActivation.ActivationOrdinal,
            terminalActivation.VisitOrdinal,
            terminalActivation.NodeId,
            terminalActivation.Attempt,
            terminalActivation.CycleId,
            terminalActivation.CycleIteration,
            GovernedLoopControlCondition.Success,
            terminalActivation.SelectedControlEdgeIds,
            terminalActivation.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Completed,
            CustomLoopSequentialOutcomeArtifactHash.Compute(baseEvent),
            string.Empty));
        var lifecycle = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 1,
            "human-input-ordered-reentry-frontier",
            completedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The exact accepted Human Input response advanced the canonical frontier before ordered re-entry.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var terminal = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = completedAtUtc,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = completedAtUtc },
            Frontier = completed.Frontier,
            Events = [.. run.Events, lifecycle, baseEvent with { SequentialNodeEvidence = evidence }],
            HumanInputWaitingCheckpoints = [terminalCheckpoint],
        };
        Assert.True(CustomLoopRunValidator.Validate(terminal).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(terminal).Errors));
        return (terminal, terminalCheckpoint, receiptHash);
    }

    private static CustomLoopRunRecord AnswerHumanInputCheckpointForOrderedReentry(CustomLoopRunRecord run, DateTimeOffset selectedAtUtc)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var request = new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
        var response = new HumanInputResponseReference(
            HumanInputResponseReference.CurrentSchemaVersion,
            "human-input-ordered-reentry-response",
            request,
            new string('a', 64),
            new string('b', 64));
        var selection = HumanInputResponseSelectionReference.Create(HumanInputResponseSelectionHash.Apply(new HumanInputResponseSelection(
            HumanInputResponseSelection.CurrentSchemaVersion,
            "human-input-ordered-reentry-selection",
            request,
            HumanInputResponsePolicyKind.FirstValid,
            ImmutableArray.Create(response),
            null,
            null,
            selectedAtUtc,
            string.Empty)));
        var answeredEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered,
            selectedAtUtc,
            selection,
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var answered = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed,
            [.. checkpoint.Evidence, answeredEvidence],
            string.Empty));
        var candidate = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            UpdatedAtUtc = selectedAtUtc,
            HumanInputWaitingCheckpoints = [answered],
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(run, candidate).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(run, candidate).Errors));
        return candidate;
    }

    private static (CustomLoopRunRecord Run, GovernedLoopHumanInputWaitingCheckpoint Checkpoint, string RetirementEvidenceHash, string EventId, string FailureEvidenceHash) RejectHumanInputCheckpointForOrderedReentry(
        CustomLoopRunRecord run,
        SequentialTestContext context,
        DateTimeOffset rejectedAtUtc)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(run.Frontier?.Payload.Nodes.ElementAtOrDefault(checkpoint.Binding.ActivationOrdinal));
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(context.Plan.Nodes.ElementAtOrDefault(activation.PlanOrdinal));
        const string LifecycleOperationId = "human-input-rejected-operation";
        var operationHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(LifecycleOperationId)));
        var eventId = "human-input-human-input-rejected-" + operationHash[..24];
        var baseEvent = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 2,
            eventId,
            rejectedAtUtc,
            CustomLoopRunEventKind.NodeAttemptFailed,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            activation.NodeId,
            activation.Attempt,
            "The exact Human Input lifecycle operation rejected the request without an accepted selection.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var causal = new GovernedLoopFailureEvidenceReference(LifecycleOperationId, checkpoint.Request.RequestHash);
        var classified = new GovernedLoopFailureClassifier().Classify(
            new GovernedLoopFailureClassificationContext(
                "failure-human-input-rejected",
                context.Anchor.AdapterBinding.WorkspaceId,
                context.Anchor.AdapterBinding.ExecutionBinding.RunId,
                context.Anchor.AdapterBinding.ExecutionBinding.Revision,
                context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration,
                activation.ActivationOrdinal,
                activation.VisitOrdinal,
                activation.NodeId,
                activation.Attempt!.Value,
                causal),
            [new GovernedLoopFailureObservation(
                GovernedLoopFailureObservationKind.TerminalFailure,
                GovernedLoopFailureSource.User,
                "human-input-rejected",
                causal)],
            rejectedAtUtc);
        Assert.Equal(GovernedLoopFailureClassificationStatus.Classified, classified.Status);
        var failure = Assert.IsType<GovernedLoopFailureEvidence>(classified.Evidence);
        var classifiedEvent = baseEvent with { FailureEvidence = failure };
        var failed = GovernedLoopSequentialFrontierMachine.FailWaiting(
            run.Frontier,
            context.Anchor.AdapterBinding,
            context.Plan,
            node,
            activation,
            activation.Attempt.Value,
            activation.AttemptOperationId,
            classifiedEvent.EventId,
            CustomLoopSequentialOutcomeArtifactHash.Compute(classifiedEvent),
            GovernedLoopControlCondition.Failure,
            rejectedAtUtc);
        Assert.True(failed.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied, failed.Detail);
        var failedActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(failed.Frontier?.Payload.Nodes.ElementAtOrDefault(activation.ActivationOrdinal));
        var sequentialEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection,
            context.Anchor.AdapterBinding.WorkspaceId,
            context.Anchor.AdapterBinding.ExecutionBinding.RunId,
            context.Anchor.AdapterBinding.ExecutionBinding.Revision,
            context.Anchor.AdapterBinding.ExecutionBinding.ExecutionGeneration,
            failedActivation.ActivationOrdinal,
            failedActivation.VisitOrdinal,
            failedActivation.NodeId,
            failedActivation.Attempt,
            failedActivation.CycleId,
            failedActivation.CycleIteration,
            GovernedLoopControlCondition.Failure,
            failedActivation.SelectedControlEdgeIds,
            failedActivation.SkippedControlEdgeIds,
            null,
            null,
            CustomLoopSequentialNodeDisposition.Rejected,
            CustomLoopSequentialOutcomeArtifactHash.Compute(classifiedEvent),
            string.Empty)
        {
            FailureEvidenceId = failure.EvidenceId,
            FailureEvidenceHash = failure.ContentHash,
        });
        var retirementEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Rejected,
            rejectedAtUtc,
            null,
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var retiredCheckpoint = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Rejected,
            [.. checkpoint.Evidence, retirementEvidence],
            string.Empty));
        var lifecycle = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 1,
            "human-input-rejected-frontier",
            rejectedAtUtc,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The exact Human Input no-response disposition advanced the canonical frontier without an accepted response.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var retired = run with
        {
            LifecycleVersion = checked(run.LifecycleVersion + 1),
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = rejectedAtUtc,
            ExecutionClock = run.ExecutionClock with { ActiveSinceUtc = rejectedAtUtc },
            Frontier = failed.Frontier,
            Events = [.. run.Events, lifecycle, classifiedEvent with { SequentialNodeEvidence = sequentialEvidence }],
            HumanInputWaitingCheckpoints = [retiredCheckpoint],
        };
        Assert.True(CustomLoopRunValidator.Validate(retired).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(retired).Errors));
        return (retired, retiredCheckpoint, retirementEvidence.EvidenceHash, eventId, failure.ContentHash);
    }

    private static CustomLoopRunRecord ExpireHumanInputWaitingCheckpoint(CustomLoopRunRecord run)
    {
        var checkpoint = Assert.Single(run.HumanInputWaitingCheckpoints);
        var expiredEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion,
            2,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired,
            checkpoint.Request.Timing.ExpiresAtUtc.AddTicks(1),
            null,
            null,
            null,
            null,
            null,
            Assert.Single(checkpoint.Evidence).EvidenceHash,
            string.Empty));
        var expired = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Expired,
            [.. checkpoint.Evidence, expiredEvidence],
            string.Empty));
        return run with { HumanInputWaitingCheckpoints = [expired] };
    }

    private static async Task<(FakeRunStore Store, QueueExecutor Executor, RecordingPublisher Publisher)> InterruptHumanInputDuringPolicyResolutionAsync()
    {
        var context = await HumanInputContextAsync();
        using var cancellation = new CancellationTokenSource();
        var source = new HumanInputPolicyResolutionTestSource
        {
            BeforeRead = (_, _) => cancellation.Cancel(),
        };
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor();
        var publisher = new RecordingPublisher();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runtime(context, store, executor, publisher, HumanInputPolicyResolver(context, source: source)).RunAsync(Request(context), cancellation.Token));
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
        return (store, executor, publisher);
    }

    private static void AssertInterruptedHumanInputRecoveryCandidate(CustomLoopRunRecord? candidate)
    {
        var attempted = Assert.IsType<CustomLoopRunRecord>(candidate);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, attempted.Status);
        Assert.Equal("recovery_open_attempt", attempted.FailureCode);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, attempted.Frontier?.Payload.Status);
    }

    private static async Task<(FakeRunStore Store, QueueExecutor Executor, RecordingPublisher Publisher, RecordingHumanInputRequestPublicationService RequestPublication)> ParkParallelHumanInputAsync()
    {
        var context = await ParallelHumanInputContextAsync();
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("parallel Human Input source"));
        var publisher = new RecordingPublisher();
        var requestPublication = new RecordingHumanInputRequestPublicationService();

        var parked = await Runtime(
            context,
            store,
            executor,
            publisher,
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: requestPublication).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, store.Current.Status);
        Assert.Equal(2, store.Current.HumanInputWaitingCheckpoints.Count);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        return (store, executor, publisher, requestPublication);
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

    private static async Task<SequentialTestContext> HumanInputPublicationContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            },
            bindResolvedGrantToArtifact: true);

    private static async Task<SequentialTestContext> HumanInputFailureContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputFailureArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> HumanInputSuccessFallbackContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputSuccessFallbackArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static async Task<SequentialTestContext> HumanInputConditionContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanInputConditionArtifact(role);
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

    private static async Task<SequentialTestContext> ParallelHumanInputPublicationContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = ParallelHumanInputArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            },
            bindResolvedGrantToArtifact: true);

    private static GovernedLoopSequentialOrderedRuntimeAdapter Runtime(
        SequentialTestContext context,
        FakeRunStore store,
        QueueExecutor executor,
        RecordingPublisher publisher,
        HumanInputPolicyResolutionService? resolver,
        TimeProvider? runnerTimeProvider = null,
        RecordingAuditLog? audit = null,
        IGovernedLoopSequentialHumanInputBindingSource? humanInputBindingSource = null,
        IHumanInputRequestPublicationService? humanInputRequestPublicationService = null,
        bool composeHumanInputRequestPublicationService = true)
    {
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        return new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                executor,
                publisher,
                audit,
                timeProvider: runnerTimeProvider ?? new FixedTimeProvider(_now),
                humanInputPolicyResolutionService: resolver,
                humanInputBindingSource: humanInputBindingSource,
                humanInputRequestPublicationService: composeHumanInputRequestPublicationService
                    ? humanInputRequestPublicationService ?? new RecordingHumanInputRequestPublicationService()
                    : humanInputRequestPublicationService),
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

    private static GovernedLoopGraphRevisionArtifact HumanInputFailureArtifact(ContextualRoleRevisionPin role)
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
                new GovernedLoopNodeDefinition(
                    "success-fallback",
                    GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                    [
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
                GovernedLoopSequentialApplicationTestFixture.Node("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-fallback-success", "human-input", "success-fallback", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-input-to-fail-failure", "human-input", "fail", GovernedLoopControlCondition.Failure),
                new GovernedLoopControlEdgeDefinition("fallback-to-exit", "success-fallback", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit", "fail"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-success-fallback", GovernedLoopBindingKind.Data, "trigger", "request", "success-fallback", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("success-fallback-to-exit", GovernedLoopBindingKind.Data, "success-fallback", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact HumanInputSuccessFallbackArtifact(ContextualRoleRevisionPin role)
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
                new GovernedLoopNodeDefinition(
                    "success-fallback",
                    GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                    [
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-fallback-success", "human-input", "success-fallback", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("fallback-to-exit", "success-fallback", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-fallback", GovernedLoopBindingKind.Data, "trigger", "request", "success-fallback", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("fallback-to-exit", GovernedLoopBindingKind.Data, "success-fallback", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    private static GovernedLoopGraphRevisionArtifact HumanInputConditionArtifact(ContextualRoleRevisionPin role)
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
                new GovernedLoopNodeDefinition(
                    "response-condition",
                    GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
                    [new GovernedLoopPortDefinition(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string> { [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = "allow" }),
                new GovernedLoopNodeDefinition(
                    "approved",
                    GovernedLoopSequentialNodeDescriptors.FailTerminal,
                    [],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>
                    {
                        [GovernedLoopFailNodeVocabulary.CodeParameter] = "response-approved",
                        [GovernedLoopFailNodeVocabulary.ExplanationParameter] = "The approved route terminates without retaining the response value.",
                    }),
                new GovernedLoopNodeDefinition(
                    "denied-fallback",
                    GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                    [
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                        new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                    ],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>()),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-condition", "human-input", "response-condition", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("condition-approved", "response-condition", "approved", GovernedLoopControlCondition.True),
                new GovernedLoopControlEdgeDefinition("condition-denied", "response-condition", "denied-fallback", GovernedLoopControlCondition.False),
                new GovernedLoopControlEdgeDefinition("denied-fallback-to-exit", "denied-fallback", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit", "approved"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("response-to-condition", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "response-condition", GovernedLoopTopologyNodeVocabulary.ValuePort),
                new GovernedLoopBindingDefinition("request-to-denied-fallback", GovernedLoopBindingKind.Data, "trigger", "request", "denied-fallback", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("denied-fallback-to-exit", GovernedLoopBindingKind.Data, "denied-fallback", GovernedLoopPureNodeVocabulary.OutputPort, "exit", "result"),
            ],
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

    private static HumanInputPolicyResolutionService HumanInputPolicyResolver(
        SequentialTestContext context,
        DateTimeOffset? resolvedAtUtc = null,
        HumanInputPolicyResolutionTestSource? source = null,
        TimeProvider? timeProvider = null)
    {
        source ??= new HumanInputPolicyResolutionTestSource();
        var binding = context.Anchor.AdapterBinding;
        var timeout = TimeoutPolicy(binding, context.Run.AdmissionActor);
        var failure = FailurePolicy(binding, context.Run.AdmissionActor);
        source.Results.TryAdd(timeout.Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, timeout, 1));
        source.Results.TryAdd(failure.Reference, new HumanInputPolicySourceReadResult(HumanInputPolicySourceReadStatus.Ready, failure, 1));
        return new HumanInputPolicyResolutionService(source, timeProvider ?? new FixedTimeProvider(resolvedAtUtc ?? _now));
    }

    private static HumanInputRequestPublicationService Publication(
        SequentialTestContext context,
        FakeRunStore runs,
        InMemoryHumanInputRequestLifecycleStore lifecycle)
        => new(
            runs,
            lifecycle,
            context.Store,
            context.Store,
            context.Anchor.AdapterBinding.WorkspaceId,
            new FixedTimeProvider(AuthorityGrantApplicationTestFixture.Now));

    private static CustomLoopLifecycleService HumanInputCancellationLifecycle(
        SequentialTestContext context,
        FakeRunStore runs,
        InMemoryHumanInputRequestLifecycleStore lifecycle,
        FakeControlOperationStore controls,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? new FixedTimeProvider(_now.AddSeconds(3));
        var convergence = HumanInputCancellationConvergence(context, runs, lifecycle, controls, context.Store, clock);
        return new CustomLoopLifecycleService(
            runs,
            controls,
            new NoopWaitLifecycleResumeExecutor(runs.Current),
            new AvailableModel(),
            new NoActiveAttemptCancellationSignal(),
            new RecordingAuditLog(),
            new TestExecutionGate(),
            clock,
            cancellationAuthorityTransaction: context.Store,
            humanInputCancellationConvergence: convergence);
    }

    private static CustomLoopHumanInputCancellationConvergenceService HumanInputCancellationConvergence(
        SequentialTestContext context,
        FakeRunStore runs,
        InMemoryHumanInputRequestLifecycleStore lifecycle,
        FakeControlOperationStore controls,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
        => new(
            runs,
            controls,
            lifecycle,
            context.Store,
            authorityTransaction,
            context.Anchor.AdapterBinding.WorkspaceId,
            timeProvider ?? new FixedTimeProvider(_now.AddSeconds(3)));

    private static CustomLoopControlOperation PendingCancellationControl(CustomLoopRunRecord run, string operationId)
        => new(
            CustomLoopControlOperation.CurrentSchemaVersion,
            operationId,
            CustomLoopControlRequestHash.Compute(CustomLoopControlKind.Cancel, run.Id, run.LifecycleVersion, operationId, AuditSchema.Actors.Web),
            CustomLoopControlKind.Cancel,
            run.Id,
            run.LifecycleVersion,
            AuditSchema.Actors.Web,
            _now,
            _now,
            CustomLoopControlOperationState.Pending,
            CustomLoopControlStatus.Unknown,
            null,
            null,
            false,
            "Cancellation operation pending.");

    private static async Task<(SequentialTestContext Context, FakeRunStore Runs, InMemoryHumanInputRequestLifecycleStore Lifecycle, GovernedLoopHumanInputWaitingCheckpoint Checkpoint)> PublishedHumanInputCancellationAsync()
    {
        var context = await HumanInputPublicationContextAsync();
        var runs = new FakeRunStore(context.Run);
        var lifecycle = new InMemoryHumanInputRequestLifecycleStore();
        var parked = await Runtime(
            context,
            runs,
            new QueueExecutor(),
            new RecordingPublisher(),
            HumanInputPolicyResolver(context),
            humanInputRequestPublicationService: Publication(context, runs, lifecycle)).RunAsync(Request(context));

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        return (context, runs, lifecycle, Assert.Single(runs.Current.HumanInputWaitingCheckpoints));
    }

    private static CustomLoopRunRecord CreateHistoricalBoundPendingHumanInputRun(CustomLoopRunRecord run, int pendingCheckpointCount)
    {
        var templateCheckpoint = run.HumanInputWaitingCheckpoints.First();
        var adapter = Assert.IsType<GovernedLoopSequentialAdapterBinding>(run.SequentialAdapterBinding);
        var templateActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(run.Frontier?.Payload.Nodes.ElementAtOrDefault(templateCheckpoint.Binding.ActivationOrdinal));
        var firstSyntheticActivationOrdinal = run.Frontier!.Payload.Nodes.Count;
        var syntheticCheckpointCount = pendingCheckpointCount - run.HumanInputWaitingCheckpoints.Count(checkpoint => checkpoint.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Pending);
        Assert.True(syntheticCheckpointCount >= 0);
        var nodes = run.Frontier.Payload.Nodes
            .Concat(Enumerable.Range(firstSyntheticActivationOrdinal, syntheticCheckpointCount)
            .Select(index => GovernedLoopNodeExecutionEvidence.CreateActivation(
                index,
                index,
                1,
                HistoricalBoundHumanInputNodeId(index),
                templateActivation.Descriptor,
                [],
                [],
                GovernedLoopNodeExecutionStatus.Waiting,
                1,
                $"historical-bound-human-input-attempt-{index:D3}")))
            .ToArray();
        var frontier = GovernedLoopFrontierPosture.Create(
            adapter.ExecutionBinding,
            adapter.WorkspaceId,
            adapter.GraphArtifactHash,
            adapter.GraphLayoutHash,
            adapter.AdmissionReceiptHash,
            checked(run.Frontier!.Payload.FrontierVersion + 1),
            GovernedLoopExecutionLimits.Schema1ConcurrencyCeiling,
            GovernedLoopFrontierStatus.Waiting,
            nodes,
            run.Frontier.Payload.UpdatedAtUtc,
            string.Empty);
        var pending = Enumerable.Range(firstSyntheticActivationOrdinal, syntheticCheckpointCount)
            .Select(index => CreateHistoricalBoundHumanInputCheckpoint(templateCheckpoint, adapter, frontier, index))
            .ToArray();
        var checkpoints = new[] { templateCheckpoint }
            .Concat(pending)
            .OrderBy(checkpoint => checkpoint.Binding.ActivationOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.NodeVisitOrdinal)
            .ThenBy(checkpoint => checkpoint.Binding.CheckpointId, StringComparer.Ordinal)
            .ToArray();
        var historicalBound = run with
        {
            Frontier = frontier,
            HumanInputWaitingCheckpoints = checkpoints,
        };
        Assert.True(CustomLoopRunValidator.Validate(historicalBound).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(historicalBound).Errors));
        return historicalBound;
    }

    private static GovernedLoopHumanInputWaitingCheckpoint CreateHistoricalBoundHumanInputCheckpoint(
        GovernedLoopHumanInputWaitingCheckpoint template,
        GovernedLoopSequentialAdapterBinding adapter,
        GovernedLoopFrontierPosture frontier,
        int index)
    {
        var nodeId = HistoricalBoundHumanInputNodeId(index);
        var checkpointId = $"historical-bound-human-input-checkpoint-{index:D3}";
        var policy = HumanInputPolicyResolutionSnapshot.TryCreate(
            adapter.WorkspaceId,
            adapter.ExecutionBinding.Revision.GraphId,
            adapter.ExecutionBinding.Revision.RevisionId,
            nodeId,
            template.ResolvedPolicy.ActorId,
            template.ResolvedPolicy.TimeoutPolicy,
            template.ResolvedPolicy.FailurePolicy,
            template.ResolvedPolicy.ResolvedAtUtc);
        Assert.NotNull(policy);
        var binding = new GovernedLoopHumanInputWaitingCheckpointBinding(
            GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion,
            adapter.WorkspaceId,
            adapter.ExecutionBinding,
            template.Binding.Publication,
            adapter.GraphArtifactHash,
            adapter.GraphLayoutHash,
            adapter.AdmissionReceiptHash,
            frontier.Payload.FrontierVersion,
            frontier.Payload.ContentHash,
            index,
            null,
            null,
            nodeId,
            1,
            checkpointId);
        var request = HumanInputRequestHash.Apply(new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            $"historical-bound-human-input-request-{index:D3}",
            $"historical-bound-human-input-request-version-{index:D3}",
            new HumanInputRequestBinding(adapter.WorkspaceId, adapter.ExecutionBinding.Revision.GraphId, adapter.ExecutionBinding.Revision.RevisionId, nodeId, adapter.ExecutionBinding.RunId, checkpointId),
            template.NodeConfiguration.Purpose!,
            template.NodeConfiguration.Prompt!,
            template.NodeConfiguration.ResponseSchema!,
            template.NodeConfiguration.PrivacyClass,
            template.NodeConfiguration.EligibleRespondents!.Select(item => item!).ToArray(),
            new HumanInputTiming(policy.ResolvedAtUtc, policy.ExpiresAtUtc),
            template.NodeConfiguration.ResponsePolicy!,
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, nodeId, checkpointId),
            string.Empty));
        var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion,
            1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published,
            policy.ResolvedAtUtc,
            null,
            null,
            null,
            null,
            null,
            string.Empty,
            string.Empty));
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            binding,
            template.NodeConfiguration,
            policy,
            request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Pending,
            [evidence],
            string.Empty));
    }

    private static string HistoricalBoundHumanInputNodeId(int index) => $"historical-bound-human-input-node-{index:D3}";

    private static CustomLoopRunStoreResult CheckpointRetirementResult(CustomLoopRunStoreStatus status, CustomLoopRunRecord run)
        => status switch
        {
            CustomLoopRunStoreStatus.Conflict => CustomLoopRunStoreResult.VersionConflict(run, run.LifecycleVersion),
            CustomLoopRunStoreStatus.TerminalImmutable => CustomLoopRunStoreResult.TerminalImmutable(run, run.LifecycleVersion),
            CustomLoopRunStoreStatus.NotFound => CustomLoopRunStoreResult.NotFound(),
            _ => throw new InvalidOperationException("The test checkpoint-retirement result is unsupported."),
        };

    private static GovernedLoopSleepCurrentPosture HumanInputContinuationPosture(SequentialTestContext context, CustomLoopRunRecord run)
    {
        var lifecycle = GovernedLoopRunLifecycle.Create(
            context.Anchor.AdapterBinding.ExecutionBinding,
            GovernedLoopRunLifecyclePayload.Create(
                1,
                run.LifecycleVersion,
                GovernedLoopRunStatus.Waiting,
                run.CreatedAtUtc,
                run.UpdatedAtUtc,
                null));
        var execution = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, run.Frontier!, [], []);
        return new GovernedLoopSleepCurrentPosture(
            execution,
            context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication,
            true,
            GovernedLoopSleepApplicationTestFixture.Hash('f'),
            null,
            _now.AddSeconds(3),
            GovernedLoopSleepApplicationTestFixture.Hash('9'));
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
