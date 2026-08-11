using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopTopologyPlanBuilderTests
{
    [Fact]
    public void Selected_branch_plan_preserves_exact_edges_static_order_and_concurrency_one_policy()
    {
        var artifact = BranchArtifact(GovernedLoopSequentialNodeDescriptors.SelectedJoin);

        var result = GovernedLoopTopologyPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, result.Status);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(result.Plan);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], plan.Nodes.Select(node => node.StaticOrdinal));
        Assert.Equal(["trigger", "infer", "condition", "branch-a", "branch-b", "join", "exit"], plan.Nodes.Select(node => node.NodeId));
        Assert.Equal(1, plan.SchedulerPolicy.MaximumConcurrency);
        Assert.Equal(GovernedLoopTopologyReadyOrdering.StaticOrdinalThenNodeId, plan.SchedulerPolicy.ReadyOrdering);
        Assert.False(plan.SchedulerPolicy.AllowsParallelEffectfulNodes);
        var condition = plan.Nodes.Single(node => node.NodeId == "condition");
        Assert.Equal(["condition-false", "condition-true"], condition.OutgoingControlEdgeIds);
        Assert.Null(condition.OutgoingControlEdgeId);
        var join = plan.Nodes.Single(node => node.NodeId == "join");
        Assert.Equal(["branch-a-to-join", "branch-b-to-join"], join.IncomingControlEdgeIds);
        Assert.Null(join.IncomingControlEdgeId);
        Assert.All(plan.Components, component => Assert.False(component.IsCyclic));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<string>>(condition.OutgoingControlEdgeIds).RemoveAt(0));
    }

    [Fact]
    public void Noncondition_fanout_admits_multiple_ready_nodes_but_never_parallel_running_policy()
    {
        var artifact = ParallelAllJoinArtifact();

        var result = GovernedLoopTopologyPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, result.Status);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(result.Plan);
        var inference = plan.Nodes.Single(node => node.NodeId == "infer");
        Assert.Equal(["infer-to-branch-a", "infer-to-branch-b"], inference.OutgoingControlEdgeIds);
        Assert.Equal(1, plan.SchedulerPolicy.MaximumConcurrency);
        Assert.Equal(GovernedLoopJoinPolicy.All, ResolveJoin(plan.Nodes.Single(node => node.NodeId == "join").Descriptor));
    }

    [Fact]
    public void All_join_over_mutually_exclusive_condition_paths_fails_closed()
    {
        var result = GovernedLoopTopologyPlanBuilder.Build(BranchArtifact(GovernedLoopSequentialNodeDescriptors.AllJoin));

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("$.graph.controlEdges", result.FailurePath);
    }

    [Fact]
    public void Bounded_self_cycle_receives_stable_cycle_identity_and_most_restrictive_bounds()
    {
        var first = CycleArtifact(iterations: "7", durationMilliseconds: "9000");
        var second = CycleArtifact(iterations: "7", durationMilliseconds: "9000");

        var firstResult = GovernedLoopTopologyPlanBuilder.Build(first);
        var secondResult = GovernedLoopTopologyPlanBuilder.Build(second);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, firstResult.Status);
        var firstPlan = Assert.IsType<GovernedLoopSequentialPlan>(firstResult.Plan);
        var secondPlan = Assert.IsType<GovernedLoopSequentialPlan>(secondResult.Plan);
        var cycle = Assert.Single(firstPlan.Components, component => component.IsCyclic);
        Assert.Equal(7, cycle.MaximumIterations);
        Assert.Equal(9000, cycle.MaximumDurationMilliseconds);
        Assert.StartsWith("cycle-", cycle.CycleId, StringComparison.Ordinal);
        Assert.Equal(cycle.CycleId, secondPlan.Components.Single(component => component.IsCyclic).CycleId);
        Assert.Equal(cycle.CycleId, firstPlan.Nodes.Single(node => node.NodeId == "condition").CycleId);
    }

    [Fact]
    public void Inference_and_model_decision_loop_back_share_one_cycle_and_use_most_restrictive_bounds()
    {
        var result = GovernedLoopTopologyPlanBuilder.Build(InferenceDecisionCycleArtifact());

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, result.Status);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(result.Plan);
        var cycle = Assert.Single(plan.Components, component => component.IsCyclic);
        Assert.Equal(["loop-infer", "condition"], cycle.NodeIds);
        Assert.Equal(3, cycle.MaximumIterations);
        Assert.Equal(4000, cycle.MaximumDurationMilliseconds);
        Assert.All(plan.Nodes.Where(node => cycle.NodeIds.Contains(node.NodeId, StringComparer.Ordinal)), node => Assert.Equal(cycle.CycleId, node.CycleId));
    }

    [Fact]
    public void Same_iteration_forward_bindings_are_admitted_but_loop_carried_and_post_exit_sources_are_rejected()
    {
        var valid = GovernedLoopTopologyPlanBuilder.Build(ThreeNodeCycleArtifact(reverseRequestBinding: false, ambiguousExitBinding: false));
        var reversed = GovernedLoopTopologyPlanBuilder.Build(ThreeNodeCycleArtifact(reverseRequestBinding: true, ambiguousExitBinding: false));
        var ambiguousExit = GovernedLoopTopologyPlanBuilder.Build(ThreeNodeCycleArtifact(reverseRequestBinding: false, ambiguousExitBinding: true));

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, valid.Status);
        var cycle = Assert.Single(valid.Plan!.Components, component => component.IsCyclic);
        Assert.Equal(["infer-a", "condition", "infer-b"], cycle.NodeIds);
        Assert.Equal([0, 1, 2], valid.Plan.Nodes.Where(node => node.CycleId == cycle.CycleId).Select(node => node.ComponentTraversalOrdinal));
        Assert.All(new[] { reversed, ambiguousExit }, result =>
        {
            Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, result.Status);
            Assert.Null(result.Plan);
            Assert.Equal("$.graph.bindings", result.FailurePath);
        });
    }

    [Theory]
    [InlineData(null, "9000")]
    [InlineData("7", null)]
    [InlineData("0", "9000")]
    [InlineData("7", "0")]
    [InlineData("10001", "9000")]
    [InlineData("7", "86400001")]
    public void Cycles_require_explicit_canonical_bounded_visit_and_time_budgets(string? iterations, string? durationMilliseconds)
    {
        var result = GovernedLoopTopologyPlanBuilder.Build(CycleArtifact(iterations, durationMilliseconds));

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, result.Status);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Duplicate_condition_outcome_and_incomplete_join_fail_before_plan_creation()
    {
        var source = BranchArtifact(GovernedLoopSequentialNodeDescriptors.SelectedJoin).Graph;
        var duplicateOutcome = Artifact(
            source.Nodes,
            [.. source.ControlEdges, new GovernedLoopControlEdgeDefinition("condition-true-copy", "condition", "branch-b", GovernedLoopControlCondition.True)],
            source.Bindings);
        var missingJoinArrival = Artifact(
            source.Nodes,
            source.ControlEdges.Where(edge => edge.Id != "branch-b-to-join").Append(new GovernedLoopControlEdgeDefinition("branch-b-to-exit", "branch-b", "exit", GovernedLoopControlCondition.Success)).ToArray(),
            source.Bindings);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopTopologyPlanBuilder.Build(duplicateOutcome).Status);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopTopologyPlanBuilder.Build(missingJoinArrival).Status);
    }

    [Fact]
    public void Scheduler_policy_rejects_any_attempt_to_enable_parallel_running_nodes()
    {
        Assert.Equal(1, GovernedLoopTopologySchedulerPolicy.Create().MaximumConcurrency);
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopTopologySchedulerPolicy.Create(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GovernedLoopTopologySchedulerPolicy.Create(2));
    }

    private static GovernedLoopGraphRevisionArtifact BranchArtifact(GovernedLoopNodeDescriptor joinDescriptor)
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer"),
            Condition("condition", includeCycleBudgets: false),
            Identity("branch-a"),
            Identity("branch-b"),
            Join("join", joinDescriptor),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
            Edge("infer-to-condition", "infer", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-true", "condition", "branch-a", GovernedLoopControlCondition.True),
            Edge("condition-false", "condition", "branch-b", GovernedLoopControlCondition.False),
            Edge("branch-a-to-join", "branch-a", "join", GovernedLoopControlCondition.Success),
            Edge("branch-b-to-join", "branch-b", "join", GovernedLoopControlCondition.Success),
            Edge("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
        };
        return Artifact(nodes, edges, StandardBindings(includeCondition: true, includeBranches: true));
    }

    private static GovernedLoopGraphRevisionArtifact ParallelAllJoinArtifact()
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer"),
            Identity("branch-a"),
            Identity("branch-b"),
            Join("join", GovernedLoopSequentialNodeDescriptors.AllJoin),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
            Edge("infer-to-branch-a", "infer", "branch-a", GovernedLoopControlCondition.Success),
            Edge("infer-to-branch-b", "infer", "branch-b", GovernedLoopControlCondition.Success),
            Edge("branch-a-to-join", "branch-a", "join", GovernedLoopControlCondition.Success),
            Edge("branch-b-to-join", "branch-b", "join", GovernedLoopControlCondition.Success),
            Edge("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
        };
        return Artifact(nodes, edges, StandardBindings(includeCondition: false, includeBranches: true));
    }

    private static GovernedLoopGraphRevisionArtifact CycleArtifact(string? iterations, string? durationMilliseconds)
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            GovernedLoopSequentialApplicationTestFixture.Inference("infer"),
            Condition("condition", includeCycleBudgets: true, iterations, durationMilliseconds),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
            Edge("infer-to-condition", "infer", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-repeat", "condition", "condition", GovernedLoopControlCondition.True),
            Edge("condition-exit", "condition", "exit", GovernedLoopControlCondition.False),
        };
        return Artifact(nodes, edges, StandardBindings(includeCondition: true, includeBranches: false));
    }

    private static GovernedLoopGraphRevisionArtifact InferenceDecisionCycleArtifact()
    {
        var loopInference = GovernedLoopSequentialApplicationTestFixture.Inference("loop-infer", "Continue the bounded admitted cycle.") with
        {
            Parameters = new Dictionary<string, string>
            {
                ["instruction"] = "Continue the bounded admitted cycle.",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "3",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "4000",
            }
        };
        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.ModelDecisionCondition,
            [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.DecisionPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter] = "continue",
                [GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter] = "stop",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "7",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "9000",
            });
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            loopInference,
            condition,
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-loop-infer", "trigger", "loop-infer", GovernedLoopControlCondition.Always),
            Edge("loop-infer-to-condition", "loop-infer", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-continue", "condition", "loop-infer", GovernedLoopControlCondition.True),
            Edge("condition-stop", "condition", "exit", GovernedLoopControlCondition.False),
        };
        var bindings = new GovernedLoopBindingDefinition[]
        {
            new("request-to-loop-infer", GovernedLoopBindingKind.Data, "trigger", "request", "loop-infer", "request"),
            new("context-to-loop-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "loop-infer", "invocation-context"),
            new("decision-to-condition", GovernedLoopBindingKind.Data, "loop-infer", "result", "condition", GovernedLoopTopologyNodeVocabulary.DecisionPort),
            new("result-to-exit", GovernedLoopBindingKind.Data, "loop-infer", "result", "exit", "result"),
        };
        return Artifact(nodes, edges, bindings);
    }

    private static GovernedLoopGraphRevisionArtifact ThreeNodeCycleArtifact(bool reverseRequestBinding, bool ambiguousExitBinding)
    {
        GovernedLoopNodeDefinition CyclicInference(string id, string instruction)
            => GovernedLoopSequentialApplicationTestFixture.Inference(id, instruction) with
            {
                Parameters = new Dictionary<string, string>
                {
                    ["instruction"] = instruction,
                    [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "5",
                    [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "5000",
                }
            };

        var condition = new GovernedLoopNodeDefinition(
            "condition",
            GovernedLoopSequentialNodeDescriptors.ModelDecisionCondition,
            [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.DecisionPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>
            {
                [GovernedLoopTopologyNodeVocabulary.TrueDecisionParameter] = "continue",
                [GovernedLoopTopologyNodeVocabulary.FalseDecisionParameter] = "stop",
                [GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter] = "5",
                [GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter] = "5000",
            });
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
            CyclicInference("infer-a", "Start one bounded cycle iteration."),
            condition,
            CyclicInference("infer-b", "Complete one bounded cycle iteration."),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var edges = new[]
        {
            Edge("trigger-to-infer-a", "trigger", "infer-a", GovernedLoopControlCondition.Always),
            Edge("infer-a-to-condition", "infer-a", "condition", GovernedLoopControlCondition.Success),
            Edge("condition-continue", "condition", "infer-b", GovernedLoopControlCondition.True),
            Edge("infer-b-to-infer-a", "infer-b", "infer-a", GovernedLoopControlCondition.Success),
            Edge("condition-stop", "condition", "exit", GovernedLoopControlCondition.False),
        };
        var requestSource = reverseRequestBinding ? "infer-b" : "trigger";
        var requestPort = reverseRequestBinding ? "result" : "request";
        var exitSource = ambiguousExitBinding ? "infer-b" : "infer-a";
        var bindings = new GovernedLoopBindingDefinition[]
        {
            new("request-to-infer-a", GovernedLoopBindingKind.Data, requestSource, requestPort, "infer-a", "request"),
            new("context-to-infer-a", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-a", "invocation-context"),
            new("decision-to-condition", GovernedLoopBindingKind.Data, "infer-a", "result", "condition", GovernedLoopTopologyNodeVocabulary.DecisionPort),
            new("request-to-infer-b", GovernedLoopBindingKind.Data, "infer-a", "result", "infer-b", "request"),
            new("context-to-infer-b", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer-b", "invocation-context"),
            new("result-to-exit", GovernedLoopBindingKind.Data, exitSource, "result", "exit", "result"),
        };
        return Artifact(nodes, edges, bindings);
    }

    private static GovernedLoopGraphRevisionArtifact Artifact(
        IReadOnlyList<GovernedLoopNodeDefinition> nodes,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> edges,
        IReadOnlyList<GovernedLoopBindingDefinition> bindings)
        => GovernedLoopSequentialApplicationTestFixture.Artifact(nodes, edges, ["exit"], bindings: bindings);

    private static GovernedLoopNodeDefinition Condition(
        string id,
        bool includeCycleBudgets,
        string? iterations = null,
        string? durationMilliseconds = null)
    {
        var parameters = new Dictionary<string, string>
        {
            [GovernedLoopTopologyNodeVocabulary.ExpectedParameter] = "repeat",
        };
        if (includeCycleBudgets && iterations is not null)
        {
            parameters.Add(GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter, iterations);
        }

        if (includeCycleBudgets && durationMilliseconds is not null)
        {
            parameters.Add(GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter, durationMilliseconds);
        }

        return new GovernedLoopNodeDefinition(
            id,
            GovernedLoopSequentialNodeDescriptors.ExactTextCondition,
            [GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopTopologyNodeVocabulary.ValuePort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            parameters);
    }

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

    private static GovernedLoopControlEdgeDefinition Edge(string id, string from, string to, GovernedLoopControlCondition condition)
        => new(id, from, to, condition);

    private static IReadOnlyList<GovernedLoopBindingDefinition> StandardBindings(bool includeCondition, bool includeBranches)
    {
        var bindings = new List<GovernedLoopBindingDefinition>
        {
            new("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
            new("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
            new("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
        };
        if (includeCondition)
        {
            bindings.Add(new GovernedLoopBindingDefinition("result-to-condition", GovernedLoopBindingKind.Data, "infer", "result", "condition", GovernedLoopTopologyNodeVocabulary.ValuePort));
        }

        if (includeBranches)
        {
            bindings.Add(new GovernedLoopBindingDefinition("result-to-branch-a", GovernedLoopBindingKind.Data, "infer", "result", "branch-a", GovernedLoopPureNodeVocabulary.InputPort));
            bindings.Add(new GovernedLoopBindingDefinition("result-to-branch-b", GovernedLoopBindingKind.Data, "infer", "result", "branch-b", GovernedLoopPureNodeVocabulary.InputPort));
        }

        return bindings;
    }

    private static GovernedLoopJoinPolicy ResolveJoin(GovernedLoopNodeDescriptor descriptor)
    {
        Assert.True(GovernedLoopTopologyNodeCatalogContract.TryResolve(descriptor, out var contract));
        return contract!.JoinPolicy;
    }
}
