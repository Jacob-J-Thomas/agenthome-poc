using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

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

    [Fact]
    public void Mixed_transform_inference_and_validate_line_builds_one_deterministic_plan()
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.MixedPureArtifact();

        var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, result.Status);
        Assert.Null(result.FailurePath);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(result.Plan);
        Assert.Equal(["trigger", "identity", "infer", "validate-length", "exit"], plan.Nodes.Select(node => node.NodeId));
        Assert.Equal(
            [
                GovernedLoopSequentialNodeDescriptors.ManualTrigger,
                GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                GovernedLoopSequentialNodeDescriptors.ProviderInference,
                GovernedLoopSequentialNodeDescriptors.TextLength,
                GovernedLoopSequentialNodeDescriptors.SuccessExit
            ],
            plan.Nodes.Select(node => node.Descriptor));
        Assert.Equal([0, 1, 2, 3, 4], plan.Nodes.Select(node => node.Ordinal));
        Assert.Single(plan.Nodes, node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference);
    }

    [Fact]
    public void Pure_node_descriptor_authority_parameter_and_schema_substitutions_fail_closed()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.MixedPureArtifact().Graph;
        var identity = source.Nodes.Single(node => node.Id == "identity");
        var validation = source.Nodes.Single(node => node.Id == "validate-length");
        var wrongVersion = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == identity.Id
                ? node with { Descriptor = node.Descriptor with { Version = 2 } }
                : node).ToArray());
        var authorityWidening = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == identity.Id
                ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId]) }
                : node).ToArray());
        var reversedRange = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == validation.Id
                ? node with
                {
                    Parameters = new Dictionary<string, string>
                    {
                        [GovernedLoopPureNodeVocabulary.MinimumParameter] = "2",
                        [GovernedLoopPureNodeVocabulary.MaximumParameter] = "1"
                    }
                }
                : node).ToArray());
        var formattedSchema = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            valueSchemas: source.ValueSchemas.Select(schema => schema.Id == "text" ? schema with { Format = "markdown" } : schema).ToArray());

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedDescriptor, GovernedLoopSequentialPlanBuilder.Build(wrongVersion).Status);
        Assert.All(new[] { authorityWidening, reversedRange }, artifact =>
        {
            var result = GovernedLoopSequentialPlanBuilder.Build(artifact);
            Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, result.Status);
            Assert.Equal("$.graph.nodes", result.FailurePath);
        });
        var schemaResult = GovernedLoopSequentialPlanBuilder.Build(formattedSchema);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, schemaResult.Status);
        Assert.Equal("$.graph.valueSchemas", schemaResult.FailurePath);
    }

    [Fact]
    public void Pure_inputs_require_explicit_data_bindings_from_earlier_plan_nodes()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.MixedPureArtifact().Graph;
        var requestBinding = source.Bindings.Single(binding => binding.Id == "request-to-identity");
        var identity = source.Nodes.Single(node => node.Id == "identity");
        var contextIdentity = identity with
        {
            Ports = identity.Ports.Select(port => port.Id == GovernedLoopPureNodeVocabulary.InputPort
                ? port with { BindingKind = GovernedLoopBindingKind.Context }
                : port).ToArray()
        };
        var contextBound = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == identity.Id ? contextIdentity : node).ToArray(),
            bindings: source.Bindings.Select(binding => binding.Id == requestBinding.Id
                ? binding with { Kind = GovernedLoopBindingKind.Context, FromPortId = "invocation-context" }
                : binding).ToArray());
        var futureBound = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            bindings: source.Bindings.Select(binding => binding.Id == requestBinding.Id
                ? binding with { FromNodeId = "infer", FromPortId = "result" }
                : binding).ToArray());

        Assert.Throws<ArgumentException>(() => GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            bindings: source.Bindings.Where(binding => binding.Id != requestBinding.Id).ToArray()));
        var contextResult = GovernedLoopSequentialPlanBuilder.Build(contextBound);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, contextResult.Status);
        Assert.Equal("$.graph.nodes", contextResult.FailurePath);
        var futureResult = GovernedLoopSequentialPlanBuilder.Build(futureBound);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, futureResult.Status);
        Assert.Equal("$.graph.bindings", futureResult.FailurePath);
    }

    [Theory]
    [InlineData("schema-root-format")]
    [InlineData("schema-element-format")]
    [InlineData("schema-cycle")]
    [InlineData("concat-array-format")]
    [InlineData("concat-element-format")]
    public void Formatted_or_cyclic_pure_schema_trees_fail_before_plan_admission(string substitution)
    {
        var artifact = substitution switch
        {
            "schema-root-format" => GovernedLoopPureSchemaAdmissionTestFixture.SchemaConformanceArtifact(formatRoot: true),
            "schema-element-format" => GovernedLoopPureSchemaAdmissionTestFixture.SchemaConformanceArtifact(formatElement: true),
            "schema-cycle" => GovernedLoopPureSchemaAdmissionTestFixture.SchemaConformanceArtifact(cycle: true),
            "concat-array-format" => GovernedLoopPureSchemaAdmissionTestFixture.ConcatArtifact(formatArray: true),
            "concat-element-format" => GovernedLoopPureSchemaAdmissionTestFixture.ConcatArtifact(formatElement: true),
            _ => throw new InvalidOperationException("Unknown pure-schema substitution."),
        };

        var result = GovernedLoopSequentialPlanBuilder.Build(artifact);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("$.graph.valueSchemas", result.FailurePath);
    }

    [Fact]
    public void Schema_tree_depth_matches_the_materializable_typed_value_boundary()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.MixedPureArtifact().Graph;
        var accepted = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            valueSchemas:
            [
                .. source.ValueSchemas,
                .. GovernedLoopPureSchemaAdmissionTestFixture.DeepArraySchemas(CustomLoopLimits.MaxGraphTypedValueDepth - 1),
            ]);
        var rejected = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            valueSchemas:
            [
                .. source.ValueSchemas,
                .. GovernedLoopPureSchemaAdmissionTestFixture.DeepArraySchemas(CustomLoopLimits.MaxGraphTypedValueDepth),
            ]);

        var acceptedResult = GovernedLoopSequentialPlanBuilder.Build(accepted);
        var rejectedResult = GovernedLoopSequentialPlanBuilder.Build(rejected);

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, acceptedResult.Status);
        Assert.NotNull(acceptedResult.Plan);
        Assert.Null(acceptedResult.FailurePath);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, rejectedResult.Status);
        Assert.Null(rejectedResult.Plan);
        Assert.Equal("$.graph.valueSchemas", rejectedResult.FailurePath);
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
    public void Typed_earlier_sources_and_schema_id_renames_are_supported_while_future_sources_and_output_substitution_fail_closed()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(2).Graph;
        var firstInference = source.Nodes.Single(node => node.Id == "infer-01");
        var secondInference = source.Nodes.Single(node => node.Id == "infer-02");
        var bypassedDataChain = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            bindings: source.Bindings.Select(binding => binding.ToNodeId == secondInference.Id && binding.ToPortId == "request"
                ? binding with { FromNodeId = "trigger", FromPortId = "request" }
                : binding).ToArray());
        var futureDataSource = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            bindings: source.Bindings.Select(binding => binding.ToNodeId == firstInference.Id && binding.ToPortId == "request"
                ? binding with { FromNodeId = secondInference.Id, FromPortId = "result" }
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

        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, GovernedLoopSequentialPlanBuilder.Build(bypassedDataChain).Status);
        Assert.Equal(GovernedLoopSequentialPlanBuildStatus.Ready, GovernedLoopSequentialPlanBuilder.Build(renamedSchema).Status);
        Assert.Equal("$.graph.bindings", GovernedLoopSequentialPlanBuilder.Build(futureDataSource).FailurePath);
        Assert.Equal("$.graph.outputContract", GovernedLoopSequentialPlanBuilder.Build(substitutedOutput).FailurePath);
        Assert.All(
            new[] { futureDataSource, substitutedOutput },
            artifact => Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, GovernedLoopSequentialPlanBuilder.Build(artifact).Status));
    }

    [Fact]
    public void Implicit_nonJoin_fanIn_and_unbounded_cycles_fail_closed_before_contract_projection()
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
    public void Entry_terminal_and_inference_outcome_substitutions_fail_at_the_structural_boundary()
    {
        var source = GovernedLoopSequentialApplicationTestFixture.LinearArtifact().Graph;
        var invalidEntry = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => node.Id == source.EntryNodeId
                ? node with { Descriptor = GovernedLoopSequentialNodeDescriptors.ProviderInference }
                : node).ToArray());
        var invalidTerminal = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            source,
            nodes: source.Nodes.Select(node => source.TerminalNodeIds.Contains(node.Id, StringComparer.Ordinal)
                ? node with { Descriptor = GovernedLoopSequentialNodeDescriptors.IdentityTransform }
                : node).ToArray());
        var invalidInferenceOutcome = GovernedLoopSequentialApplicationTestFixture.Artifact(
            source.Nodes,
            source.ControlEdges.Select(edge => edge.FromNodeId == "infer-01"
                ? edge with { Condition = GovernedLoopControlCondition.Always }
                : edge).ToArray(),
            source.TerminalNodeIds,
            source.OwningRole,
            source.Bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);

        Assert.Equal("$.graph.entryNodeId", GovernedLoopSequentialPlanBuilder.Build(invalidEntry).FailurePath);
        Assert.Equal("$.graph.terminalNodeIds", GovernedLoopSequentialPlanBuilder.Build(invalidTerminal).FailurePath);
        Assert.Equal("$.graph.controlEdges", GovernedLoopSequentialPlanBuilder.Build(invalidInferenceOutcome).FailurePath);
        Assert.All(
            new[] { invalidEntry, invalidTerminal, invalidInferenceOutcome },
            artifact => Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, GovernedLoopSequentialPlanBuilder.Build(artifact).Status));
    }

    [Fact]
    public void Exact_node_and_dataflow_contracts_reject_missing_parameters_extra_ports_and_nondominating_sources()
    {
        var pureSource = GovernedLoopSequentialApplicationTestFixture.MixedPureArtifact().Graph;
        var missingPureParameter = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            pureSource,
            nodes: pureSource.Nodes.Select(node => node.Id == "validate-length"
                ? node with
                {
                    Parameters = node.Parameters
                        .Where(parameter => parameter.Key != GovernedLoopPureNodeVocabulary.MaximumParameter)
                        .ToDictionary(StringComparer.Ordinal),
                }
                : node).ToArray());
        var joinSource = GovernedLoopSequentialApplicationTestFixture.ParallelAllJoinArtifact().Graph;
        var extraJoinPort = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            joinSource,
            nodes: joinSource.Nodes.Select(node => node.Id == "join"
                ? node with
                {
                    Ports =
                    [
                        .. node.Ports,
                        GovernedLoopSequentialApplicationTestFixture.Port("unexpected", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                    ],
                }
                : node).ToArray());
        var nondominatingExitSource = GovernedLoopSequentialApplicationTestFixture.Rebuild(
            joinSource,
            bindings: joinSource.Bindings.Select(binding => binding.Id == "result-to-exit"
                ? binding with { FromNodeId = "branch-a", FromPortId = GovernedLoopPureNodeVocabulary.OutputPort }
                : binding).ToArray());

        Assert.Equal("$.graph.nodes", GovernedLoopSequentialPlanBuilder.Build(missingPureParameter).FailurePath);
        Assert.Equal("$.graph.nodes", GovernedLoopSequentialPlanBuilder.Build(extraJoinPort).FailurePath);
        Assert.Equal("$.graph.bindings", GovernedLoopSequentialPlanBuilder.Build(nondominatingExitSource).FailurePath);
        Assert.All(
            new[] { missingPureParameter, extraJoinPort, nondominatingExitSource },
            artifact => Assert.Equal(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, GovernedLoopSequentialPlanBuilder.Build(artifact).Status));
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
