using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests;

public sealed class GovernedLoopGraphDefinitionTests
{
    [Fact]
    public void Valid_graph_is_canonical_bounded_and_explicit()
    {
        var owningRole = GovernedLoopGraphTestFixture.Role();
        var graph = GovernedLoopGraphTestFixture.Create(owningRole: owningRole);

        Assert.Equal(1, graph.SchemaVersion);
        Assert.Equal("research-loop", graph.GraphId);
        Assert.Equal(GovernedLoopGraphTestFixture.Role(), graph.OwningRole);
        Assert.NotSame(owningRole, graph.OwningRole);
        Assert.NotSame(owningRole.Identity, graph.OwningRole.Identity);
        Assert.Equal([GovernedLoopGraphTestFixture.ModelInferenceCapability, GovernedLoopGraphTestFixture.WorkspaceReadCapability], graph.AuthorityCeiling.CapabilityIds);
        Assert.Contains(graph.Bindings, binding => binding.Kind == GovernedLoopBindingKind.Data);
        Assert.Contains(graph.Bindings, binding => binding.Kind == GovernedLoopBindingKind.Context);
        Assert.DoesNotContain(graph.ControlEdges, edge => edge.Id == graph.Bindings[0].Id);
        Assert.Equal(graph.ExecutableHash, graph.RevisionReference.ExecutableHash);
        Assert.Equal(graph.RevisionId, graph.RevisionReference.RevisionId);
        Assert.Equal(1, graph.RevisionReference.SchemaVersion);
    }

    [Fact]
    public void Owning_role_requires_an_exact_canonical_revision_pin()
    {
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphTestFixture.Create(owningRole: new ContextualRoleRevisionPin(null!, new string('a', 64))));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(owningRole: GovernedLoopGraphTestFixture.Role("INVALID")));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(owningRole: GovernedLoopGraphTestFixture.Role(revision: 0)));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(owningRole: GovernedLoopGraphTestFixture.Role(contentHash: 'A')));
    }

    [Fact]
    public void Authority_ceiling_requires_a_collection()
    {
        Assert.Throws<ArgumentNullException>(() => GovernedLoopAuthorityCeiling.Create(null!));
    }

    [Fact]
    public void Authority_ceiling_requires_exact_capability_ids_and_canonicalizes_order()
    {
        var ceiling = GovernedLoopAuthorityCeiling.Create(
            [GovernedLoopGraphTestFixture.WorkspaceReadCapability, GovernedLoopGraphTestFixture.ModelInferenceCapability]);

        Assert.Equal(
            [GovernedLoopGraphTestFixture.ModelInferenceCapability, GovernedLoopGraphTestFixture.WorkspaceReadCapability],
            ceiling.CapabilityIds);
        Assert.Throws<ArgumentException>(() => GovernedLoopAuthorityCeiling.Create(["model-inference"]));
        Assert.Throws<ArgumentException>(() => GovernedLoopAuthorityCeiling.Create(["Org.EmbodySense/model-inference"]));
        Assert.Throws<ArgumentException>(() => GovernedLoopAuthorityCeiling.Create([GovernedLoopGraphTestFixture.ModelInferenceCapability, GovernedLoopGraphTestFixture.ModelInferenceCapability]));
    }

    [Fact]
    public void All_declared_descriptor_kinds_are_accepted_without_runtime_claims()
    {
        var nonterminalKinds = Enum.GetValues<GovernedLoopNodeKind>().Where(kind => kind is not GovernedLoopNodeKind.Unknown and not GovernedLoopNodeKind.Trigger and not GovernedLoopNodeKind.Exit and not GovernedLoopNodeKind.Fail);

        foreach (var kind in nonterminalKinds)
        {
            var nodes = GovernedLoopGraphTestFixture.Nodes();
            nodes[1] = nodes[1] with { Descriptor = new GovernedLoopNodeDescriptor(kind, $"extension-{kind.ToString().ToLowerInvariant()}", 7) };
            var graph = GovernedLoopGraphTestFixture.Create(nodes: nodes);
            Assert.Equal(kind, graph.Nodes.Single(node => node.Id == "infer").Descriptor.Kind);
        }

        var failNodes = GovernedLoopGraphTestFixture.Nodes();
        failNodes[2] = failNodes[2] with { Descriptor = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Fail, "terminal-failure", 1) };
        Assert.Equal(GovernedLoopNodeKind.Fail, GovernedLoopGraphTestFixture.Create(nodes: failNodes).Nodes.Single(node => node.Id == "exit").Descriptor.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Non_schema_one_graphs_are_rejected(int schemaVersion)
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemaVersion: schemaVersion));
    }

    [Theory]
    [InlineData("Graph")]
    [InlineData("graph id")]
    [InlineData("gráph")]
    [InlineData("graph\u202Eid")]
    [InlineData("graph-")]
    public void Noncanonical_or_unsafe_graph_ids_are_rejected(string graphId)
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(graphId: graphId));
    }

    [Theory]
    [InlineData("unsafe\u202Epurpose")]
    [InlineData("decomposed-e\u0301")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    [InlineData("cr\rline")]
    public void Noncanonical_or_unsafe_text_is_rejected(string purpose)
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(purpose: purpose));
    }

    [Fact]
    public void Unicode_validation_accepts_safe_scalars_and_rejects_ill_formed_or_private_values()
    {
        Assert.Equal("Research 🧪 safely.", GovernedLoopGraphTestFixture.Create(purpose: "Research 🧪 safely.").Purpose);
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(purpose: new string('\uD800', 1)));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(purpose: "unsafe-\uE000-private"));
    }

    [Fact]
    public void Duplicate_identity_at_every_graph_level_is_rejected()
    {
        var schemas = GovernedLoopGraphTestFixture.Schemas();
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemas: [schemas[0], schemas[0]]));

        var nodes = GovernedLoopGraphTestFixture.Nodes();
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: [nodes[0], nodes[1], nodes[1], nodes[2]]));

        var duplicatePorts = GovernedLoopGraphTestFixture.Nodes();
        duplicatePorts[1] = duplicatePorts[1] with { Ports = [duplicatePorts[1].Ports[0], duplicatePorts[1].Ports[0]] };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: duplicatePorts));

        var edges = GovernedLoopGraphTestFixture.Edges();
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(edges: [edges[0], edges[0]]));

        var bindings = GovernedLoopGraphTestFixture.Bindings();
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: [bindings[0], bindings[0]]));
    }

    [Fact]
    public void Undefined_kinds_fail_closed()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Descriptor = nodes[1].Descriptor with { Kind = (GovernedLoopNodeKind)999 } };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: nodes));

        var schemas = GovernedLoopGraphTestFixture.Schemas();
        schemas[0] = schemas[0] with { Kind = GovernedLoopValueKind.Unknown };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemas: schemas));

        var bindings = GovernedLoopGraphTestFixture.Bindings();
        bindings[0] = bindings[0] with { Kind = GovernedLoopBindingKind.Unknown };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: bindings));

        var edges = GovernedLoopGraphTestFixture.Edges();
        edges[0] = edges[0] with { Condition = (GovernedLoopControlCondition)999 };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(edges: edges));
    }

    [Fact]
    public void Node_authority_can_narrow_but_never_widen_loop_ceiling()
    {
        var narrowed = GovernedLoopGraphTestFixture.Create();
        Assert.Equal([GovernedLoopGraphTestFixture.ModelInferenceCapability], narrowed.Nodes.Single(node => node.Id == "infer").AuthorityCeiling.CapabilityIds);

        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create(["org.embodysense/external-publish"]) };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: nodes));
    }

    [Fact]
    public void Required_inputs_have_no_ambient_predecessor_output()
    {
        var bindings = GovernedLoopGraphTestFixture.Bindings().Where(binding => binding.Id != "context-binding");

        var exception = Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: bindings));

        Assert.Contains("must have one explicit binding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_binding_direction_channel_schema_and_fan_in_are_rejected()
    {
        var baseBindings = GovernedLoopGraphTestFixture.Bindings();
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: baseBindings.Select(binding => binding.Id == "request-binding" ? binding with { FromPortId = "missing" } : binding)));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: baseBindings.Select(binding => binding.Id == "request-binding" ? binding with { Kind = GovernedLoopBindingKind.Context } : binding)));

        var schemas = new[] { new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false), new GovernedLoopValueSchemaDefinition("json", GovernedLoopValueKind.Object, false) };
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Ports = nodes[1].Ports.Select(port => port.Id == "request" ? port with { ValueSchemaId = "json" } : port).ToArray() };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemas: schemas, nodes: nodes));

        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: [.. baseBindings, baseBindings[0] with { Id = "second-request-binding" }]));
    }

    [Fact]
    public void Public_collection_maxima_fail_closed()
    {
        var tooManyCapabilities = Enumerable.Range(0, CustomLoopLimits.MaxGraphAuthorityCapabilities + 1).Select(index => $"org.embodysense/capability-{index}");
        Assert.Throws<ArgumentException>(() => GovernedLoopAuthorityCeiling.Create(tooManyCapabilities));

        var tooManySchemas = Enumerable.Range(0, CustomLoopLimits.MaxGraphValueSchemas + 1).Select(index => new GovernedLoopValueSchemaDefinition($"schema-{index}", GovernedLoopValueKind.Text, false));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemas: tooManySchemas));

        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Ports = Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode + 1).Select(index => GovernedLoopGraphTestFixture.OutputPort($"port-{index}", GovernedLoopBindingKind.Data)).ToArray() };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: nodes));

        var tooManyNodes = Enumerable.Range(0, CustomLoopLimits.MaxGraphNodes + 1).Select(index => new GovernedLoopNodeDefinition(
            $"node-{index}",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "transform", 1),
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: tooManyNodes));
        var tooManyEdges = Enumerable.Range(0, CustomLoopLimits.MaxGraphControlEdges + 1).Select(index => new GovernedLoopControlEdgeDefinition($"edge-{index}", "trigger", "infer", GovernedLoopControlCondition.Always));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(edges: tooManyEdges));
        var tooManyBindings = Enumerable.Range(0, CustomLoopLimits.MaxGraphBindings + 1).Select(index => new GovernedLoopBindingDefinition(
            $"binding-{index}",
            GovernedLoopBindingKind.Data,
            "trigger",
            "request",
            "infer",
            "request"));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(bindings: tooManyBindings));
        var tooManyOutputs = new GovernedLoopOutputContract(
            "Too many outputs.",
            Enumerable.Range(0, CustomLoopLimits.MaxGraphOutputs + 1).Select(index => new GovernedLoopOutputDefinition(
                $"output-{index}",
                "text",
                "exit",
                "published-result",
                true)).ToArray());
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(output: tooManyOutputs));
    }

    [Fact]
    public void Exact_public_collection_maxima_are_accepted()
    {
        var capabilities = new[] { GovernedLoopGraphTestFixture.ModelInferenceCapability, GovernedLoopGraphTestFixture.WorkspaceReadCapability }.Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphAuthorityCapabilities - 2).Select(index => $"org.embodysense/capability-{index}"));
        var schemas = GovernedLoopGraphTestFixture.Schemas().Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphValueSchemas - 1).Select(index => new GovernedLoopValueSchemaDefinition($"schema-{index}", GovernedLoopValueKind.Text, false)));

        var graph = GovernedLoopGraphTestFixture.Create(authorityCeiling: GovernedLoopAuthorityCeiling.Create(capabilities), schemas: schemas);

        Assert.Equal(CustomLoopLimits.MaxGraphAuthorityCapabilities, graph.AuthorityCeiling.CapabilityIds.Count);
        Assert.Equal(CustomLoopLimits.MaxGraphValueSchemas, graph.ValueSchemas.Count);
    }

    [Fact]
    public void Exact_graph_structure_maxima_are_accepted()
    {
        var maximumPorts = GovernedLoopGraphTestFixture.Create(nodes: NodesWithMaximumPorts());
        var maximumNodes = GovernedLoopGraphTestFixture.Create(nodes: NodesAtMaximum());
        var maximumEdges = GovernedLoopGraphTestFixture.Create(edges: EdgesAtMaximum());
        var (bindingNodes, bindings) = BindingsAtMaximum();
        var maximumBindings = GovernedLoopGraphTestFixture.Create(
            nodes: bindingNodes,
            edges: [new GovernedLoopControlEdgeDefinition("trigger-to-exit", "trigger", "exit", GovernedLoopControlCondition.Always)],
            bindings: bindings,
            output: new GovernedLoopOutputContract("No declared values.", []),
            display: new GovernedLoopDisplayMetadata("Maximum bindings", string.Empty, []));
        var maximumOutputs = GovernedLoopGraphTestFixture.Create(output: new GovernedLoopOutputContract(
            "Return every declared value.",
            Enumerable.Range(0, CustomLoopLimits.MaxGraphOutputs).Select(index => new GovernedLoopOutputDefinition($"output-{index}", "text", "exit", "published-result", true)).ToArray()));
        var parameterNodes = GovernedLoopGraphTestFixture.Nodes();
        parameterNodes[1] = parameterNodes[1] with { Parameters = Enumerable.Range(0, CustomLoopLimits.MaxGraphDescriptorParameters).ToDictionary(index => $"parameter-{index}", _ => "value") };
        var maximumParameters = GovernedLoopGraphTestFixture.Create(nodes: parameterNodes);

        Assert.Equal(CustomLoopLimits.MaxGraphPortsPerNode, maximumPorts.Nodes.Single(node => node.Id == "infer").Ports.Count);
        Assert.Equal(CustomLoopLimits.MaxGraphNodes, maximumNodes.Nodes.Count);
        Assert.Equal(CustomLoopLimits.MaxGraphControlEdges, maximumEdges.ControlEdges.Count);
        Assert.Equal(CustomLoopLimits.MaxGraphBindings, maximumBindings.Bindings.Count);
        Assert.Equal(CustomLoopLimits.MaxGraphOutputs, maximumOutputs.OutputContract.Outputs.Count);
        Assert.Equal(CustomLoopLimits.MaxGraphDescriptorParameters, maximumParameters.Nodes.Single(node => node.Id == "infer").Parameters.Count);
    }

    [Fact]
    public void Exact_text_and_canvas_boundaries_are_accepted()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = new string('i', CustomLoopLimits.MaxGraphParameterValueCharacters) } };
        var display = GovernedLoopGraphTestFixture.Display() with
        {
            DisplayName = new string('g', CustomLoopLimits.MaxNameCharacters),
            Description = new string('d', CustomLoopLimits.MaxDescriptionCharacters),
            Nodes = GovernedLoopGraphTestFixture.Display().Nodes.Select((node, index) => node with
            {
                DisplayName = new string('n', CustomLoopLimits.MaxNameCharacters),
                Description = new string('x', CustomLoopLimits.MaxDescriptionCharacters),
                CanvasX = index == 0 ? CustomLoopLimits.MaxGraphCanvasCoordinate : -CustomLoopLimits.MaxGraphCanvasCoordinate,
                CanvasY = index == 0 ? -CustomLoopLimits.MaxGraphCanvasCoordinate : CustomLoopLimits.MaxGraphCanvasCoordinate
            }).ToArray()
        };
        var output = GovernedLoopGraphTestFixture.Output() with { Summary = new string('o', CustomLoopLimits.MaxDescriptionCharacters) };

        var graph = GovernedLoopGraphTestFixture.Create(purpose: new string('p', CustomLoopLimits.MaxDescriptionCharacters), nodes: nodes, output: output, display: display);

        Assert.Equal(CustomLoopLimits.MaxDescriptionCharacters, graph.Purpose.Length);
        Assert.Equal(CustomLoopLimits.MaxGraphParameterValueCharacters, graph.Nodes.Single(node => node.Id == "infer").Parameters["instruction"].Length);
        Assert.Equal(CustomLoopLimits.MaxNameCharacters, graph.DisplayMetadata.DisplayName.Length);
        Assert.Equal(CustomLoopLimits.MaxDescriptionCharacters, graph.DisplayMetadata.Description.Length);
        Assert.Equal(CustomLoopLimits.MaxDescriptionCharacters, graph.OutputContract.Summary.Length);
        Assert.Contains(graph.DisplayMetadata.Nodes, node => node.CanvasX == CustomLoopLimits.MaxGraphCanvasCoordinate && node.CanvasY == -CustomLoopLimits.MaxGraphCanvasCoordinate);
        Assert.Contains(graph.DisplayMetadata.Nodes, node => node.CanvasX == -CustomLoopLimits.MaxGraphCanvasCoordinate && node.CanvasY == CustomLoopLimits.MaxGraphCanvasCoordinate);
    }

    [Fact]
    public void Display_layout_is_bounded_even_though_it_is_non_executable()
    {
        var display = GovernedLoopGraphTestFixture.Display(x: CustomLoopLimits.MaxGraphCanvasCoordinate + 1);

        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(display: display));
    }

    [Fact]
    public void Additional_local_value_invariants_fail_closed()
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(purpose: new string('p', CustomLoopLimits.MaxDescriptionCharacters + 1)));
        Assert.Throws<ArgumentException>(() => GovernedLoopAuthorityCeiling.Create([GovernedLoopGraphTestFixture.WorkspaceReadCapability, GovernedLoopGraphTestFixture.WorkspaceReadCapability]));

        var arrayWithoutElement = new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Array, false);
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemas: [arrayWithoutElement]));
        var scalarWithElement = new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false, ElementSchemaId: "text");
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(schemas: [scalarWithElement]));

        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Descriptor = nodes[1].Descriptor with { Version = 0 } };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: nodes));
        nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Ports = nodes[1].Ports.Select(port => port.Id == "result" ? port with { ValueSchemaId = "missing" } : port).ToArray() };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: nodes));
        nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Parameters = Enumerable.Range(0, CustomLoopLimits.MaxGraphDescriptorParameters + 1).ToDictionary(index => $"parameter-{index}", _ => "value") };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(nodes: nodes));

        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(entryNodeId: "missing"));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(terminalNodeIds: ["missing"]));
        var edges = GovernedLoopGraphTestFixture.Edges();
        edges[0] = edges[0] with { ToNodeId = "missing" };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(edges: edges));

        var output = GovernedLoopGraphTestFixture.Output() with { Outputs = [new GovernedLoopOutputDefinition("result", "missing", "exit", "published-result", true)] };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(output: output));
        var display = GovernedLoopGraphTestFixture.Display() with { Nodes = [new GovernedLoopNodeDisplayMetadata("missing", "Missing", "Missing node.")] };
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphTestFixture.Create(display: display));
    }

    private static GovernedLoopNodeDefinition[] NodesWithMaximumPorts()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with
        {
            Ports = nodes[1].Ports.Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode - nodes[1].Ports.Count).Select(index => GovernedLoopGraphTestFixture.OutputPort($"extra-port-{index}", GovernedLoopBindingKind.Data))).ToArray()
        };
        return nodes;
    }

    private static GovernedLoopNodeDefinition[] NodesAtMaximum()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        return nodes.Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphNodes - nodes.Length).Select(index => new GovernedLoopNodeDefinition(
            $"extra-node-{index}",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "bounded-transform", 1),
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>()))).ToArray();
    }

    private static GovernedLoopControlEdgeDefinition[] EdgesAtMaximum()
    {
        var edges = GovernedLoopGraphTestFixture.Edges();
        return edges.Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphControlEdges - edges.Length).Select(index => new GovernedLoopControlEdgeDefinition($"extra-edge-{index}", "trigger", "infer", GovernedLoopControlCondition.Always))).ToArray();
    }

    private static (GovernedLoopNodeDefinition[] Nodes, GovernedLoopBindingDefinition[] Bindings) BindingsAtMaximum()
    {
        const int InputNodeCount = CustomLoopLimits.MaxGraphBindings / CustomLoopLimits.MaxGraphPortsPerNode;
        var trigger = new GovernedLoopNodeDefinition(
            "trigger",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
            [GovernedLoopGraphTestFixture.OutputPort("source", GovernedLoopBindingKind.Data)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var consumers = Enumerable.Range(0, InputNodeCount).Select(nodeIndex => new GovernedLoopNodeDefinition(
            $"input-node-{nodeIndex}",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "bounded-transform", 1),
            Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode).Select(portIndex => GovernedLoopGraphTestFixture.InputPort($"input-{portIndex}", GovernedLoopBindingKind.Data)).ToArray(),
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>())).ToArray();
        var exit = new GovernedLoopNodeDefinition(
            "exit",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var bindings = consumers.SelectMany((node, nodeIndex) => node.Ports.Select((port, portIndex) => new GovernedLoopBindingDefinition(
            $"binding-{nodeIndex}-{portIndex}",
            GovernedLoopBindingKind.Data,
            "trigger",
            "source",
            node.Id,
            port.Id))).ToArray();
        return ([trigger, .. consumers, exit], bindings);
    }
}
