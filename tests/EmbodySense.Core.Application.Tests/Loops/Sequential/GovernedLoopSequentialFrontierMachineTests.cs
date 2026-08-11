using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

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
