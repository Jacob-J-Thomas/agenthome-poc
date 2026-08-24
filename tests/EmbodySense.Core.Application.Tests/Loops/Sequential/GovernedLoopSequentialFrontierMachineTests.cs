using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialFrontierMachineTests
{
    private static readonly DateTimeOffset _startedAtUtc = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Initialize_commits_trigger_and_only_first_executable_as_reached_plan_prefix()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();

        var initialized = Initialize(context);

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, initialized.Status);
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(initialized.Frontier);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(frontier, context.AdapterBinding, context.Plan));
        Assert.Equal(1, frontier.Payload.FrontierVersion);
        Assert.Equal(1, frontier.Payload.ConcurrencyCeiling);
        Assert.Equal(GovernedLoopFrontierStatus.Active, frontier.Payload.Status);
        Assert.Collection(
            frontier.Payload.Nodes,
            trigger =>
            {
                Assert.Equal(0, trigger.PlanOrdinal);
                Assert.Equal(context.Plan.Nodes[0].NodeId, trigger.NodeId);
                Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, trigger.Status);
                Assert.Equal(1, trigger.Attempt);
                Assert.Equal("event-trigger", trigger.AttemptOperationId);
                Assert.Equal("event-trigger", trigger.OutcomeEvidenceId);
                Assert.Equal(Hash('a'), trigger.OutcomeEvidenceHash);
            },
            firstExecutable =>
            {
                Assert.Equal(1, firstExecutable.PlanOrdinal);
                Assert.Equal(context.Plan.Nodes[1].NodeId, firstExecutable.NodeId);
                Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, firstExecutable.Status);
                Assert.Null(firstExecutable.Attempt);
                Assert.Null(firstExecutable.AttemptOperationId);
                Assert.Null(firstExecutable.OutcomeEvidenceId);
                Assert.Null(firstExecutable.OutcomeEvidenceHash);
            });
        Assert.Equal(2, frontier.Payload.Nodes.Count);
        Assert.True(GovernedLoopFrontierContractHash.Matches(frontier));

        var selected = GovernedLoopSequentialFrontierMachine.Select(frontier, context.AdapterBinding, context.Plan);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Ready, selected.Status);
        Assert.Same(context.Plan.Nodes[1], selected.Node);
    }

    [Fact]
    public async Task Ready_running_completed_transitions_are_versioned_and_append_only_next_ordinal()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var inference = context.Plan.Nodes[1];

        var started = GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            inference,
            SelectedActivation(initial, context),
            1,
            "attempt-inference-1",
            _startedAtUtc.AddSeconds(1));
        var running = Frontier(started);

        Assert.Equal(2, running.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, running.Payload.Nodes[^1].Status);
        var runningSelection = GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Running, runningSelection.Status);
        Assert.Equal(1, runningSelection.Attempt);
        Assert.Equal("attempt-inference-1", runningSelection.AttemptOperationId);

        var completed = GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            inference,
            SelectedActivation(running, context),
            1,
            "attempt-inference-1",
            "event-inference-1-completed",
            Hash('b'),
            GovernedLoopControlCondition.Success,
            [],
            _startedAtUtc.AddSeconds(2));
        var advanced = Frontier(completed);

        Assert.Equal(3, advanced.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopFrontierStatus.Active, advanced.Payload.Status);
        Assert.Equal(3, advanced.Payload.Nodes.Count);
        Assert.Equal(
            [0, 1, 2],
            advanced.Payload.Nodes.Select(node => node.PlanOrdinal).ToArray());
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, advanced.Payload.Nodes[1].Status);
        Assert.Equal("event-inference-1-completed", advanced.Payload.Nodes[1].OutcomeEvidenceId);
        Assert.Equal(Hash('b'), advanced.Payload.Nodes[1].OutcomeEvidenceHash);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, advanced.Payload.Nodes[2].Status);
        Assert.Equal(context.Plan.Nodes[2].NodeId, advanced.Payload.Nodes[2].NodeId);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(advanced, context.AdapterBinding, context.Plan));
    }

    [Fact]
    public async Task Selection_and_advance_follow_plan_ordinal_not_lexical_node_identity()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(
            inferenceCount: 3,
            inferenceIds: ["z-last-lexically", "a-first-lexically", "m-middle"]);
        var frontier = Frontier(Initialize(context));

        for (var ordinal = 1; ordinal < context.Plan.Nodes.Count; ordinal++)
        {
            var selected = GovernedLoopSequentialFrontierMachine.Select(frontier, context.AdapterBinding, context.Plan);
            Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Ready, selected.Status);
            Assert.Same(context.Plan.Nodes[ordinal], selected.Node);
            var operationId = $"attempt-plan-{ordinal}";
            frontier = Frontier(GovernedLoopSequentialFrontierMachine.Start(
                frontier,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                selected.Activation,
                1,
                operationId,
                _startedAtUtc.AddSeconds(ordinal * 2 - 1)));
            frontier = Frontier(GovernedLoopSequentialFrontierMachine.CompleteRunning(
                frontier,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                SelectedActivation(frontier, context),
                1,
                operationId,
                $"event-plan-{ordinal}",
                Hash((char)('b' + ordinal)),
                GovernedLoopControlCondition.Success,
                [],
                _startedAtUtc.AddSeconds(ordinal * 2)));
        }

        Assert.Equal(GovernedLoopFrontierStatus.Completed, frontier.Payload.Status);
        Assert.Equal(context.Plan.Nodes.Count, frontier.Payload.Nodes.Count);
        Assert.Equal(
            context.Plan.Nodes.Select(node => node.NodeId).ToArray(),
            frontier.Payload.Nodes.Select(node => node.NodeId).ToArray());
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Terminal, GovernedLoopSequentialFrontierMachine.Select(frontier, context.AdapterBinding, context.Plan).Status);
    }

    [Fact]
    public async Task Exact_running_attempt_can_fail_or_review_block_without_exposing_later_nodes()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var node = context.Plan.Nodes[1];
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(initial, context),
            1,
            "attempt-running",
            _startedAtUtc.AddSeconds(1)));

        var completedWithUnroutedFailure = GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(running, context),
            1,
            "attempt-running",
            "event-invalid-completion",
            Hash('e'),
            GovernedLoopControlCondition.Failure,
            [],
            _startedAtUtc.AddSeconds(2));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, completedWithUnroutedFailure.Status);

        var failed = Frontier(GovernedLoopSequentialFrontierMachine.FailRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(running, context),
            1,
            "attempt-running",
            "event-failed",
            Hash('f'),
            GovernedLoopControlCondition.Failure,
            _startedAtUtc.AddSeconds(2)));
        Assert.Equal(GovernedLoopFrontierStatus.Failed, failed.Payload.Status);
        Assert.Equal(initial.Payload.Nodes.Count, failed.Payload.Nodes.Count);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, failed.Payload.Nodes[^1].Status);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(failed, context.AdapterBinding, context.Plan));

        var review = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(running, context),
            1,
            "attempt-running",
            null,
            null,
            _startedAtUtc.AddSeconds(2)));
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, review.Payload.Status);
        Assert.Equal(initial.Payload.Nodes.Count, review.Payload.Nodes.Count);
        Assert.Equal(GovernedLoopNodeExecutionStatus.ReviewBlocked, review.Payload.Nodes[^1].Status);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.ReviewBlocked, GovernedLoopSequentialFrontierMachine.Select(review, context.AdapterBinding, context.Plan).Status);
    }

    [Fact]
    public async Task Exact_known_failure_advances_only_its_admitted_failure_route()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(artifactFactory: role =>
        {
            var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(owningRole: role).Graph;
            var inference = source.Nodes.Single(node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference);
            var fail = GovernedLoopSequentialApplicationTestFixture.Node("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal);
            return GovernedLoopSequentialApplicationTestFixture.Artifact(
                source.Nodes.Append(fail).ToArray(),
                source.ControlEdges.Append(new GovernedLoopControlEdgeDefinition("infer-to-fail", inference.Id, fail.Id, GovernedLoopControlCondition.Failure)).ToArray(),
                [source.TerminalNodeIds.Single(), fail.Id],
                role,
                source.Bindings,
                source.ValueSchemas,
                source.OutputContract,
                source.AuthorityCeiling);
        });
        var initial = Frontier(Initialize(context));
        var selected = GovernedLoopSequentialFrontierMachine.Select(initial, context.AdapterBinding, context.Plan);
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            selected.Node,
            selected.Activation,
            1,
            "attempt-known-failure",
            _startedAtUtc.AddSeconds(1)));

        var routed = Frontier(GovernedLoopSequentialFrontierMachine.FailRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            selected.Node,
            SelectedActivation(running, context),
            1,
            "attempt-known-failure",
            "event-known-failure",
            Hash('f'),
            GovernedLoopControlCondition.Failure,
            _startedAtUtc.AddSeconds(2)));

        Assert.Equal(GovernedLoopFrontierStatus.Active, routed.Payload.Status);
        Assert.Collection(
            routed.Payload.Nodes,
            trigger => Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, trigger.Status),
            failed =>
            {
                Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, failed.Status);
                Assert.Equal(["infer-to-fail"], failed.SelectedControlEdgeIds);
                Assert.Single(failed.SkippedControlEdgeIds);
            },
            fail =>
            {
                Assert.Equal(GovernedLoopSequentialNodeDescriptors.FailTerminal, fail.Descriptor);
                Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, fail.Status);
                Assert.Equal(["infer-to-fail"], fail.IncomingControlEdgeIds);
            });
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(routed, context.AdapterBinding, context.Plan));
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Ready, GovernedLoopSequentialFrontierMachine.Select(routed, context.AdapterBinding, context.Plan).Status);
    }

    [Fact]
    public async Task Undispatched_ready_frontier_can_enter_aggregate_review_or_cancellation_without_fabricating_an_attempt()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));

        var review = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockAggregate(
            initial,
            context.AdapterBinding,
            _startedAtUtc.AddSeconds(1)));
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, review.Payload.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, review.Payload.Nodes[^1].Status);
        Assert.Null(review.Payload.Nodes[^1].Attempt);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(review, context.AdapterBinding, context.Plan));

        var cancelled = Frontier(GovernedLoopSequentialFrontierMachine.CancelCurrent(
            initial,
            context.AdapterBinding,
            _startedAtUtc.AddSeconds(1)));
        Assert.Equal(GovernedLoopFrontierStatus.Cancelled, cancelled.Payload.Status);
        Assert.Equal(
            initial.Payload.Nodes.Select(node => (node.PlanOrdinal, node.NodeId, node.Status, node.Attempt, node.AttemptOperationId, node.OutcomeEvidenceId, node.OutcomeEvidenceHash)),
            cancelled.Payload.Nodes.Select(node => (node.PlanOrdinal, node.NodeId, node.Status, node.Attempt, node.AttemptOperationId, node.OutcomeEvidenceId, node.OutcomeEvidenceHash)));
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(cancelled, context.AdapterBinding, context.Plan));
    }

    [Fact]
    public async Task Exact_ready_activation_can_atomically_retain_attempt_and_review_outcome_without_a_version_gap()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var selected = GovernedLoopSequentialFrontierMachine.Select(initial, context.AdapterBinding, context.Plan);

        var review = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockReady(
            initial,
            context.AdapterBinding,
            context.Plan,
            selected.Node,
            selected.Activation,
            1,
            "attempt-atomic-review",
            "event-atomic-review",
            Hash('a'),
            _startedAtUtc.AddSeconds(1)));

        Assert.Equal(initial.Payload.FrontierVersion + 1, review.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, review.Payload.Status);
        var blocked = Assert.Single(review.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        Assert.Equal(1, blocked.Attempt);
        Assert.Equal("attempt-atomic-review", blocked.AttemptOperationId);
        Assert.Equal("event-atomic-review", blocked.OutcomeEvidenceId);
        Assert.Equal(Hash('a'), blocked.OutcomeEvidenceHash);
        Assert.Null(blocked.ControlOutcome);
        Assert.Empty(blocked.SelectedControlEdgeIds);
        Assert.Empty(blocked.SkippedControlEdgeIds);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(review, context.AdapterBinding, context.Plan));
    }

    [Fact]
    public async Task Fan_out_ready_siblings_survive_aggregate_review_claimed_failure_review_and_cancellation()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(
            artifactFactory: GovernedLoopSequentialApplicationTestFixture.ParallelAllJoinArtifact);
        var fanOut = AdvanceInferenceToFanOut(context);
        var ready = fanOut.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.Ready).ToArray();
        Assert.Equal(["branch-a", "branch-b"], ready.Select(node => node.NodeId).Order(StringComparer.Ordinal).ToArray());

        var aggregateReview = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockAggregate(
            fanOut,
            context.AdapterBinding,
            _startedAtUtc.AddSeconds(3)));
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, aggregateReview.Payload.Status);
        Assert.All(ready.Zip(aggregateReview.Payload.Nodes.Skip(2)), pair => AssertNodeEqual(pair.First, pair.Second));
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(aggregateReview, context.AdapterBinding, context.Plan));

        var cancelled = Frontier(GovernedLoopSequentialFrontierMachine.Cancel(
            fanOut,
            context.AdapterBinding,
            context.Plan,
            _startedAtUtc.AddSeconds(3)));
        Assert.Equal(GovernedLoopFrontierStatus.Cancelled, cancelled.Payload.Status);
        Assert.All(fanOut.Payload.Nodes.Zip(cancelled.Payload.Nodes), pair => AssertNodeEqual(pair.First, pair.Second));

        var selection = GovernedLoopSequentialFrontierMachine.Select(fanOut, context.AdapterBinding, context.Plan);
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            fanOut,
            context.AdapterBinding,
            context.Plan,
            selection.Node,
            selection.Activation,
            1,
            "attempt-branch-a",
            _startedAtUtc.AddSeconds(3)));
        var runningActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(
            GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan).Activation);
        Assert.Single(running.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.Ready);

        var review = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            selection.Node,
            runningActivation,
            1,
            "attempt-branch-a",
            null,
            null,
            _startedAtUtc.AddSeconds(4)));
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, review.Payload.Status);
        Assert.Single(review.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.Ready);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(review, context.AdapterBinding, context.Plan));

        var failed = Frontier(GovernedLoopSequentialFrontierMachine.FailRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            selection.Node,
            runningActivation,
            1,
            "attempt-branch-a",
            "event-branch-failed",
            Hash('d'),
            GovernedLoopControlCondition.Failure,
            _startedAtUtc.AddSeconds(4)));
        Assert.Equal(GovernedLoopFrontierStatus.Failed, failed.Payload.Status);
        Assert.Single(failed.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.Ready);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(failed, context.AdapterBinding, context.Plan));
    }

    [Fact]
    public async Task Retry_waiting_exhaustion_terminalizes_the_exact_activation_while_a_sibling_remains_waiting()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(artifactFactory: ParallelRetryArtifact);
        var fanOut = AdvanceInferenceToFanOut(context);
        var branchA = context.Plan.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal));
        var branchB = context.Plan.Nodes.Single(node => string.Equals(node.NodeId, "branch-b", StringComparison.Ordinal));

        var runningA = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            fanOut,
            context.AdapterBinding,
            context.Plan,
            branchA,
            fanOut.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal)),
            1,
            "attempt-branch-a-1",
            _startedAtUtc.AddSeconds(3)));
        var waitingA = Frontier(GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            runningA,
            context.AdapterBinding,
            context.Plan,
            branchA,
            SelectedActivation(runningA, context),
            1,
            2,
            "attempt-branch-a-2",
            _startedAtUtc.AddSeconds(4)));
        var selectedB = GovernedLoopSequentialFrontierMachine.Select(waitingA, context.AdapterBinding, context.Plan);
        var runningB = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            waitingA,
            context.AdapterBinding,
            context.Plan,
            branchB,
            selectedB.Activation,
            1,
            "attempt-branch-b-1",
            _startedAtUtc.AddSeconds(5)));
        var waitingBoth = Frontier(GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            runningB,
            context.AdapterBinding,
            context.Plan,
            branchB,
            SelectedActivation(runningB, context),
            1,
            2,
            "attempt-branch-b-2",
            _startedAtUtc.AddSeconds(6)));

        var exhausted = Frontier(GovernedLoopSequentialFrontierMachine.FailWaiting(
            waitingBoth,
            context.AdapterBinding,
            context.Plan,
            branchA,
            waitingBoth.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal)),
            2,
            "attempt-branch-a-2",
            "event-branch-a-exhausted",
            Hash('e'),
            GovernedLoopControlCondition.Failure,
            _startedAtUtc.AddSeconds(7)));

        Assert.Equal(GovernedLoopFrontierStatus.Active, exhausted.Payload.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, exhausted.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal)).Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, exhausted.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-b", StringComparison.Ordinal)).Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Ready, exhausted.Payload.Nodes.Single(node => string.Equals(node.NodeId, "fail", StringComparison.Ordinal)).Status);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(exhausted, context.AdapterBinding, context.Plan));
    }

    [Fact]
    public async Task Retry_parking_rejects_reusing_the_running_attempt_operation_as_the_next_reservation()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(artifactFactory: ParallelRetryArtifact);
        var fanOut = AdvanceInferenceToFanOut(context);
        var branchA = context.Plan.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal));
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            fanOut,
            context.AdapterBinding,
            context.Plan,
            branchA,
            fanOut.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal)),
            1,
            "attempt-branch-a-1",
            _startedAtUtc.AddSeconds(3)));

        var parked = GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            running,
            context.AdapterBinding,
            context.Plan,
            branchA,
            SelectedActivation(running, context),
            1,
            2,
            "attempt-branch-a-1",
            _startedAtUtc.AddSeconds(4));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, parked.Status);
        Assert.Null(parked.Frontier);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(running, context.AdapterBinding, context.Plan));

        var nonUtc = GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            running,
            context.AdapterBinding,
            context.Plan,
            branchA,
            SelectedActivation(running, context),
            1,
            2,
            "attempt-branch-a-2",
            _startedAtUtc.AddSeconds(4).ToOffset(TimeSpan.FromHours(1)));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, nonUtc.Status);
        Assert.Null(nonUtc.Frontier);
    }

    [Fact]
    public async Task Retry_resume_rejects_a_substituted_attempt_operation_without_releasing_the_waiting_reservation()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(artifactFactory: ParallelRetryArtifact);
        var fanOut = AdvanceInferenceToFanOut(context);
        var branchA = context.Plan.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal));
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            fanOut,
            context.AdapterBinding,
            context.Plan,
            branchA,
            fanOut.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal)),
            1,
            "attempt-branch-a-1",
            _startedAtUtc.AddSeconds(3)));
        var waiting = Frontier(GovernedLoopSequentialFrontierMachine.ParkRunningForRetry(
            running,
            context.AdapterBinding,
            context.Plan,
            branchA,
            SelectedActivation(running, context),
            1,
            2,
            "attempt-branch-a-2",
            _startedAtUtc.AddSeconds(4)));
        var activation = waiting.Payload.Nodes.Single(node => string.Equals(node.NodeId, "branch-a", StringComparison.Ordinal));

        var resumed = GovernedLoopSequentialFrontierMachine.ResumeRetry(
            waiting,
            context.AdapterBinding,
            context.Plan,
            activation,
            2,
            "attempt-branch-a-substituted",
            _startedAtUtc.AddSeconds(5));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, resumed.Status);
        Assert.Null(resumed.Frontier);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Waiting, activation.Status);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(waiting, context.AdapterBinding, context.Plan));

        var nonUtc = GovernedLoopSequentialFrontierMachine.ResumeRetry(
            waiting,
            context.AdapterBinding,
            context.Plan,
            activation,
            2,
            "attempt-branch-a-2",
            _startedAtUtc.AddSeconds(5).ToOffset(TimeSpan.FromHours(1)));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, nonUtc.Status);
        Assert.Null(nonUtc.Frontier);
    }

    [Fact]
    public async Task Substituted_node_attempt_and_evidence_cannot_advance_running_frontier()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var node = context.Plan.Nodes[1];
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(initial, context),
            1,
            "attempt-exact",
            _startedAtUtc.AddSeconds(1)));

        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.Start(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(running, context),
            2,
            "attempt-second",
            _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(running, context),
            1,
            "attempt-substituted",
            "event-complete",
            Hash('c'),
            GovernedLoopControlCondition.Success,
            [],
            _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            context.Plan.Nodes[2],
            SelectedActivation(running, context),
            1,
            "attempt-exact",
            "event-complete",
            Hash('c'),
            GovernedLoopControlCondition.Success,
            [],
            _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            SelectedActivation(running, context),
            1,
            "attempt-exact",
            null,
            null,
            GovernedLoopControlCondition.Success,
            [],
            _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Running, GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan).Status);
    }

    [Fact]
    public async Task Cancellation_retains_exact_reached_prefix_and_terminal_frontiers_are_immutable()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var cancelled = Frontier(GovernedLoopSequentialFrontierMachine.Cancel(
            initial,
            context.AdapterBinding,
            context.Plan,
            _startedAtUtc.AddSeconds(1)));

        Assert.Equal(GovernedLoopFrontierStatus.Cancelled, cancelled.Payload.Status);
        Assert.Equal(initial.Payload.Nodes.Count, cancelled.Payload.Nodes.Count);
        Assert.All(initial.Payload.Nodes.Zip(cancelled.Payload.Nodes), pair => AssertNodeEqual(pair.First, pair.Second));
        Assert.Equal(initial.Payload.FrontierVersion + 1, cancelled.Payload.FrontierVersion);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Terminal, GovernedLoopSequentialFrontierMachine.Select(cancelled, context.AdapterBinding, context.Plan).Status);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Invalid, GovernedLoopSequentialFrontierMachine.Cancel(
            cancelled,
            context.AdapterBinding,
            context.Plan,
            _startedAtUtc.AddSeconds(2)).Status);
    }

    [Fact]
    public async Task Bound_prefix_terminalization_never_selects_or_exposes_unreached_work()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(inferenceCount: 3);
        var initial = Frontier(Initialize(context));

        var failed = Frontier(GovernedLoopSequentialFrontierMachine.FailCurrent(
            initial,
            context.AdapterBinding,
            "event-pre-dispatch-failure",
            "event-pre-dispatch-failure",
            Hash('f'),
            _startedAtUtc.AddSeconds(1)));

        Assert.Equal(GovernedLoopFrontierStatus.Failed, failed.Payload.Status);
        Assert.Equal(2, failed.Payload.Nodes.Count);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Failed, failed.Payload.Nodes[^1].Status);
        Assert.Equal("event-pre-dispatch-failure", failed.Payload.Nodes[^1].AttemptOperationId);
        Assert.Equal("event-pre-dispatch-failure", failed.Payload.Nodes[^1].OutcomeEvidenceId);

        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            context.Plan.Nodes[1],
            SelectedActivation(initial, context),
            1,
            "attempt-open",
            _startedAtUtc.AddSeconds(1)));
        var review = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(
            running,
            context.AdapterBinding,
            "event-review",
            Hash('e'),
            _startedAtUtc.AddSeconds(2)));

        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, review.Payload.Status);
        Assert.Equal(2, review.Payload.Nodes.Count);
        Assert.Equal("attempt-open", review.Payload.Nodes[^1].AttemptOperationId);
        Assert.Equal("event-review", review.Payload.Nodes[^1].OutcomeEvidenceId);
        Assert.True(GovernedLoopFrontierContractHash.Matches(review));
    }

    [Fact]
    public async Task Sole_ready_terminal_preparation_claims_once_and_retains_exact_review_evidence()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));

        var running = Frontier(GovernedLoopSequentialFrontierMachine.StartCurrent(
            initial,
            context.AdapterBinding,
            "attempt-terminal-review",
            _startedAtUtc.AddSeconds(1)));
        var selected = Assert.Single(running.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.Running);
        Assert.Equal(1, selected.Attempt);
        Assert.Equal("attempt-terminal-review", selected.AttemptOperationId);
        Assert.Equal(initial.Payload.FrontierVersion + 1, running.Payload.FrontierVersion);

        var review = Frontier(GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(
            running,
            context.AdapterBinding,
            "event-terminal-review",
            Hash('d'),
            _startedAtUtc.AddSeconds(2)));

        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, review.Payload.Status);
        var blocked = Assert.Single(review.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        Assert.Equal("attempt-terminal-review", blocked.AttemptOperationId);
        Assert.Equal("event-terminal-review", blocked.OutcomeEvidenceId);
        Assert.Equal(Hash('d'), blocked.OutcomeEvidenceHash);
        Assert.Equal(running.Payload.FrontierVersion + 1, review.Payload.FrontierVersion);
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(review, context.AdapterBinding, context.Plan));
    }

    [Fact]
    public async Task Malformed_or_ambiguous_public_transitions_fail_closed_without_rewriting_the_durable_frontier()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var selected = GovernedLoopSequentialFrontierMachine.Select(initial, context.AdapterBinding, context.Plan);
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            selected.Node,
            selected.Activation,
            1,
            "attempt-running",
            _startedAtUtc.AddSeconds(1)));
        var fanOutContext = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(
            artifactFactory: GovernedLoopSequentialApplicationTestFixture.ParallelAllJoinArtifact);
        var fanOut = AdvanceInferenceToFanOut(fanOutContext);

        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.Initialize(null, null, null, null, null, _startedAtUtc).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.Initialize(
                context.AdapterBinding,
                context.Plan,
                null,
                null,
                null,
                _startedAtUtc).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierSelectionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.Select(null, context.AdapterBinding, context.Plan).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.PlanPruning(
                initial,
                context.AdapterBinding,
                context.Plan,
                selected.Activation,
                GovernedLoopControlCondition.Success).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.Start(
                initial,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                selected.Activation,
                2,
                "attempt-two",
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.Start(
                initial,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                selected.Activation,
                1,
                null,
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.ReviewBlockReady(
                initial,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                selected.Activation,
                2,
                "attempt-review",
                "event-review",
                Hash('r'),
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.ReviewBlockReady(
                initial,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                selected.Activation,
                1,
                "attempt-review",
                null,
                null,
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.StartCurrent(
                fanOut,
                fanOutContext.AdapterBinding,
                "ambiguous-ready",
                _startedAtUtc.AddSeconds(3)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.StartCurrent(
                initial,
                context.AdapterBinding,
                null,
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.ReviewBlockAggregate(
                running,
                context.AdapterBinding,
                _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(
                initial,
                context.AdapterBinding,
                "event-review",
                Hash('v'),
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.FailCurrent(
                fanOut,
                fanOutContext.AdapterBinding,
                "attempt-failure",
                "event-failure",
                Hash('f'),
                _startedAtUtc.AddSeconds(3)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.FailCurrent(
                initial,
                context.AdapterBinding,
                "attempt-failure",
                "event-failure",
                Hash('f'),
                GovernedLoopControlCondition.Success,
                [],
                [],
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.FailCurrent(
                initial,
                context.AdapterBinding,
                null,
                null,
                null,
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.CancelCurrent(
                null,
                context.AdapterBinding,
                _startedAtUtc.AddSeconds(1)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.CancelCurrent(
                initial,
                context.AdapterBinding,
                _startedAtUtc.ToOffset(TimeSpan.FromHours(1))).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.Cancel(
                initial,
                context.AdapterBinding,
                null,
                _startedAtUtc.AddSeconds(1)).Status);

        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(initial, context.AdapterBinding, context.Plan));
        Assert.Equal(1, initial.Payload.FrontierVersion);
        Assert.True(GovernedLoopFrontierContractHash.Matches(initial));
    }

    [Fact]
    public async Task Running_resolution_rejects_missing_outcome_and_route_evidence_without_exposing_a_successor()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var initial = Frontier(Initialize(context));
        var selected = GovernedLoopSequentialFrontierMachine.Select(initial, context.AdapterBinding, context.Plan);
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            selected.Node,
            selected.Activation,
            1,
            "attempt-running",
            _startedAtUtc.AddSeconds(1)));
        var activation = SelectedActivation(running, context);

        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.CompleteRunning(
                initial,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                selected.Activation,
                1,
                "attempt-running",
                "event-complete",
                Hash('c'),
                GovernedLoopControlCondition.Success,
                [],
                _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.CompleteRunning(
                running,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                activation,
                1,
                "attempt-running",
                null,
                null,
                GovernedLoopControlCondition.Success,
                [],
                _startedAtUtc.AddSeconds(2)).Status);
        Assert.Equal(
            GovernedLoopSequentialFrontierTransitionStatus.Invalid,
            GovernedLoopSequentialFrontierMachine.CompleteRunning(
                running,
                context.AdapterBinding,
                context.Plan,
                selected.Node,
                activation,
                1,
                "attempt-running",
                "event-complete",
                Hash('c'),
                GovernedLoopControlCondition.Unknown,
                [],
                _startedAtUtc.AddSeconds(2)).Status);

        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Running, GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan).Status);
        Assert.Equal(2, running.Payload.FrontierVersion);
        Assert.DoesNotContain(running.Payload.Nodes, node => node.PlanOrdinal > selected.Node!.Ordinal);
        Assert.True(GovernedLoopFrontierContractHash.Matches(running));
    }

    private static GovernedLoopSequentialFrontierTransitionResult Initialize(GovernedLoopSequentialRunMaterializerTests.TestContext context)
        => GovernedLoopSequentialFrontierMachine.Initialize(
            context.AdapterBinding,
            context.Plan,
            "event-trigger",
            "event-trigger",
            Hash('a'),
            _startedAtUtc);

    private static GovernedLoopFrontierPosture Frontier(GovernedLoopSequentialFrontierTransitionResult result)
    {
        Assert.True(result.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied, result.Detail);
        return Assert.IsType<GovernedLoopFrontierPosture>(result.Frontier);
    }

    private static GovernedLoopNodeExecutionEvidence SelectedActivation(
        GovernedLoopFrontierPosture frontier,
        GovernedLoopSequentialRunMaterializerTests.TestContext context)
        => Assert.IsType<GovernedLoopNodeExecutionEvidence>(
            GovernedLoopSequentialFrontierMachine.Select(frontier, context.AdapterBinding, context.Plan).Activation);

    private static GovernedLoopFrontierPosture AdvanceInferenceToFanOut(
        GovernedLoopSequentialRunMaterializerTests.TestContext context)
    {
        var initial = Frontier(Initialize(context));
        var selection = GovernedLoopSequentialFrontierMachine.Select(initial, context.AdapterBinding, context.Plan);
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            initial,
            context.AdapterBinding,
            context.Plan,
            selection.Node,
            selection.Activation,
            1,
            "attempt-infer",
            _startedAtUtc.AddSeconds(1)));
        return Frontier(GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            selection.Node,
            SelectedActivation(running, context),
            1,
            "attempt-infer",
            "event-infer-complete",
            Hash('c'),
            GovernedLoopControlCondition.Success,
            [],
            _startedAtUtc.AddSeconds(2)));
    }

    private static GovernedLoopGraphRevisionArtifact ParallelRetryArtifact(ContextualRoleRevisionPin owningRole)
    {
        var source = GovernedLoopSequentialApplicationTestFixture.ParallelAllJoinArtifact(owningRole).Graph;
        var policyA = GovernedLoopRetryContract.CreatePolicy(
            "branch-retry-policy",
            "branch-a",
            [GovernedLoopFailureClass.DispatchProvedNotStarted],
            [],
            2,
            1_000,
            10_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            1,
            1,
            GovernedLoopRetryJitterStrategy.None,
            0,
            maximumResourceUnits: 2);
        var policyB = GovernedLoopRetryContract.CreatePolicy(
            "branch-retry-policy-b",
            "branch-b",
            [GovernedLoopFailureClass.DispatchProvedNotStarted],
            [],
            2,
            1_000,
            10_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            1,
            1,
            GovernedLoopRetryJitterStrategy.None,
            0,
            maximumResourceUnits: 2);
        var nodes = source.Nodes.Select(node => string.Equals(node.Id, "branch-a", StringComparison.Ordinal)
            ? new GovernedLoopNodeDefinition(node.Id, node.Descriptor, node.Ports, node.AuthorityCeiling, node.Parameters, node.ModelRoutingPolicy, node.AuthoredInputDataClasses, policyA)
            : string.Equals(node.Id, "branch-b", StringComparison.Ordinal)
                ? new GovernedLoopNodeDefinition(node.Id, node.Descriptor, node.Ports, node.AuthorityCeiling, node.Parameters, node.ModelRoutingPolicy, node.AuthoredInputDataClasses, policyB)
                : node)
            .Append(GovernedLoopSequentialApplicationTestFixture.Node("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal))
            .ToArray();
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            source.ControlEdges.Append(new GovernedLoopControlEdgeDefinition("branch-a-to-fail", "branch-a", "fail", GovernedLoopControlCondition.Failure)).ToArray(),
            source.TerminalNodeIds.Append("fail").ToArray(),
            owningRole,
            source.Bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private static string Hash(char value) => GovernedLoopSequentialApplicationTestFixture.Hash(value);

    private static void AssertNodeEqual(GovernedLoopNodeExecutionEvidence expected, GovernedLoopNodeExecutionEvidence actual)
    {
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.PlanOrdinal, actual.PlanOrdinal);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.Descriptor, actual.Descriptor);
        Assert.Equal(expected.IncomingControlEdgeIds, actual.IncomingControlEdgeIds);
        Assert.Equal(expected.OutgoingControlEdgeIds, actual.OutgoingControlEdgeIds);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Attempt, actual.Attempt);
        Assert.Equal(expected.AttemptOperationId, actual.AttemptOperationId);
        Assert.Equal(expected.OutcomeEvidenceId, actual.OutcomeEvidenceId);
        Assert.Equal(expected.OutcomeEvidenceHash, actual.OutcomeEvidenceHash);
    }
}
