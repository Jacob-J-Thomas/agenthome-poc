using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests.Loops.Execution;

public sealed class GovernedLoopFrontierActivationContractTests
{
    [Fact]
    public void Repeated_cycle_visits_are_distinct_from_attempts_and_transition_by_activation_identity()
    {
        var first = CycleActivation(0, 1, GovernedLoopNodeExecutionStatus.Completed, GovernedLoopControlCondition.True, ["edge-loop"], []);
        var secondReady = CycleActivation(1, 2, GovernedLoopNodeExecutionStatus.Ready, null, [], []);
        var current = Frontier(1, [first, secondReady]);
        var secondRunning = CycleActivation(1, 2, GovernedLoopNodeExecutionStatus.Running, null, [], [], attempt: 1);
        var next = Frontier(2, [first, secondRunning]);

        Assert.Equal(0, first.ActivationOrdinal);
        Assert.Equal(1, first.VisitOrdinal);
        Assert.Equal(1, first.Attempt);
        Assert.Equal(1, secondRunning.ActivationOrdinal);
        Assert.Equal(2, secondRunning.VisitOrdinal);
        Assert.Equal(1, secondRunning.Attempt);
        Assert.Equal("cycle-main", secondRunning.CycleId);
        Assert.Equal(2, secondRunning.CycleIteration);
        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, next).IsValid);
        Assert.True(GovernedLoopFrontierContractHash.Matches(next));
    }

    [Fact]
    public void Repeated_cycle_history_composes_without_treating_node_identity_as_unique()
    {
        var binding = GovernedLoopExecutionTestFixture.Binding();
        var first = CycleActivation(0, 1, GovernedLoopNodeExecutionStatus.Completed, GovernedLoopControlCondition.True, ["edge-loop"], []);
        var second = CycleActivation(1, 2, GovernedLoopNodeExecutionStatus.Ready, null, [], []);
        var frontier = GovernedLoopExecutionTestFixture.Frontier(
            binding,
            GovernedLoopFrontierStatus.Active,
            nodes: [first, second],
            updatedAtUtc: GovernedLoopExecutionTestFixture.UpdatedAtUtc);
        var lifecycle = GovernedLoopExecutionTestFixture.Lifecycle(binding, GovernedLoopRunStatus.Running);
        var effect = GovernedLoopEffectPosture.Create(
            binding,
            GovernedLoopExecutionTestFixture.Effect(originNodeId: "condition"));

        var validation = GovernedLoopExecutionValidator.ValidateComposition(1, lifecycle, frontier, [effect], []);
        var evidence = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, frontier, [effect], []);

        Assert.True(validation.IsValid);
        Assert.Equal(2, evidence.Frontier.Payload.Nodes.Count);
    }

    [Fact]
    public void Activation_history_rejects_gaps_implicit_cycles_and_changed_plan_topology()
    {
        var first = CycleActivation(0, 1, GovernedLoopNodeExecutionStatus.Completed, GovernedLoopControlCondition.True, ["edge-loop"], []);
        var activationGap = CycleActivation(2, 2, GovernedLoopNodeExecutionStatus.Ready, null, [], []);
        var visitGap = CycleActivation(1, 3, GovernedLoopNodeExecutionStatus.Ready, null, [], []);
        var implicitCycle = GovernedLoopNodeExecutionEvidence.CreateActivation(
            1,
            0,
            2,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            ["edge-loop"],
            ["edge-loop"],
            GovernedLoopNodeExecutionStatus.Ready);
        var changedTopology = GovernedLoopNodeExecutionEvidence.CreateActivation(
            1,
            0,
            2,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            ["edge-loop", "edge-new"],
            ["edge-loop"],
            GovernedLoopNodeExecutionStatus.Ready,
            cycleId: "cycle-main",
            cycleIteration: 2);

        Assert.Throws<ArgumentException>(() => Payload([first, activationGap]));
        Assert.Throws<ArgumentException>(() => Payload([first, visitGap]));
        Assert.Throws<ArgumentException>(() => Payload([first, implicitCycle]));
        Assert.Throws<ArgumentException>(() => Payload([first, changedTopology]));
    }

    [Fact]
    public void Repeated_node_visits_require_strictly_advancing_cycle_iterations()
    {
        var first = CycleActivation(0, 1, GovernedLoopNodeExecutionStatus.Completed, GovernedLoopControlCondition.True, ["edge-loop"], []);
        var repeatedIteration = GovernedLoopNodeExecutionEvidence.CreateActivation(
            1,
            0,
            2,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            ["edge-loop"],
            ["edge-loop"],
            GovernedLoopNodeExecutionStatus.Ready,
            cycleId: "cycle-main",
            cycleIteration: 1);

        Assert.Throws<ArgumentException>(() => Payload([first, repeatedIteration]));
    }

    [Fact]
    public void Join_arrivals_bind_one_exact_selected_source_activation_and_are_defensively_copied()
    {
        var source = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            [],
            ["edge-join", "edge-skip"],
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            "attempt-condition-1",
            "outcome-condition-1",
            Hash('a'),
            controlOutcome: GovernedLoopControlCondition.True,
            selectedControlEdgeIds: ["edge-join"],
            skippedControlEdgeIds: ["edge-skip"]);
        var sourceArrival = GovernedLoopJoinArrivalEvidence.Create(1, "edge-join", 0);
        var arrivals = new[] { sourceArrival };
        var join = GovernedLoopNodeExecutionEvidence.CreateActivation(
            1,
            1,
            1,
            "join",
            Descriptor(GovernedLoopNodeKind.Join),
            ["edge-join"],
            [],
            GovernedLoopNodeExecutionStatus.Ready,
            joinArrivals: arrivals);
        arrivals[0] = GovernedLoopJoinArrivalEvidence.Create(1, "edge-other", 0);

        var payload = Payload([source, join]);

        Assert.Equal("edge-join", payload.Nodes[1].JoinArrivals[0].ControlEdgeId);
        Assert.Equal(0, payload.Nodes[1].JoinArrivals[0].SourceActivationOrdinal);
        var missingSelection = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "source",
            Descriptor(GovernedLoopNodeKind.Transform),
            [],
            ["edge-join"],
            GovernedLoopNodeExecutionStatus.Completed,
            1,
            "attempt-source-1",
            "outcome-source-1",
            Hash('b'));
        Assert.Throws<ArgumentException>(() => Payload([missingSelection, join]));
        var reviewSource = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            [],
            ["edge-join", "edge-skip"],
            GovernedLoopNodeExecutionStatus.ReviewBlocked,
            1,
            "attempt-condition-1",
            "outcome-condition-1",
            Hash('a'),
            controlOutcome: GovernedLoopControlCondition.True,
            selectedControlEdgeIds: ["edge-join"],
            skippedControlEdgeIds: ["edge-skip"]);
        var failedSource = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            [],
            ["edge-join", "edge-skip"],
            GovernedLoopNodeExecutionStatus.Failed,
            1,
            "attempt-condition-1",
            "outcome-condition-1",
            Hash('a'),
            controlOutcome: GovernedLoopControlCondition.True,
            selectedControlEdgeIds: ["edge-join"],
            skippedControlEdgeIds: ["edge-skip"]);
        Assert.Throws<ArgumentException>(() => Payload([reviewSource, join]));
        Assert.Throws<ArgumentException>(() => Payload([failedSource, join]));
        var selfArrival = GovernedLoopNodeExecutionEvidence.CreateActivation(
            1,
            1,
            1,
            "join",
            Descriptor(GovernedLoopNodeKind.Join),
            ["edge-join"],
            [],
            GovernedLoopNodeExecutionStatus.Ready,
            joinArrivals: [GovernedLoopJoinArrivalEvidence.Create(1, "edge-join", 1)]);
        Assert.Throws<ArgumentException>(() => Payload([source, selfArrival]));
    }

    [Fact]
    public void Routing_and_cycle_shapes_fail_closed_before_frontier_construction()
    {
        Assert.Throws<ArgumentException>(() => Activation(controlOutcome: null, selected: ["edge-true"], skipped: ["edge-false"]));
        Assert.Throws<ArgumentException>(() => Activation(controlOutcome: GovernedLoopControlCondition.Unknown, selected: ["edge-true"], skipped: ["edge-false"]));
        Assert.Throws<ArgumentException>(() => Activation(controlOutcome: GovernedLoopControlCondition.True, selected: ["edge-true"], skipped: []));
        Assert.Throws<ArgumentException>(() => Activation(controlOutcome: GovernedLoopControlCondition.True, selected: ["edge-true"], skipped: ["edge-true"]));
        Assert.Throws<ArgumentException>(() => Activation(controlOutcome: GovernedLoopControlCondition.True, selected: ["edge-true"], skipped: ["edge-false"], status: GovernedLoopNodeExecutionStatus.Running));
        Assert.Throws<ArgumentException>(() => Activation(controlOutcome: GovernedLoopControlCondition.True, selected: ["edge-true"], skipped: ["edge-false"], cycleId: "cycle-main", cycleIteration: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Activation(controlOutcome: GovernedLoopControlCondition.True, selected: ["edge-true"], skipped: ["edge-false"], cycleId: "cycle-main", cycleIteration: GovernedLoopExecutionLimits.MaxCycleIterations + 1));
    }

    [Fact]
    public void Canonical_hash_covers_activation_routing_cycle_and_join_evidence()
    {
        var source = Activation(GovernedLoopControlCondition.True, ["edge-true"], ["edge-false"]);
        var baseline = Frontier(1, [source], GovernedLoopFrontierStatus.Completed);
        var changedRouting = Frontier(1, [Activation(GovernedLoopControlCondition.False, ["edge-false"], ["edge-true"])], GovernedLoopFrontierStatus.Completed);
        var cyclic = Frontier(1, [Activation(GovernedLoopControlCondition.True, ["edge-true"], ["edge-false"], cycleId: "cycle-main", cycleIteration: 1)], GovernedLoopFrontierStatus.Completed);

        Assert.NotEqual(baseline.Payload.ContentHash, changedRouting.Payload.ContentHash);
        Assert.NotEqual(baseline.Payload.ContentHash, cyclic.Payload.ContentHash);
        Assert.True(GovernedLoopFrontierContractValidator.Validate(baseline).IsValid);
        Assert.True(GovernedLoopFrontierContractValidator.Validate(changedRouting).IsValid);
        Assert.True(GovernedLoopFrontierContractValidator.Validate(cyclic).IsValid);
    }

    [Fact]
    public void Active_frontier_allows_multiple_ready_activations_but_only_one_running_activation()
    {
        var running = SimpleActivation(0, 0, "running", GovernedLoopNodeExecutionStatus.Running);
        var readyA = SimpleActivation(1, 1, "ready-a", GovernedLoopNodeExecutionStatus.Ready);
        var readyB = SimpleActivation(2, 2, "ready-b", GovernedLoopNodeExecutionStatus.Ready);

        var payload = Payload([running, readyA, readyB]);

        Assert.Equal(2, payload.Nodes.Count(node => node.Status == GovernedLoopNodeExecutionStatus.Ready));
        Assert.Single(payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.Running);
        var runningB = SimpleActivation(2, 2, "ready-b", GovernedLoopNodeExecutionStatus.Running);
        Assert.Throws<ArgumentException>(() => Payload([running, readyA, runningB]));
    }

    [Fact]
    public void Ready_to_skipped_commits_durable_pruning_without_inventing_a_control_outcome()
    {
        var ready = SimpleActivation(0, 0, "pruned", GovernedLoopNodeExecutionStatus.Ready, ["edge-next"]);
        var skipped = GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "pruned",
            Descriptor(GovernedLoopNodeKind.Transform),
            [],
            ["edge-next"],
            GovernedLoopNodeExecutionStatus.Skipped,
            outcomeEvidenceId: "skip-evidence",
            outcomeEvidenceHash: Hash('e'));

        Assert.True(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(ready, skipped));
        Assert.Null(skipped.ControlOutcome);
        Assert.Empty(skipped.SelectedControlEdgeIds);
        Assert.Empty(skipped.SkippedControlEdgeIds);
    }

    [Fact]
    public void Skipped_activation_cannot_invent_a_selected_route_that_authenticates_a_join()
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "pruned",
            Descriptor(GovernedLoopNodeKind.Transform),
            [],
            ["edge-join"],
            GovernedLoopNodeExecutionStatus.Skipped,
            outcomeEvidenceId: "skip-evidence",
            outcomeEvidenceHash: Hash('e'),
            controlOutcome: GovernedLoopControlCondition.Success,
            selectedControlEdgeIds: ["edge-join"],
            skippedControlEdgeIds: []));
    }

    [Fact]
    public void Frontier_successor_exposes_new_activations_before_claim_or_terminal_resolution()
    {
        var currentRunning = SimpleActivation(0, 0, "first", GovernedLoopNodeExecutionStatus.Running);
        var current = Frontier(1, [currentRunning]);
        var completed = SimpleActivation(0, 0, "first", GovernedLoopNodeExecutionStatus.Completed);
        var skipped = SimpleActivation(1, 1, "pruned", GovernedLoopNodeExecutionStatus.Skipped);
        var ready = SimpleActivation(2, 2, "next", GovernedLoopNodeExecutionStatus.Ready);
        var exposed = Frontier(2, [completed, skipped, ready]);

        Assert.True(GovernedLoopExecutionValidator.ValidateTransition(current, exposed).IsValid);

        var appendedRunning = SimpleActivation(1, 1, "next", GovernedLoopNodeExecutionStatus.Running);
        var claimedWithoutExposure = Frontier(2, [completed, appendedRunning]);
        var appendedCompleted = SimpleActivation(1, 1, "next", GovernedLoopNodeExecutionStatus.Completed);
        var completedWithoutExposure = Frontier(2, [completed, appendedCompleted], GovernedLoopFrontierStatus.Completed);

        Assert.Contains(
            GovernedLoopExecutionValidator.ValidateTransition(current, claimedWithoutExposure).Errors,
            error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition && error.Path == "$frontier.payload.nodes[1].status");
        Assert.Contains(
            GovernedLoopExecutionValidator.ValidateTransition(current, completedWithoutExposure).Errors,
            error => error.Code == GovernedLoopExecutionValidationErrorCode.IllegalTransition && error.Path == "$frontier.payload.nodes[1].status");
    }

    [Fact]
    public void Committed_route_and_join_evidence_are_immutable_across_frontier_successors()
    {
        var current = Activation(GovernedLoopControlCondition.True, ["edge-true"], ["edge-false"]);
        var changed = Activation(GovernedLoopControlCondition.False, ["edge-false"], ["edge-true"]);

        Assert.False(GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(current, changed));
    }

    [Fact]
    public void Activation_trace_exhaustion_fails_before_unbounded_enumeration()
    {
        var maximum = Enumerable.Range(0, GovernedLoopExecutionLimits.MaxFrontierNodes)
            .Select(index => SimpleActivation(
                index,
                index,
                $"node-{index:D3}",
                index == GovernedLoopExecutionLimits.MaxFrontierNodes - 1 ? GovernedLoopNodeExecutionStatus.Ready : GovernedLoopNodeExecutionStatus.Completed))
            .ToArray();

        Assert.Equal(GovernedLoopExecutionLimits.MaxFrontierNodes, Payload(maximum).Nodes.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopFrontierPayload.Create(
            1,
            1,
            1,
            GovernedLoopFrontierStatus.Active,
            maximum.Append(SimpleActivation(0, 0, "unreachable", GovernedLoopNodeExecutionStatus.Ready)),
            GovernedLoopExecutionTestFixture.UpdatedAtUtc,
            string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopNodeExecutionEvidence.CreateActivation(
            GovernedLoopExecutionLimits.MaxFrontierNodes,
            0,
            1,
            "overflow",
            Descriptor(GovernedLoopNodeKind.Transform),
            [],
            [],
            GovernedLoopNodeExecutionStatus.Ready));
    }

    private static GovernedLoopNodeExecutionEvidence Activation(
        GovernedLoopControlCondition? controlOutcome,
        IEnumerable<string> selected,
        IEnumerable<string> skipped,
        GovernedLoopNodeExecutionStatus status = GovernedLoopNodeExecutionStatus.Completed,
        string? cycleId = null,
        int? cycleIteration = null)
    {
        return GovernedLoopNodeExecutionEvidence.CreateActivation(
            0,
            0,
            1,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            [],
            ["edge-false", "edge-true"],
            status,
            1,
            "attempt-condition-1",
            status == GovernedLoopNodeExecutionStatus.Running ? null : "outcome-condition-1",
            status == GovernedLoopNodeExecutionStatus.Running ? null : Hash('c'),
            cycleId,
            cycleIteration,
            controlOutcome,
            selected,
            skipped);
    }

    private static GovernedLoopNodeExecutionEvidence CycleActivation(
        int activationOrdinal,
        int visitOrdinal,
        GovernedLoopNodeExecutionStatus status,
        GovernedLoopControlCondition? controlOutcome,
        IEnumerable<string> selected,
        IEnumerable<string> skipped,
        int? attempt = 1)
    {
        var selectedAttempt = status == GovernedLoopNodeExecutionStatus.Ready ? null : attempt;
        return GovernedLoopNodeExecutionEvidence.CreateActivation(
            activationOrdinal,
            0,
            visitOrdinal,
            "condition",
            Descriptor(GovernedLoopNodeKind.Condition),
            ["edge-loop"],
            ["edge-loop"],
            status,
            selectedAttempt,
            selectedAttempt is null ? null : $"attempt-condition-{visitOrdinal}",
            status == GovernedLoopNodeExecutionStatus.Completed ? $"outcome-condition-{visitOrdinal}" : null,
            status == GovernedLoopNodeExecutionStatus.Completed ? Hash('d') : null,
            "cycle-main",
            visitOrdinal,
            controlOutcome,
            selected,
            skipped);
    }

    private static GovernedLoopNodeExecutionEvidence SimpleActivation(
        int activationOrdinal,
        int planOrdinal,
        string nodeId,
        GovernedLoopNodeExecutionStatus status,
        IEnumerable<string>? outgoing = null)
    {
        int? attempt = status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Skipped ? null : 1;
        var outcome = status is GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed
            ? $"outcome-{nodeId}"
            : status == GovernedLoopNodeExecutionStatus.Skipped ? $"skip-{nodeId}" : null;
        return GovernedLoopNodeExecutionEvidence.CreateActivation(
            activationOrdinal,
            planOrdinal,
            1,
            nodeId,
            Descriptor(GovernedLoopNodeKind.Transform),
            [],
            outgoing ?? [],
            status,
            attempt,
            attempt is null ? null : $"attempt-{nodeId}-1",
            outcome,
            outcome is null ? null : Hash('f'));
    }

    private static GovernedLoopFrontierPayload Payload(IEnumerable<GovernedLoopNodeExecutionEvidence> nodes)
        => GovernedLoopFrontierPayload.Create(1, 1, 1, GovernedLoopFrontierStatus.Active, nodes, GovernedLoopExecutionTestFixture.UpdatedAtUtc, string.Empty);

    private static GovernedLoopFrontierPosture Frontier(long version, IEnumerable<GovernedLoopNodeExecutionEvidence> nodes, GovernedLoopFrontierStatus status = GovernedLoopFrontierStatus.Active)
        => GovernedLoopExecutionTestFixture.Frontier(GovernedLoopExecutionTestFixture.Binding(), status, version, nodes, GovernedLoopExecutionTestFixture.UpdatedAtUtc.AddMinutes(version));

    private static GovernedLoopNodeDescriptor Descriptor(GovernedLoopNodeKind kind)
        => new(kind, kind == GovernedLoopNodeKind.Join ? "join-all" : "condition-expression", 1);

    private static string Hash(char character) => new(character, 64);
}
