using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Theory]
    [InlineData("select-a", GovernedLoopControlCondition.True, "branch-a", "branch-b", "branch-a-to-join")]
    [InlineData("select-b", GovernedLoopControlCondition.False, "branch-b", "branch-a", "branch-b-to-join")]
    public async Task Canonical_condition_dispatches_only_the_selected_branch_and_releases_SelectedJoin_after_pruning(
        string providerOutput,
        GovernedLoopControlCondition expectedOutcome,
        string selectedNodeId,
        string skippedNodeId,
        string arrivalEdgeId)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ConditionalSelectedJoinArtifact(role));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result(providerOutput));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, $"{result.Status}: {result.Detail}");
        Assert.Equal(["infer-01"], executor.Requests.Select(request => request.StepId));
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(result.Run!.Frontier);
        var condition = Assert.Single(frontier.Payload.Nodes, node => string.Equals(node.NodeId, "condition", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, condition.Status);
        Assert.Equal(expectedOutcome, condition.ControlOutcome);
        Assert.Equal(
            [expectedOutcome == GovernedLoopControlCondition.True ? "condition-true" : "condition-false"],
            condition.SelectedControlEdgeIds);
        Assert.Equal(
            [expectedOutcome == GovernedLoopControlCondition.True ? "condition-false" : "condition-true"],
            condition.SkippedControlEdgeIds);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, Assert.Single(frontier.Payload.Nodes, node => string.Equals(node.NodeId, selectedNodeId, StringComparison.Ordinal)).Status);
        Assert.DoesNotContain(frontier.Payload.Nodes, node => string.Equals(node.NodeId, skippedNodeId, StringComparison.Ordinal));
        var join = Assert.Single(frontier.Payload.Nodes, node => string.Equals(node.NodeId, "join", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, join.Status);
        var arrival = Assert.Single(join.JoinArrivals);
        Assert.Equal(arrivalEdgeId, arrival.ControlEdgeId);
        Assert.Equal(selectedNodeId, frontier.Payload.Nodes[arrival.SourceActivationOrdinal].NodeId);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.TopologyNodeSkipped);
        Assert.Equal(["infer-01", "condition", selectedNodeId, "join", "exit"], evidence.Requests.Select(item => item.Dispatch.Node.NodeId));
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Canonical_unrecognized_model_decision_parks_attention_with_exact_route_evidence_and_no_branch_dispatch()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: ModelDecisionSelectedJoinArtifact);
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("not-an-admitted-decision"));
        var audit = new RecordingAuditLog();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, audit: audit),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(
            result.Status == CustomLoopOrderedRunStatus.NeedsReview,
            $"{result.Status}: {result.Detail} {result.Run?.FailureCode}: {result.Run?.FailureDetail} Validation: {string.Join("; ", store.ValidationFailures.Select(item => item.Code + " at " + item.Field + ": " + item.Message))}");
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("canonical_condition_route_ambiguous", result.Run.FailureCode);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, result.Run.Frontier!.Payload.Status);
        var condition = Assert.Single(result.Run.Frontier.Payload.Nodes, node => node.NodeId == "condition");
        Assert.Equal(GovernedLoopNodeExecutionStatus.ReviewBlocked, condition.Status);
        Assert.Null(condition.ControlOutcome);
        Assert.Empty(condition.SelectedControlEdgeIds);
        Assert.Empty(condition.SkippedControlEdgeIds);
        Assert.DoesNotContain(result.Run.Frontier.Payload.Nodes, node => node.NodeId is "branch-a" or "branch-b" or "join" or "exit");
        Assert.Single(executor.Requests);
        var start = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is
        {
            NodeId: "condition",
            Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
        });
        var attention = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is
        {
            NodeId: "condition",
            Kind: CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention,
            Disposition: CustomLoopSequentialNodeDisposition.NeedsReview,
        });
        Assert.Single(evidence.AuditRequests, item => item.OperationId == GovernedLoopSequentialAuditOperationId.ForNodeStart(start.SequentialNodeEvidence!.EvidenceHash));
        Assert.Single(evidence.AuditRequests, item => item.OperationId == GovernedLoopSequentialAuditOperationId.ForNodeOutcome(attention.SequentialNodeEvidence!.EvidenceHash));
        AssertSequentialPureTerminalAuditOnce(result.Run, CustomLoopRunStatus.NeedsReview, evidence, audit);
        Assert.Empty(store.ValidationFailures);
    }

    [Theory]
    [InlineData(GovernedLoopJoinPolicy.All, 2)]
    [InlineData(GovernedLoopJoinPolicy.Any, 1)]
    [InlineData(GovernedLoopJoinPolicy.Selected, 2)]
    public async Task Canonical_fanout_join_retains_the_exact_stably_ordered_arrival_snapshot(
        GovernedLoopJoinPolicy joinPolicy,
        int expectedArrivalCount)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ParallelJoinArtifact(role, JoinDescriptor(joinPolicy)));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("fanout input"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(result.Status == CustomLoopOrderedRunStatus.Completed, $"{result.Status}: {result.Detail}");
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(result.Run!.Frontier);
        var join = Assert.Single(frontier.Payload.Nodes, node => string.Equals(node.NodeId, "join", StringComparison.Ordinal));
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, join.Status);
        Assert.Equal(expectedArrivalCount, join.JoinArrivals.Count);
        Assert.Equal(
            join.JoinArrivals.OrderBy(arrival => arrival.ControlEdgeId, StringComparer.Ordinal),
            join.JoinArrivals);
        var expectedEdges = joinPolicy == GovernedLoopJoinPolicy.Any
            ? new[] { "z-branch-a-to-join" }
            : ["a-branch-b-to-join", "z-branch-a-to-join"];
        Assert.Equal(expectedEdges, join.JoinArrivals.Select(arrival => arrival.ControlEdgeId));
        var expectedSources = joinPolicy == GovernedLoopJoinPolicy.Any
            ? new[] { "branch-a" }
            : ["branch-b", "branch-a"];
        Assert.Equal(expectedSources, join.JoinArrivals.Select(arrival => frontier.Payload.Nodes[arrival.SourceActivationOrdinal].NodeId));
        Assert.Equal(["infer-01", "branch-a", "branch-b", "join", "exit"], evidence.Requests.Select(item => item.Dispatch.Node.NodeId));
        Assert.Empty(store.ValidationFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_multi_ready_boundary_pause_or_cancel_preserves_every_undispatched_activation(bool cancel)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ParallelJoinArtifact(role, GovernedLoopSequentialNodeDescriptors.AllJoin));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("fanout input"));
        var audit = new RecordingAuditLog();
        var runner = Runner(store, executor, audit: audit);
        var lifecycle = Lifecycle(store, runner, audit);
        CustomLoopControlResult? control = null;
        GovernedLoopNodeExecutionEvidence[]? readyBeforeControl = null;
        long? checkpointSequenceBeforeControl = null;
        var injected = false;
        store.AfterUpdate = async candidate =>
        {
            var ready = candidate.Frontier?.Payload.Nodes
                .Where(node => node.Status == GovernedLoopNodeExecutionStatus.Ready && node.NodeId is "branch-a" or "branch-b")
                .OrderBy(node => node.ActivationOrdinal)
                .ToArray();
            if (injected
                || ready is not { Length: 2 }
                || candidate.Events[^1].Kind != CustomLoopRunEventKind.CheckpointCommitted
                || candidate.Checkpoint.LastCommittedSequence != candidate.Events[^1].Sequence)
            {
                return;
            }

            injected = true;
            readyBeforeControl = ready;
            checkpointSequenceBeforeControl = candidate.Checkpoint.LastCommittedSequence;
            control = cancel
                ? await lifecycle.CancelAsync(new CustomLoopCancelRequest(candidate.Id, candidate.LifecycleVersion, "cancel-multi-ready-frontier", AuditSchema.Actors.Web))
                : await lifecycle.PauseAsync(new CustomLoopPauseRequest(candidate.Id, candidate.LifecycleVersion, "pause-multi-ready-frontier", AuditSchema.Actors.Web));
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(runner, evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(injected);
        Assert.Equal(cancel ? CustomLoopControlStatus.CancelRequested : CustomLoopControlStatus.PauseRequested, control!.Status);
        var expectedStatus = cancel ? CustomLoopOrderedRunStatus.Cancelled : CustomLoopOrderedRunStatus.Paused;
        Assert.True(
            result.Status == expectedStatus,
            $"Expected {expectedStatus}, received {result.Status}: {result.Run?.FailureCode}/{result.Run?.FailureDetail}. {result.Detail} Validation: {string.Join("; ", store.ValidationFailures.Select(error => error.Code + ": " + error.Message))}");
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(result.Run!.Frontier);
        Assert.Equal(cancel ? GovernedLoopFrontierStatus.Cancelled : GovernedLoopFrontierStatus.Active, frontier.Payload.Status);
        var retainedReady = frontier.Payload.Nodes
            .Where(node => node.NodeId is "branch-a" or "branch-b")
            .OrderBy(node => node.ActivationOrdinal)
            .ToArray();
        Assert.Equal(readyBeforeControl!.Select(node => node.ActivationOrdinal), retainedReady.Select(node => node.ActivationOrdinal));
        Assert.All(retainedReady, node =>
        {
            Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, node.Status);
            Assert.Null(node.Attempt);
            Assert.Null(node.AttemptOperationId);
        });
        Assert.Equal(["infer-01"], executor.Requests.Select(request => request.StepId));
        Assert.Equal(checkpointSequenceBeforeControl, result.Run.Checkpoint.LastCommittedSequence);
        Assert.Equal(CustomLoopRunEventKind.CheckpointCommitted, result.Run.Events[(int)result.Run.Checkpoint.LastCommittedSequence - 1].Kind);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted && item.StepId is "branch-a" or "branch-b");
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Canonical_condition_claim_cancellation_before_commit_cancels_without_topology_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ConditionalSelectedJoinArtifact(role));
        var injected = false;
        var store = new FakeRunStore(context.Run)
        {
            BeforeUpdate = (candidate, token) =>
            {
                if (injected || !HasTopologyDispatchStart(candidate, "condition"))
                {
                    return;
                }

                injected = true;
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };
        var executor = new QueueExecutor(Result("select-a"));
        var audit = new RecordingAuditLog();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor, audit: audit), evidence, evidence);

        var result = await adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        Assert.True(injected);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Cancelled, result.Run.Frontier!.Payload.Status);
        Assert.Single(executor.Requests);
        Assert.DoesNotContain(result.Run.Events, item => item.SequentialNodeEvidence?.NodeId == "condition");
        Assert.DoesNotContain(result.Run.Events, item => item.StepId is "branch-a" or "branch-b");
        Assert.Empty(store.ValidationFailures);
        AssertSequentialPureTerminalAuditOnce(result.Run, CustomLoopRunStatus.Cancelled, evidence, audit);
    }

    [Theory]
    [InlineData(TopologyClaimReadFailure.Unavailable, CustomLoopOrderedRunStatus.NeedsReview)]
    [InlineData(TopologyClaimReadFailure.Missing, CustomLoopOrderedRunStatus.NotFound)]
    [InlineData(TopologyClaimReadFailure.Corrupt, CustomLoopOrderedRunStatus.Conflict)]
    [InlineData(TopologyClaimReadFailure.Diverged, CustomLoopOrderedRunStatus.Conflict)]
    public async Task Cancelled_atomic_condition_claim_fails_closed_when_its_durable_outcome_cannot_be_proved(
        TopologyClaimReadFailure readFailure,
        CustomLoopOrderedRunStatus expectedStatus)
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ConditionalSelectedJoinArtifact(role));
        var injected = false;
        FakeRunStore? store = null;
        store = new FakeRunStore(context.Run)
        {
            BeforeUpdate = (candidate, token) =>
            {
                if (injected || !HasTopologyDispatchStart(candidate, "condition"))
                {
                    return;
                }

                injected = true;
                switch (readFailure)
                {
                    case TopologyClaimReadFailure.Unavailable:
                        store!.GetException = new IOException("Simulated unavailable topology reconciliation read.");
                        break;
                    case TopologyClaimReadFailure.Missing:
                        store!.ReturnMissing = true;
                        break;
                    case TopologyClaimReadFailure.Corrupt:
                        store!.ReadSubstitutionFactory = current => current with { LifecycleVersion = 0 };
                        break;
                    case TopologyClaimReadFailure.Diverged:
                        store!.ReadSubstitutionFactory = current => CreatePureControlSuccessor(
                            current,
                            CustomLoopRunStatus.PauseRequested,
                            "pause-raced-atomic-topology-claim",
                            "A concurrent controller paused while the atomic topology claim was unresolved.",
                            current.UpdatedAtUtc.AddSeconds(1));
                        break;
                }

                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };
        var executor = new QueueExecutor(Result("select-a"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        Assert.True(injected);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Single(executor.Requests);
        Assert.DoesNotContain(store.Current.Events, item => item.SequentialNodeEvidence?.NodeId == "condition");
        Assert.DoesNotContain(store.Current.Events, item => item.StepId is "branch-a" or "branch-b");
        Assert.Empty(store.ValidationFailures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_join_claim_cancellation_after_commit_adopts_exact_outcome_and_cancels_without_redispatch(
        bool persistenceThrowsCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ParallelJoinArtifact(role, GovernedLoopSequentialNodeDescriptors.AllJoin));
        var injected = false;
        var store = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (injected || !HasTopologyDispatchStart(candidate, "join"))
                {
                    return Task.CompletedTask;
                }

                injected = true;
                cancellation.Cancel();
                return persistenceThrowsCancellation
                    ? Task.FromException(new OperationCanceledException(cancellation.Token))
                    : Task.CompletedTask;
            },
        };
        var executor = new QueueExecutor(Result("fanout input"));
        var audit = new RecordingAuditLog();
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor, audit: audit), evidence, evidence);

        var result = await adapter.RunAsync(
            new GovernedLoopSequentialOrderedRunRequest(1, context.Anchor, context.Plan, context.Artifact, AuditSchema.Actors.Web),
            cancellation.Token);

        Assert.True(injected);
        Assert.Equal(CustomLoopOrderedRunStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, result.Run!.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Cancelled, result.Run.Frontier!.Payload.Status);
        Assert.Single(executor.Requests);
        var start = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { NodeId: "join", Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted });
        var outcome = Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is { NodeId: "join", Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome });
        Assert.Single(
            evidence.AuditRequests,
            item => string.Equals(item.OperationId, GovernedLoopSequentialAuditOperationId.ForNodeStart(start.SequentialNodeEvidence!.EvidenceHash), StringComparison.Ordinal)
                && item.AuditEvent.Outcome == AuditSchema.Outcomes.Started);
        Assert.Single(
            evidence.AuditRequests,
            item => string.Equals(item.OperationId, GovernedLoopSequentialAuditOperationId.ForNodeOutcome(outcome.SequentialNodeEvidence!.EvidenceHash), StringComparison.Ordinal)
                && item.AuditEvent.Outcome == AuditSchema.Outcomes.Succeeded);
        Assert.DoesNotContain(result.Run.Events, item => item.SequentialNodeEvidence?.NodeId == "exit");
        Assert.Empty(store.ValidationFailures);
        AssertSequentialPureTerminalAuditOnce(result.Run, CustomLoopRunStatus.Cancelled, evidence, audit);
    }

    [Theory]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.None, TopologyAuditFailure.None)]
    [InlineData(true, "join", "fanout input", TopologyFrontierConflict.None, TopologyAuditFailure.None)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.Matching, TopologyAuditFailure.None)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.ReadUnavailable, TopologyAuditFailure.None)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.Divergent, TopologyAuditFailure.None)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.None, TopologyAuditFailure.StartConflict)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.None, TopologyAuditFailure.StartUnavailable)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.None, TopologyAuditFailure.OutcomeConflict)]
    [InlineData(false, "condition", "select-a", TopologyFrontierConflict.None, TopologyAuditFailure.OutcomeUnavailable)]
    public async Task Restart_recovery_reconciles_a_retained_topology_outcome_without_provider_or_pure_node_redispatch(
        bool captureJoin,
        string retainedNodeId,
        string providerOutput,
        TopologyFrontierConflict frontierConflict,
        TopologyAuditFailure auditFailure)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => captureJoin
                ? ParallelJoinArtifact(role, GovernedLoopSequentialNodeDescriptors.AllJoin)
                : ConditionalSelectedJoinArtifact(role));
        CustomLoopRunRecord? retainedOutcome = null;
        var crashingStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedOutcome is null
                    && candidate.Events[^1] is
                    {
                        Kind: CustomLoopRunEventKind.NodeAttemptCompleted,
                        SequentialNodeEvidence:
                        {
                            Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                        } evidence,
                    }
                    && string.Equals(evidence.NodeId, retainedNodeId, StringComparison.Ordinal))
                {
                    retainedOutcome = candidate;
                    throw new IOException("Simulated process loss after the deterministic topology outcome became durable.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result(providerOutput));
        var firstEvidence = new SequentialEvidenceHarness(crashingStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(crashingStore, firstExecutor),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        var recoveryStore = new FakeRunStore(Assert.IsType<CustomLoopRunRecord>(retainedOutcome));
        var recoveryAudit = new RecordingAuditLog();
        var recovery = Assert.Single(await new CustomLoopRecoveryService(
            recoveryStore,
            recoveryAudit,
            new FixedTimeProvider(_now.AddMinutes(1))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Paused, recovery.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, recovery.Run.Frontier!.Payload.Nodes[^1].Status);
        Assert.All(recoveryAudit.Events, item =>
        {
            Assert.Equal(false, item.Metadata["openAttemptAfterCheckpoint"]);
            Assert.Equal(true, item.Metadata["restartSafeDeterministicAttemptAfterCheckpoint"]);
        });

        var resumable = ResumeReady(recovery.Run, $"resume-retained-{retainedNodeId}-outcome");
        var matchingConflictInjected = false;
        FakeRunStore? resumedStore = null;
        resumedStore = new FakeRunStore(resumable)
        {
            RawConflictSuccessorFactory = (current, candidate) =>
            {
                if (frontierConflict == TopologyFrontierConflict.None
                    || matchingConflictInjected
                    || current.Frontier?.Payload.Nodes.SingleOrDefault(node => string.Equals(node.NodeId, retainedNodeId, StringComparison.Ordinal))?.Status != GovernedLoopNodeExecutionStatus.Running
                    || candidate.Frontier?.Payload.Nodes.SingleOrDefault(node => string.Equals(node.NodeId, retainedNodeId, StringComparison.Ordinal))?.Status != GovernedLoopNodeExecutionStatus.Completed)
                {
                    return null;
                }

                matchingConflictInjected = true;
                return frontierConflict switch
                {
                    TopologyFrontierConflict.Matching => candidate,
                    TopologyFrontierConflict.ReadUnavailable => SetUnavailableAndReturn(candidate),
                    TopologyFrontierConflict.Divergent => CreatePureControlSuccessor(
                        current,
                        CustomLoopRunStatus.PauseRequested,
                        "pause-raced-topology-frontier",
                        "A concurrent controller changed the durable run while topology advancement reconciled.",
                        current.UpdatedAtUtc.AddSeconds(1)),
                    _ => throw new InvalidOperationException("Unexpected topology-frontier conflict."),
                };

                CustomLoopRunRecord SetUnavailableAndReturn(CustomLoopRunRecord exactCandidate)
                {
                    resumedStore!.GetException = new IOException("Simulated unavailable topology-frontier reconciliation read.");
                    return exactCandidate;
                }
            },
        };
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence)
        {
            ForcedAuditStatusFactory = auditEvent => auditFailure switch
            {
                TopologyAuditFailure.StartConflict when auditEvent.Outcome == AuditSchema.Outcomes.Started
                    => GovernedLoopSequentialAuditRecordStatus.Conflict,
                TopologyAuditFailure.StartUnavailable when auditEvent.Outcome == AuditSchema.Outcomes.Started
                    => GovernedLoopSequentialAuditRecordStatus.Unavailable,
                TopologyAuditFailure.OutcomeConflict when auditEvent.Outcome == AuditSchema.Outcomes.Succeeded
                    => GovernedLoopSequentialAuditRecordStatus.Conflict,
                TopologyAuditFailure.OutcomeUnavailable when auditEvent.Outcome == AuditSchema.Outcomes.Succeeded
                    => GovernedLoopSequentialAuditRecordStatus.Unavailable,
                _ => null,
            },
        };
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(resumedStore, resumedExecutor),
            resumedEvidence,
            resumedEvidence);

        var resumed = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            $"resume-retained-{retainedNodeId}-outcome",
            AuditSchema.Actors.Web));

        var expectedStatus = auditFailure != TopologyAuditFailure.None
            || frontierConflict == TopologyFrontierConflict.ReadUnavailable
                ? CustomLoopOrderedRunStatus.NeedsReview
                : frontierConflict == TopologyFrontierConflict.Divergent
                    ? CustomLoopOrderedRunStatus.InvalidState
                    : CustomLoopOrderedRunStatus.Completed;
        Assert.True(
            resumed.Status == expectedStatus,
            $"Expected {expectedStatus}, received {resumed.Status}: {resumed.Detail} {resumed.Run?.FailureCode}/{resumed.Run?.FailureDetail}");
        Assert.Single(firstExecutor.Requests);
        Assert.Empty(resumedExecutor.Requests);
        var durableAfterResume = resumed.Run ?? resumedStore.Current;
        var retainedCompletion = Assert.Single(durableAfterResume.Events, item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
        } evidence && string.Equals(evidence.NodeId, retainedNodeId, StringComparison.Ordinal));
        var outcomeAudits = resumedEvidence.AuditRequests.Where(
            item => string.Equals(
                item.OperationId,
                GovernedLoopSequentialAuditOperationId.ForNodeOutcome(retainedCompletion.SequentialNodeEvidence!.EvidenceHash),
                StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            auditFailure is TopologyAuditFailure.StartConflict or TopologyAuditFailure.StartUnavailable ? 0 : 1,
            outcomeAudits.Length);
        if (auditFailure != TopologyAuditFailure.None)
        {
            Assert.Equal(auditFailure switch
            {
                TopologyAuditFailure.StartConflict => "canonical_start_audit_conflict",
                TopologyAuditFailure.StartUnavailable => "canonical_start_audit_unavailable",
                TopologyAuditFailure.OutcomeConflict => "canonical_outcome_audit_conflict",
                TopologyAuditFailure.OutcomeUnavailable => "canonical_outcome_audit_unavailable",
                _ => throw new InvalidOperationException("Unexpected topology audit failure."),
            }, resumed.Run!.FailureCode);
        }

        Assert.Equal(frontierConflict != TopologyFrontierConflict.None, matchingConflictInjected);
        Assert.Empty(resumedStore.ValidationFailures);
    }

    [Fact]
    public async Task Resume_of_a_legacy_running_topology_claim_without_terminal_route_parks_review_without_re_evaluation()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: ConditionalSelectedJoinArtifact);
        CustomLoopRunRecord? retainedAtomicClaim = null;
        var interruptedStore = new FakeRunStore(context.Run)
        {
            AfterUpdate = candidate =>
            {
                if (retainedAtomicClaim is null
                    && candidate.Events[^1].SequentialNodeEvidence is
                    {
                        NodeId: "condition",
                        Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                    })
                {
                    retainedAtomicClaim = candidate;
                    throw new IOException("Simulated loss after the atomic topology write for legacy-trace reconstruction.");
                }

                return Task.CompletedTask;
            },
        };
        var firstExecutor = new QueueExecutor(Result("select-a"));
        var firstEvidence = new SequentialEvidenceHarness(interruptedStore, context.Evidence);
        var firstAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(interruptedStore, firstExecutor),
            firstEvidence,
            firstEvidence);

        _ = await firstAdapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        var atomicClaim = Assert.IsType<CustomLoopRunRecord>(retainedAtomicClaim);
        Assert.Equal(CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, atomicClaim.Events[^1].SequentialNodeEvidence!.Kind);
        var legacyStartOnly = atomicClaim with { Events = atomicClaim.Events[..^1] };
        var legacyValidation = CustomLoopRunValidator.Validate(legacyStartOnly);
        Assert.True(legacyValidation.IsValid, string.Join(Environment.NewLine, legacyValidation.Errors));
        var resumable = ResumeReady(legacyStartOnly, "resume-legacy-running-topology-claim");
        var resumedStore = new FakeRunStore(resumable);
        var resumedExecutor = new QueueExecutor();
        var resumedEvidence = new SequentialEvidenceHarness(resumedStore, context.Evidence);
        var resumedAdapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(resumedStore, resumedExecutor),
            resumedEvidence,
            resumedEvidence);

        var result = await resumedAdapter.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            resumable.LifecycleVersion,
            "resume-legacy-running-topology-claim",
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("canonical_open_frontier_attempt_requires_review", result.Run!.FailureCode);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run.Status);
        Assert.Single(firstExecutor.Requests);
        Assert.Empty(resumedExecutor.Requests);
        Assert.DoesNotContain(result.Run.Events, item => item.StepId is "branch-a" or "branch-b");
        Assert.Empty(resumedStore.ValidationFailures);
    }

    [Fact]
    public async Task Canonical_cycle_duration_expiry_before_claim_does_not_start_or_dispatch_the_second_provider_visit()
    {
        const int MaximumDurationMilliseconds = 10;
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ProviderConditionCycleArtifact(role, MaximumDurationMilliseconds));
        var store = new FakeRunStore(context.Run);
        var clock = new MutableTimeProvider(_now);
        var advanced = false;
        store.AfterUpdate = candidate =>
        {
            if (!advanced
                && candidate.Frontier?.Payload.Nodes.Any(node => string.Equals(node.NodeId, "infer-01", StringComparison.Ordinal)
                    && node.VisitOrdinal == 2
                    && node.Status == GovernedLoopNodeExecutionStatus.Ready) == true)
            {
                advanced = true;
                clock.Advance(TimeSpan.FromMilliseconds(MaximumDurationMilliseconds + 1));
            }

            return Task.CompletedTask;
        };
        var executor = new QueueExecutor(Result("continue"), Result("must not dispatch"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, timeProvider: clock),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(advanced);
        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("canonical_cycle_duration_exceeded", result.Run!.FailureCode);
        Assert.Single(executor.Requests);
        Assert.Equal(1, executor.ProviderRequestStartedCount);
        var inferenceStarts = result.Run.Events.Where(item => item.Kind == CustomLoopRunEventKind.NodeAttemptStarted && string.Equals(item.StepId, "infer-01", StringComparison.Ordinal)).ToArray();
        Assert.Single(inferenceStarts);
        Assert.Equal(1, inferenceStarts[0].SequentialNodeEvidence!.VisitOrdinal);
        Assert.DoesNotContain(result.Run.Events, item => item.SequentialNodeEvidence is { NodeId: "infer-01", VisitOrdinal: 2, Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted });
        Assert.Empty(store.ValidationFailures);
    }

    [Fact]
    public async Task Canonical_cycle_iteration_exhaustion_does_not_create_or_dispatch_an_unadmitted_third_visit()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => ProviderConditionCycleArtifact(role, maximumDurationMilliseconds: 60_000));
        var store = new FakeRunStore(context.Run);
        var executor = new QueueExecutor(Result("continue"), Result("continue"), Result("must not dispatch"));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(Runner(store, executor), evidence, evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            1,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal("canonical_topology_frontier_advancement_failed", result.Run!.FailureCode);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(2, executor.ProviderRequestStartedCount);
        var inferenceStarts = result.Run.Events
            .Where(item => item.SequentialNodeEvidence is { NodeId: "infer-01", Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted })
            .OrderBy(item => item.SequentialNodeEvidence!.VisitOrdinal)
            .ToArray();
        Assert.Equal([1, 2], inferenceStarts.Select(item => item.SequentialNodeEvidence!.VisitOrdinal).ToArray());
        Assert.DoesNotContain(result.Run.Frontier!.Payload.Nodes, node => node.NodeId == "infer-01" && node.VisitOrdinal == 3);
        Assert.Empty(store.ValidationFailures);
    }

    public enum TopologyClaimReadFailure
    {
        Unavailable,
        Missing,
        Corrupt,
        Diverged,
    }

    public enum TopologyAuditFailure
    {
        None,
        StartConflict,
        StartUnavailable,
        OutcomeConflict,
        OutcomeUnavailable,
    }

    public enum TopologyFrontierConflict
    {
        None,
        Matching,
        ReadUnavailable,
        Divergent,
    }

    private static bool HasTopologyDispatchStart(CustomLoopRunRecord run, string nodeId)
        => run.Events.Any(item => item.SequentialNodeEvidence is
        {
            Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
        } evidence && string.Equals(evidence.NodeId, nodeId, StringComparison.Ordinal));

    private static GovernedLoopGraphRevisionArtifact ConditionalSelectedJoinArtifact(ContextualRoleRevisionPin owningRole)
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer-01"),
            Condition("condition", "select-a"),
            Identity("branch-a"),
            Identity("branch-b"),
            Join("join", GovernedLoopSequentialNodeDescriptors.SelectedJoin),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer-01", GovernedLoopControlCondition.Always),
            Edge("infer-to-condition", "infer-01", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-true", "condition", "branch-a", GovernedLoopControlCondition.True),
            Edge("condition-false", "condition", "branch-b", GovernedLoopControlCondition.False),
            Edge("branch-a-to-join", "branch-a", "join", GovernedLoopControlCondition.Success),
            Edge("branch-b-to-join", "branch-b", "join", GovernedLoopControlCondition.Success),
            Edge("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
        };
        return TopologyArtifact(nodes, edges, owningRole, includeCondition: true);
    }

    private static GovernedLoopGraphRevisionArtifact ModelDecisionSelectedJoinArtifact(ContextualRoleRevisionPin owningRole)
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer-01"),
            new GovernedLoopNodeDefinition(
                "condition",
                GovernedLoopSequentialNodeDescriptors.ModelDecisionCondition,
                [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.DecisionPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>
                {
                    [GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter] = "approve",
                    [GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter] = "reject",
                }),
            Identity("branch-a"),
            Identity("branch-b"),
            Join("join", GovernedLoopSequentialNodeDescriptors.SelectedJoin),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer-01", GovernedLoopControlCondition.Always),
            Edge("infer-to-condition", "infer-01", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-true", "condition", "branch-a", GovernedLoopControlCondition.True),
            Edge("condition-false", "condition", "branch-b", GovernedLoopControlCondition.False),
            Edge("branch-a-to-join", "branch-a", "join", GovernedLoopControlCondition.Success),
            Edge("branch-b-to-join", "branch-b", "join", GovernedLoopControlCondition.Success),
            Edge("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
        };
        return TopologyArtifact(
            nodes,
            edges,
            owningRole,
            includeCondition: true,
            conditionPortId: GovernedLoopTopologyNodeVocabulary.DecisionPort);
    }

    private static GovernedLoopGraphRevisionArtifact ParallelJoinArtifact(
        ContextualRoleRevisionPin owningRole,
        GovernedLoopNodeDescriptor joinDescriptor)
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer-01"),
            Identity("branch-a"),
            Identity("branch-b"),
            Join("join", joinDescriptor),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer-01", GovernedLoopControlCondition.Always),
            Edge("infer-to-branch-a", "infer-01", "branch-a", GovernedLoopControlCondition.Success),
            Edge("infer-to-branch-b", "infer-01", "branch-b", GovernedLoopControlCondition.Success),
            Edge("z-branch-a-to-join", "branch-a", "join", GovernedLoopControlCondition.Success),
            Edge("a-branch-b-to-join", "branch-b", "join", GovernedLoopControlCondition.Success),
            Edge("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
        };
        return TopologyArtifact(nodes, edges, owningRole, includeCondition: false);
    }

    private static GovernedLoopGraphRevisionArtifact ProviderConditionCycleArtifact(
        ContextualRoleRevisionPin owningRole,
        int maximumDurationMilliseconds)
    {
        var cycleParameters = new Dictionary<string, string>
        {
            [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "2",
            [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = maximumDurationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var inference = GovernedLoopSequentialApplicationTestFixture.Inference("infer-01", "Continue the admitted bounded cycle.") with
        {
            Parameters = new Dictionary<string, string>(cycleParameters)
            {
                ["instruction"] = "Continue the admitted bounded cycle.",
            },
        };
        var conditionParameters = new Dictionary<string, string>(cycleParameters)
        {
            [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = "continue",
        };
        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
            [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            conditionParameters);
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            inference,
            condition,
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer-01", GovernedLoopControlCondition.Always),
            Edge("infer-to-condition", "infer-01", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-repeat", "condition", "infer-01", GovernedLoopControlCondition.True),
            Edge("condition-exit", "condition", "exit", GovernedLoopControlCondition.False),
        };
        var bindings = new GovernedLoopBindingDefinition[]
        {
            new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer-01", "request"),
            new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-01", "invocation-context"),
            new("result-to-condition", GovernedLoopBindingKind.Data, "infer-01", "result", "condition", GovernedLoopTopologyNodeVocabulary.ValuePort),
            new("result-to-exit", GovernedLoopBindingKind.Data, "infer-01", "result", "exit", "result"),
        };
        return GovernedLoopSequentialApplicationTestFixture.Artifact(nodes, edges, ["exit"], owningRole, bindings);
    }

    private static GovernedLoopGraphRevisionArtifact TopologyArtifact(
        IReadOnlyList<GovernedLoopNodeDefinition> nodes,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> edges,
        ContextualRoleRevisionPin owningRole,
        bool includeCondition,
        string conditionPortId = GovernedLoopTopologyNodeVocabulary.ValuePort)
    {
        var bindings = new List<GovernedLoopBindingDefinition>
        {
            new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer-01", "request"),
            new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-01", "invocation-context"),
            new("result-to-branch-a", GovernedLoopBindingKind.Data, "infer-01", "result", "branch-a", GovernedLoopPureNodeVocabulary.InputPort),
            new("result-to-branch-b", GovernedLoopBindingKind.Data, "infer-01", "result", "branch-b", GovernedLoopPureNodeVocabulary.InputPort),
            new("result-to-exit", GovernedLoopBindingKind.Data, "infer-01", "result", "exit", "result"),
        };
        if (includeCondition)
        {
            bindings.Add(new GovernedLoopBindingDefinition("result-to-condition", GovernedLoopBindingKind.Data, "infer-01", "result", "condition", conditionPortId));
        }

        return GovernedLoopSequentialApplicationTestFixture.Artifact(nodes, edges, ["exit"], owningRole, bindings);
    }

    private static GovernedLoopNodeDefinition Condition(string id, string expected)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
            [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string> { [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = expected });

    private static GovernedLoopNodeDefinition Identity(string id)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition Join(string id, GovernedLoopNodeDescriptor descriptor)
        => new(id, descriptor, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());

    private static GovernedLoopNodeDescriptor JoinDescriptor(GovernedLoopJoinPolicy joinPolicy)
        => joinPolicy switch
        {
            GovernedLoopJoinPolicy.All => GovernedLoopSequentialNodeDescriptors.AllJoin,
            GovernedLoopJoinPolicy.Any => GovernedLoopSequentialNodeDescriptors.AnyJoin,
            GovernedLoopJoinPolicy.Selected => GovernedLoopSequentialNodeDescriptors.SelectedJoin,
            _ => throw new ArgumentOutOfRangeException(nameof(joinPolicy)),
        };

    private static GovernedLoopControlEdgeDefinition Edge(
        string id,
        string fromNodeId,
        string toNodeId,
        GovernedLoopControlCondition condition)
        => new(id, fromNodeId, toNodeId, condition);
}
