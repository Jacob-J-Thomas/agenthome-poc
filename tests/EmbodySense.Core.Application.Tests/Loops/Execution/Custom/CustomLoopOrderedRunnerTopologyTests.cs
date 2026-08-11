using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
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
        bool includeCondition)
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
            bindings.Add(new GovernedLoopBindingDefinition("result-to-condition", GovernedLoopBindingKind.Data, "infer-01", "result", "condition", GovernedLoopTopologyNodeVocabulary.ValuePort));
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
