using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialPlanBuilderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Supported_one_to_five_inference_lines_build_exact_read_only_plans(int inferenceCount)
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(inferenceCount);

        var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, result.Status);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(result.Plan);
        Assert.Null(result.FailurePath);
        Assert.Equal(artifact.RevisionArtifact.Revision, plan.Revision);
        Assert.Equal(artifact.ArtifactHash, plan.GraphArtifactHash);
        Assert.Equal(artifact.LayoutHash, plan.GraphLayoutHash);
        Assert.Equal(inferenceCount + 2, plan.Nodes.Count);
        Assert.Equal(GovernedLoopSequentialNodeDescriptors.ManualTrigger, plan.Nodes[0].Descriptor);
        Assert.Equal(GovernedLoopSequentialNodeDescriptors.SuccessExit, plan.Nodes[^1].Descriptor);
        Assert.All(plan.Nodes.Skip(1).SkipLast(1), node => Assert.Equal(GovernedLoopSequentialNodeDescriptors.ProviderInference, node.Descriptor));
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList<GovernedLoopSequentialPlanNode>>(plan.Nodes).RemoveAt(0));
    }

    [Fact]
    public void Traversal_uses_control_edges_instead_of_canonical_node_array_order()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2, ["z-infer", "a-infer"]);
        Assert.Equal(["a-infer", "exit", "trigger", "z-infer"], artifact.Graph.Nodes.Select(node => node.Id));

        var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

        var plan = Assert.IsType<GovernedLoopSequentialPlan>(result.Plan);
        Assert.Equal(["trigger", "z-infer", "a-infer", "exit"], plan.Nodes.Select(node => node.NodeId));
        Assert.Equal([0, 1, 2, 3], plan.Nodes.Select(node => node.Ordinal));
        Assert.Null(plan.Nodes[0].IncomingControlEdgeId);
        Assert.Null(plan.Nodes[^1].OutgoingControlEdgeId);
        Assert.Equal("trigger-to-z-infer", plan.Nodes[0].OutgoingControlEdgeId);
        Assert.Equal("z-infer-to-a-infer", plan.Nodes[2].IncomingControlEdgeId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Inference_count_outside_the_supported_bounds_fails_closed(int inferenceCount)
    {
        var result = GovernedLoopSequentialPlanBuilder.Build(GovernedLoopSequentialApplicationTestFixture.LinearArtifact(inferenceCount));

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, result.Status);
        Assert.Null(result.Plan);
        Assert.NotNull(result.FailurePath);
    }

    [Fact]
    public void Unsupported_exact_kind_type_or_version_fails_before_topology_is_planned()
    {
        var substitutions = new[]
        {
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "provider-inference", 1),
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "other-inference", 1),
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 2),
        };

        foreach (var descriptor in substitutions)
        {
            var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(1, inferenceDescriptor: _ => descriptor);
            var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

            Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedDescriptor, result.Status);
            Assert.Null(result.Plan);
            Assert.Equal("$.graph.nodes", result.FailurePath);
        }
    }

    [Fact]
    public void Branch_join_and_cycle_shapes_fail_closed()
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Node("trigger", GovernedLoopSequentialNodeDescriptors.ManualTrigger),
            GovernedLoopSequentialApplicationTestFixture.Node("infer-a", GovernedLoopSequentialNodeDescriptors.ProviderInference),
            GovernedLoopSequentialApplicationTestFixture.Node("infer-b", GovernedLoopSequentialNodeDescriptors.ProviderInference),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
        };
        var branch = GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            [
                new("trigger-a", "trigger", "infer-a", GovernedLoopControlCondition.Always),
                new("trigger-b", "trigger", "infer-b", GovernedLoopControlCondition.Always),
                new("a-exit", "infer-a", "exit", GovernedLoopControlCondition.Success),
                new("b-exit", "infer-b", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"]);
        var cycle = GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            [
                new("trigger-a", "trigger", "infer-a", GovernedLoopControlCondition.Always),
                new("a-b", "infer-a", "infer-b", GovernedLoopControlCondition.Success),
                new("b-a", "infer-b", "infer-a", GovernedLoopControlCondition.Success),
            ],
            ["exit"]);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopSequentialPlanBuilder.Build(branch).Status);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopSequentialPlanBuilder.Build(cycle).Status);
    }

    [Fact]
    public void Multiple_terminals_and_wrong_control_outcome_fail_closed()
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Node("trigger", GovernedLoopSequentialNodeDescriptors.ManualTrigger),
            GovernedLoopSequentialApplicationTestFixture.Node("infer", GovernedLoopSequentialNodeDescriptors.ProviderInference),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit-a"),
            GovernedLoopSequentialApplicationTestFixture.Exit("exit-b"),
        };
        var extraTerminal = GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            [
                new("trigger-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new("infer-exit-a", "infer", "exit-a", GovernedLoopControlCondition.Success),
                new("exit-a-exit-b", "exit-a", "exit-b", GovernedLoopControlCondition.Success),
            ],
            ["exit-a", "exit-b"]);
        var wrongOutcome = GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes.Take(3).ToArray(),
            [
                new("trigger-infer", "trigger", "infer", GovernedLoopControlCondition.Success),
                new("infer-exit-a", "infer", "exit-a", GovernedLoopControlCondition.Success),
            ],
            ["exit-a"]);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopSequentialPlanBuilder.Build(extraTerminal).Status);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopSequentialPlanBuilder.Build(wrongOutcome).Status);
    }

    [Fact]
    public void Missing_artifact_is_rejected_without_exception()
    {
        var result = GovernedLoopSequentialPlanBuilder.Build(null);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.InvalidArtifact, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("$", result.FailurePath);
    }
}
