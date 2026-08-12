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
    public void Exact_workspace_command_assignment_is_supported_only_as_a_graph_and_every_inference_node_subset()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2, allowWorkspaceTools: true);

        var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, result.Status);
        Assert.Equal(
            [GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId, GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId, GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId],
            artifact.Graph.AuthorityCeiling.CapabilityIds);
        Assert.All(
            artifact.Graph.Nodes.Where(node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference),
            node => Assert.Equal(
                [GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId, GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId],
                node.AuthorityCeiling.CapabilityIds));

        var firstInference = artifact.Graph.Nodes.Single(node => node.Id == "infer-01");
        var missingNodeAssignment = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            artifact.Graph,
            nodes: artifact.Graph.Nodes.Select(node => node.Id == firstInference.Id
                ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId]) }
                : node).ToArray());

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, GovernedLoopSequentialPlanBuilder.Build(missingNodeAssignment).Status);
        Assert.Equal("$.graph.nodes", GovernedLoopSequentialPlanBuilder.Build(missingNodeAssignment).FailurePath);
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
    public void Missing_or_substituted_first_wave_node_contracts_fail_closed()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact().Graph;
        var inference = source.Nodes.Single(node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var missingInstruction = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == inference.Id ? node with { Parameters = new Dictionary<string, string>() } : node).ToArray());
        var emptyInstruction = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == inference.Id ? node with { Parameters = new Dictionary<string, string> { ["instruction"] = string.Empty } } : node).ToArray());
        var missingAuthority = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == inference.Id ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([]) } : node).ToArray());
        var substitutedAuthority = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == inference.Id ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId]) } : node).ToArray(),
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId, GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId, GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId]));
        var missingContextPort = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == inference.Id ? node with { Ports = node.Ports.Where(port => port.Id != "invocation-context").ToArray() } : node).ToArray(),
            bindings: source.Bindings.Where(binding => binding.Kind != GovernedLoopBindingKind.Context).ToArray());
        var extraPort = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == inference.Id
                ? node with { Ports = [.. node.Ports, GovernedLoopSequentialApplicationTestFixture.Port("debug", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, required: false)] }
                : node).ToArray());
        var exit = source.Nodes.Single(node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.SuccessExit);
        var missingPublicationAuthority = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == exit.Id
                ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([]) }
                : node).ToArray());
        var substitutedPublicationAuthority = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == exit.Id
                ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId]) }
                : node).ToArray());

        foreach (var artifact in new[] { missingInstruction, emptyInstruction, missingAuthority, substitutedAuthority, missingContextPort, extraPort, missingPublicationAuthority, substitutedPublicationAuthority })
        {
            var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

            Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, result.Status);
            Assert.Equal("$.graph.nodes", result.FailurePath);
            Assert.Null(result.Plan);
        }
    }

    [Fact]
    public void Extra_graph_wide_authority_fails_closed_even_when_every_node_contract_is_exact()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact().Graph;
        var artifact = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            authorityCeiling: GovernedLoopAuthorityCeiling.Create(
                [GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId, GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId, "org.embodysense/workspace-read"]));

        var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, result.Status);
        Assert.Equal("$.graph.authorityCeiling", result.FailurePath);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void Substituted_schema_binding_and_output_contracts_fail_closed()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2).Graph;
        var secondInference = source.Nodes.Single(node => node.Id == "infer-02");
        var bypassedDataChain = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            bindings: source.Bindings.Select(binding => binding.ToNodeId == secondInference.Id && binding.ToPortId == "request"
                ? binding with { FromNodeId = "trigger", FromPortId = "request" }
                : binding).ToArray());
        var substitutedOutput = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            outputContract: new GovernedLoopOutputContract(
                "Return a substituted source.",
                [new GovernedLoopOutputDefinition("result", "text", secondInference.Id, "result", true)]));
        var renamedSchema = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node with { Ports = node.Ports.Select(port => port with { ValueSchemaId = "message" }).ToArray() }).ToArray(),
            valueSchemas: [new GovernedLoopValueSchemaDefinition("message", GovernedLoopValueKind.Text, false)],
            outputContract: new GovernedLoopOutputContract(
                source.OutputContract.Summary,
                [source.OutputContract.Outputs[0] with { ValueSchemaId = "message" }]));

        Assert.Equal("$.graph.bindings", GovernedLoopSequentialPlanBuilder.Build(bypassedDataChain).FailurePath);
        Assert.Equal("$.graph.outputContract", GovernedLoopSequentialPlanBuilder.Build(substitutedOutput).FailurePath);
        Assert.Equal("$.graph.valueSchemas", GovernedLoopSequentialPlanBuilder.Build(renamedSchema).FailurePath);
        Assert.All(
            new[] { bypassedDataChain, substitutedOutput, renamedSchema },
            artifact => Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, GovernedLoopSequentialPlanBuilder.Build(artifact).Status));
    }

    [Fact]
    public void Branch_join_and_cycle_shapes_fail_closed()
    {
        var nodes = new[]
        {
            GovernedLoopSequentialApplicationTestFixture.Node("trigger", GovernedLoopSequentialNodeDescriptors.ManualTrigger),
            GovernedLoopSequentialApplicationTestFixture.Node("infer-a", GovernedLoopSequentialNodeDescriptors.ProviderInference),
            GovernedLoopSequentialApplicationTestFixture.Node("infer-b", GovernedLoopSequentialNodeDescriptors.ProviderInference),
            GovernedLoopSequentialApplicationTestFixture.Node("exit", GovernedLoopSequentialNodeDescriptors.SuccessExit),
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
            GovernedLoopSequentialApplicationTestFixture.Node("exit-a", GovernedLoopSequentialNodeDescriptors.SuccessExit),
            GovernedLoopSequentialApplicationTestFixture.Node("exit-b", GovernedLoopSequentialNodeDescriptors.SuccessExit),
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
