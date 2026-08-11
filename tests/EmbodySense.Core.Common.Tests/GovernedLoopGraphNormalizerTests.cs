using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests;

public sealed class GovernedLoopGraphNormalizerTests
{
    [Fact]
    public void NormalizeReturnsCanonicalGraphAndPermutationStableResult()
    {
        var expected = GovernedLoopGraphNormalizer.Normalize(Candidate());
        var permuted = GovernedLoopGraphNormalizer.Normalize(Candidate(
            nodes: GovernedLoopGraphTestFixture.Nodes().Reverse().ToArray(),
            edges: GovernedLoopGraphTestFixture.Edges().Reverse().ToArray(),
            bindings: GovernedLoopGraphTestFixture.Bindings().Reverse().ToArray()));

        Assert.True(expected.IsValid);
        Assert.True(permuted.IsValid);
        Assert.Equal(expected.Graph!.ExecutableHash, permuted.Graph!.ExecutableHash);
        Assert.Empty(expected.Errors);
    }

    [Fact]
    public void NormalizeFailsClosedForNullAndMalformedUnicode()
    {
        var missing = GovernedLoopGraphNormalizer.Normalize(null);
        var malformed = GovernedLoopGraphNormalizer.Normalize(Candidate(purpose: new string('\uD800', 1)));

        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, error => error.Code == "graph.required");
        Assert.False(malformed.IsValid);
        Assert.Contains(malformed.Errors, error => error.Code == "graph.purpose.invalid");
    }

    [Fact]
    public void NormalizeAttributesMissingAndDuplicateIdentities()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[0] = nodes[0] with { Id = null! };
        nodes[2] = nodes[2] with { Id = "infer" };
        var edges = GovernedLoopGraphTestFixture.Edges();
        edges[1] = edges[0] with { FromNodeId = "missing" };
        var bindings = GovernedLoopGraphTestFixture.Bindings();
        bindings[1] = bindings[0];

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes, edges: edges, bindings: bindings));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "element.id.invalid" && error.Element.Kind == GovernedLoopGraphElementKind.Node);
        Assert.Contains(result.Errors, error => error.Code == "element.id.duplicate" && error.Element.Kind == GovernedLoopGraphElementKind.Node);
        Assert.Contains(result.Errors, error => error.Code == "element.id.duplicate" && error.Element.Kind == GovernedLoopGraphElementKind.ControlEdge);
        Assert.Contains(result.Errors, error => error.Code == "element.id.duplicate" && error.Element.Kind == GovernedLoopGraphElementKind.Binding);
    }

    [Fact]
    public void NormalizeRejectsUnreachableAndTerminalLessRegions()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes().Append(new GovernedLoopNodeDefinition("orphan", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "orphan-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>())).ToArray();

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes));

        Assert.Contains(result.Errors, error => error.Code == "graph.node.unreachable" && error.Element.Id == "orphan");
        Assert.Contains(result.Errors, error => error.Code == "graph.node.no-terminal-path" && error.Element.Id == "orphan");
        Assert.Contains(result.Errors, error => error.Code == "graph.node.dead-end" && error.Element.Id == "orphan");
    }

    [Fact]
    public void NormalizeUsesOnlyControlEdgesForReachabilityAndRejectsAmbientBindings()
    {
        var bindings = GovernedLoopGraphTestFixture.Bindings();
        bindings[0] = bindings[0] with { FromNodeId = "exit", FromPortId = "published-result" };

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(bindings: bindings));

        Assert.Contains(result.Errors, error => error.Code == "binding.source.not-control-predecessor" && error.Element.Id == "request-binding");
    }

    [Fact]
    public void NormalizeRejectsRequiredBindingWhenAControlPathBypassesItsProducer()
    {
        var edges = GovernedLoopGraphTestFixture.Edges().Append(new GovernedLoopControlEdgeDefinition("trigger-to-exit", "trigger", "exit", GovernedLoopControlCondition.Failure)).ToArray();

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(edges: edges));

        Assert.Contains(result.Errors, error => error.Code == "binding.source.not-control-dominator" && error.Element.Id == "result-binding");
    }

    [Fact]
    public void NormalizeRejectsRequiredBindingWhenDiamondBranchBypassesItsProducer()
    {
        var bypass = new GovernedLoopNodeDefinition("bypass", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "bypass-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var edges = new[]
        {
            new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Success),
            new GovernedLoopControlEdgeDefinition("trigger-to-bypass", "trigger", "bypass", GovernedLoopControlCondition.Failure),
            new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            new GovernedLoopControlEdgeDefinition("bypass-to-exit", "bypass", "exit", GovernedLoopControlCondition.Success)
        };

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: [.. GovernedLoopGraphTestFixture.Nodes(), bypass], edges: edges));

        Assert.Contains(result.Errors, error => error.Code == "binding.source.not-control-dominator" && error.Element.Id == "result-binding");
    }

    [Fact]
    public void NormalizeAcceptsRequiredBindingWhenProducerDominatesConsumerAcrossCycle()
    {
        var cycle = new GovernedLoopNodeDefinition("cycle", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "cycle-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var edges = new[]
        {
            new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-to-cycle", "infer", "cycle", GovernedLoopControlCondition.Failure),
            new GovernedLoopControlEdgeDefinition("cycle-to-infer", "cycle", "infer", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success)
        };

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: [.. GovernedLoopGraphTestFixture.Nodes(), cycle], edges: edges));

        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Errors, error => error.Code == "binding.source.not-control-dominator");
    }

    [Fact]
    public void NormalizeRejectsRequiredBindingWhenCycleCanEnterConsumerBeforeProducer()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        var producer = new GovernedLoopNodeDefinition("producer", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "producer-transform", 1), [GovernedLoopGraphTestFixture.OutputPort("request", GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var bindings = GovernedLoopGraphTestFixture.Bindings().Select(binding => binding.Id == "request-binding" ? binding with { FromNodeId = "producer", FromPortId = "request" } : binding).ToArray();
        var edges = new[]
        {
            new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-to-producer", "infer", "producer", GovernedLoopControlCondition.Failure),
            new GovernedLoopControlEdgeDefinition("producer-to-infer", "producer", "infer", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success)
        };

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: [.. nodes, producer], edges: edges, bindings: bindings));

        Assert.Contains(result.Errors, error => error.Code == "binding.source.not-control-dominator" && error.Element.Id == "request-binding");
    }

    [Fact]
    public void NormalizeRejectsRequiredInputConflictAndCompatibilityMismatch()
    {
        var bindings = GovernedLoopGraphTestFixture.Bindings();
        var conflicting = bindings.Append(bindings[0] with { Id = "second-request", Kind = GovernedLoopBindingKind.Context }).ToArray();
        var missing = bindings.Where(binding => binding.Id != "context-binding").ToArray();

        var conflictResult = GovernedLoopGraphNormalizer.Normalize(Candidate(bindings: conflicting));
        var missingResult = GovernedLoopGraphNormalizer.Normalize(Candidate(bindings: missing));

        Assert.Contains(conflictResult.Errors, error => error.Code == "binding.incompatible");
        Assert.Contains(conflictResult.Errors, error => error.Code == "binding.input.conflict");
        Assert.Contains(missingResult.Errors, error => error.Code == "binding.input.required" && error.Element.Id == "infer.invocation-context");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalizeRejectsSelfBindingsWithOrWithoutAControlCycle(bool includeCycle)
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with
        {
            Ports = [.. nodes[1].Ports, GovernedLoopGraphTestFixture.OutputPort("loop-output", GovernedLoopBindingKind.Data), GovernedLoopGraphTestFixture.InputPort("loop-input", GovernedLoopBindingKind.Data)]
        };
        var bindings = GovernedLoopGraphTestFixture.Bindings().Append(new GovernedLoopBindingDefinition("self-binding", GovernedLoopBindingKind.Data, "infer", "loop-output", "infer", "loop-input")).ToArray();
        var edges = includeCycle ? GovernedLoopGraphTestFixture.Edges().Append(new GovernedLoopControlEdgeDefinition("infer-loop", "infer", "infer", GovernedLoopControlCondition.Failure)).ToArray() : GovernedLoopGraphTestFixture.Edges();

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes, edges: edges, bindings: bindings));

        Assert.Contains(result.Errors, error => error.Code == "binding.self-reference.unsupported" && error.Element.Id == "self-binding");
    }

    [Fact]
    public void NormalizeRejectsTerminalOutgoingAndEntryIncomingControl()
    {
        var edges = GovernedLoopGraphTestFixture.Edges().Append(new GovernedLoopControlEdgeDefinition("exit-to-trigger", "exit", "trigger", GovernedLoopControlCondition.Always)).ToArray();

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(edges: edges));

        Assert.Contains(result.Errors, error => error.Code == "graph.terminal.outgoing-control");
        Assert.Contains(result.Errors, error => error.Code == "graph.entry.incoming-control");
    }

    [Fact]
    public void NormalizeEnforcesFanOutAtLimitAndLimitPlusOne()
    {
        var validNodes = GovernedLoopGraphTestFixture.Nodes().ToList();
        var validEdges = new List<GovernedLoopControlEdgeDefinition>();
        for (var index = 0; index < CustomLoopLimits.MaxGraphControlFanOut; index++)
        {
            var id = $"branch-{index:D2}";
            validNodes.Add(new GovernedLoopNodeDefinition(id, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "branch-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()));
            validEdges.Add(new GovernedLoopControlEdgeDefinition($"trigger-to-{id}", "trigger", id, GovernedLoopControlCondition.Always));
            validEdges.Add(new GovernedLoopControlEdgeDefinition($"{id}-to-infer", id, "infer", GovernedLoopControlCondition.Success));
        }

        validEdges.Add(new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success));
        var atLimit = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: validNodes, edges: validEdges));
        var extraNode = new GovernedLoopNodeDefinition("branch-extra", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "branch-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());
        var overLimit = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: validNodes.Append(extraNode).ToArray(), edges: validEdges.Append(new GovernedLoopControlEdgeDefinition("trigger-to-branch-extra", "trigger", "branch-extra", GovernedLoopControlCondition.Always)).Append(new GovernedLoopControlEdgeDefinition("branch-extra-to-infer", "branch-extra", "infer", GovernedLoopControlCondition.Success)).ToArray()));

        Assert.DoesNotContain(atLimit.Errors, error => error.Code == "graph.node.fan-out");
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.node.fan-out");
    }

    [Fact]
    public void NormalizeBoundsRawCollectionsAndErrors()
    {
        var tooManyNodes = Enumerable.Range(0, CustomLoopLimits.MaxGraphNodes + 1).Select(index => (GovernedLoopNodeDefinition?)new GovernedLoopNodeDefinition($"node-{index}", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>())).ToArray();
        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: tooManyNodes));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "graph.nodes.count");
        Assert.True(result.Errors.Count <= CustomLoopLimits.MaxGraphValidationErrors);
        Assert.All(result.Errors, error =>
        {
            Assert.True(error.Code.Length <= CustomLoopLimits.MaxGraphValidationErrorCodeCharacters);
            Assert.True(error.Element.Path.Length <= CustomLoopLimits.MaxGraphValidationErrorPathCharacters);
            Assert.True(error.Message.Length <= CustomLoopLimits.MaxGraphValidationErrorMessageCharacters);
        });
    }

    [Fact]
    public void NormalizeEnforcesNodeCountAtLimitAndLimitPlusOne()
    {
        var atLimit = GovernedLoopGraphNormalizer.Normalize(NodeLimitCandidate(CustomLoopLimits.MaxGraphNodes));
        var overLimit = GovernedLoopGraphNormalizer.Normalize(NodeLimitCandidate(CustomLoopLimits.MaxGraphNodes + 1));

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.nodes.count");
    }

    [Fact]
    public void NormalizeEnforcesControlEdgeCountAtLimitAndLimitPlusOne()
    {
        var edges = GovernedLoopGraphTestFixture.Edges().Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphControlEdges - 2).Select(index => new GovernedLoopControlEdgeDefinition($"parallel-{index:D3}", "trigger", "infer", GovernedLoopControlCondition.Always))).ToArray();
        var atLimit = GovernedLoopGraphNormalizer.Normalize(Candidate(edges: edges));
        var overLimit = GovernedLoopGraphNormalizer.Normalize(Candidate(edges: [.. edges, new GovernedLoopControlEdgeDefinition("parallel-extra", "trigger", "infer", GovernedLoopControlCondition.Always)]));

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.control-edges.count");
    }

    [Fact]
    public void NormalizeEnforcesPortCountAtLimitAndLimitPlusOne()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with
        {
            Ports = [.. nodes[1].Ports, .. Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode - nodes[1].Ports.Count).Select(index => GovernedLoopGraphTestFixture.OutputPort($"extra-{index:D2}", GovernedLoopBindingKind.Data, required: false))]
        };
        var atLimit = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes));
        nodes = nodes.ToArray();
        nodes[1] = nodes[1] with { Ports = [.. nodes[1].Ports, GovernedLoopGraphTestFixture.OutputPort("extra-over", GovernedLoopBindingKind.Data, required: false)] };
        var overLimit = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes));

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "node.ports.count");
    }

    [Fact]
    public void NormalizeEnforcesBindingCountAtLimitAndLimitPlusOne()
    {
        var atLimit = GovernedLoopGraphNormalizer.Normalize(BindingLimitCandidate(includeExtra: false));
        var overLimit = GovernedLoopGraphNormalizer.Normalize(BindingLimitCandidate(includeExtra: true));

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.bindings.count");
    }

    [Fact]
    public void NormalizeEnforcesValueSchemaCountAtLimitAndLimitPlusOne()
    {
        var candidate = Candidate();
        var schemas = GovernedLoopGraphTestFixture.Schemas().Concat(Enumerable.Range(0, CustomLoopLimits.MaxGraphValueSchemas - 1).Select(index => new GovernedLoopValueSchemaDefinition($"schema-{index:D3}", GovernedLoopValueKind.Text, false))).ToArray();
        var atLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { ValueSchemas = schemas });
        var overLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { ValueSchemas = [.. schemas, new GovernedLoopValueSchemaDefinition("schema-extra", GovernedLoopValueKind.Text, false)] });

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.value-schemas.count");
    }

    [Fact]
    public void NormalizeEnforcesOutputCountAtLimitAndLimitPlusOne()
    {
        var candidate = Candidate();
        var outputs = Enumerable.Range(0, CustomLoopLimits.MaxGraphOutputs).Select(index => new GovernedLoopOutputDefinition($"result-{index:D2}", "text", "exit", "published-result", true)).ToArray();
        var atLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { OutputContract = new GovernedLoopOutputContract("Return bounded results.", outputs) });
        var overLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { OutputContract = new GovernedLoopOutputContract("Return bounded results.", [.. outputs, new GovernedLoopOutputDefinition("result-extra", "text", "exit", "published-result", true)]) });

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "output.items.count");
    }

    [Fact]
    public void NormalizeEnforcesTerminalIdCountAtLimitAndLimitPlusOne()
    {
        var candidate = Candidate();
        // An overall-valid 127-terminal graph cannot fit the independent 128-node and fan-out-16 caps, so duplicate valid references isolate the terminal-list boundary while preserving the expected independent identity error.
        var terminalIds = Enumerable.Repeat<string?>("exit", CustomLoopLimits.MaxGraphNodes - 1).ToArray();
        var atLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { TerminalNodeIds = terminalIds });
        var overLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { TerminalNodeIds = [.. terminalIds, "exit"] });

        Assert.False(atLimit.IsValid);
        Assert.DoesNotContain(atLimit.Errors, error => error.Code == "graph.terminal-node-ids.count");
        Assert.Contains(atLimit.Errors, error => error.Code == "element.id.duplicate");
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.terminal-node-ids.count");
    }

    [Fact]
    public void NormalizeEnforcesNodeParameterCountAtLimitAndLimitPlusOne()
    {
        var candidate = Candidate();
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        var parameters = new Dictionary<string, string> { ["instruction"] = "Answer safely." };
        foreach (var index in Enumerable.Range(0, CustomLoopLimits.MaxGraphDescriptorParameters - 1))
        {
            parameters[$"optional-{index:D2}"] = "x";
        }

        nodes[1] = nodes[1] with { Parameters = parameters };
        var atLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { Nodes = nodes });
        parameters = new Dictionary<string, string>(parameters) { ["optional-extra"] = "x" };
        nodes = nodes.ToArray();
        nodes[1] = nodes[1] with { Parameters = parameters };
        var overLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { Nodes = nodes });

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "node.parameters.count");
    }

    [Fact]
    public void NormalizeEnforcesParameterValueCharacterCountAtLimitAndLimitPlusOne()
    {
        var candidate = Candidate();
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = new string('a', CustomLoopLimits.MaxGraphParameterValueCharacters) } };
        var atLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { Nodes = nodes });
        nodes = nodes.ToArray();
        nodes[1] = nodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = new string('a', CustomLoopLimits.MaxGraphParameterValueCharacters + 1) } };
        var overLimit = GovernedLoopGraphNormalizer.Normalize(candidate with { Nodes = nodes });

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "node.parameter-value.invalid");
    }

    [Fact]
    public void NormalizeCapsAndOrdinallySortsErrorsIndependentlyOfElementPermutation()
    {
        var nodes = Enumerable.Range(0, CustomLoopLimits.MaxGraphNodes).Select(index => (GovernedLoopNodeDefinition?)new GovernedLoopNodeDefinition($"malformed-{index:D3}", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Unknown, "INVALID", 0), [new GovernedLoopPortDefinition("INVALID", GovernedLoopPortDirection.Unknown, GovernedLoopBindingKind.Unknown, "missing", true)], GovernedLoopAuthorityCeiling.Create(["outside-loop"]), new Dictionary<string, string> { ["INVALID"] = " bad" })).ToArray();
        var forward = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes));
        var reverse = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes.Reverse().ToArray()));

        Assert.Equal(CustomLoopLimits.MaxGraphValidationErrors, forward.Errors.Count);
        Assert.Equal(forward.Errors, reverse.Errors);
        Assert.Equal(forward.Errors.OrderBy(error => error.Element.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal).ThenBy(error => error.Element.Id, StringComparer.Ordinal), forward.Errors);
    }

    [Fact]
    public void NormalizeReturnsIdenticalErrorsAcrossBoundedTopologyPermutations()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes().Append(new GovernedLoopNodeDefinition("orphan", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "orphan-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>())).ToArray();
        var expected = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes));
        for (var offset = 0; offset < nodes.Length; offset++)
        {
            var permutedNodes = nodes.Skip(offset).Concat(nodes.Take(offset)).Reverse().ToArray();
            var edges = GovernedLoopGraphTestFixture.Edges().Skip(offset % GovernedLoopGraphTestFixture.Edges().Length).Concat(GovernedLoopGraphTestFixture.Edges().Take(offset % GovernedLoopGraphTestFixture.Edges().Length)).ToArray();
            var actual = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: permutedNodes, edges: edges, bindings: GovernedLoopGraphTestFixture.Bindings().Reverse().ToArray()));
            Assert.Equal(expected.Errors, actual.Errors);
        }
    }

    [Fact]
    public void NormalizeAttributesMissingOuterContractsWithoutThrowing()
    {
        var candidate = Candidate() with
        {
            SchemaVersion = 2,
            GraphId = null,
            RevisionId = "INVALID",
            Purpose = null,
            OwningRole = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("INVALID", 0), "short"),
            EntryNodeId = null,
            TerminalNodeIds = null,
            AuthorityCeiling = null,
            ValueSchemas = null,
            Nodes = null,
            ControlEdges = null,
            Bindings = null,
            OutputContract = null,
            DisplayMetadata = null
        };

        var result = GovernedLoopGraphNormalizer.Normalize(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "graph.schema-version.unsupported");
        Assert.Contains(result.Errors, error => error.Code == "graph.role-id.invalid" && error.Element.Path == "graph.owningRole.roleId");
        Assert.Contains(result.Errors, error => error.Code == "graph.role-revision.invalid" && error.Element.Path == "graph.owningRole.revision");
        Assert.Contains(result.Errors, error => error.Code == "graph.role-content-hash.invalid" && error.Element.Path == "graph.owningRole.contentHash");
        Assert.Contains(result.Errors, error => error.Code == "graph.authority.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.value-schemas.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.nodes.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.control-edges.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.bindings.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.terminal-node-ids.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.output-contract.required");
        Assert.Contains(result.Errors, error => error.Code == "graph.display.required");
    }

    [Fact]
    public void NormalizeRejectsMissingOrIncompleteOwningRolePinsWithoutThrowing()
    {
        var missing = GovernedLoopGraphNormalizer.Normalize(Candidate() with { OwningRole = null });
        var missingIdentity = GovernedLoopGraphNormalizer.Normalize(Candidate() with
        {
            OwningRole = new ContextualRoleRevisionPin(null!, new string('a', 64)),
        });

        Assert.Contains(missing.Errors, error => error.Code == "graph.role.required" && error.Element.Path == "graph.owningRole");
        Assert.Contains(missingIdentity.Errors, error => error.Code == "graph.role-id.invalid" && error.Element.Path == "graph.owningRole.roleId");
        Assert.Contains(missingIdentity.Errors, error => error.Code == "graph.role-revision.invalid" && error.Element.Path == "graph.owningRole.revision");
    }

    [Fact]
    public void NormalizeAttributesMalformedNestedContracts()
    {
        var schemas = new GovernedLoopValueSchemaDefinition?[]
        {
            null,
            new("unknown", GovernedLoopValueKind.Unknown, false, "INVALID"),
            new("array", GovernedLoopValueKind.Array, false, ElementSchemaId: "missing"),
            new("scalar", GovernedLoopValueKind.Text, false, ElementSchemaId: "unknown")
        };
        var nodes = NodesForMalformedContracts();
        var edges = new GovernedLoopControlEdgeDefinition?[]
        {
            null,
            new("bad-edge", "INVALID", "missing", GovernedLoopControlCondition.Unknown)
        };
        var bindings = new GovernedLoopBindingDefinition?[]
        {
            null,
            new("bad-binding", GovernedLoopBindingKind.Unknown, "INVALID", "missing", "missing", "missing")
        };
        var output = new GovernedLoopOutputContract(" Bad output", [new GovernedLoopOutputDefinition("bad-output", "missing", "trigger", "missing", true)]);
        var display = new GovernedLoopDisplayMetadata(" Bad display", "unsafe\uE000", [new GovernedLoopNodeDisplayMetadata("missing", "", " bad", CustomLoopLimits.MaxGraphCanvasCoordinate + 1, 0)]);
        var candidate = Candidate() with { ValueSchemas = schemas, Nodes = nodes, ControlEdges = edges, Bindings = bindings, TerminalNodeIds = [null, "missing"], OutputContract = output, DisplayMetadata = display };

        var result = GovernedLoopGraphNormalizer.Normalize(candidate);

        Assert.Contains(result.Errors, error => error.Code == "schema.kind.invalid");
        Assert.Contains(result.Errors, error => error.Code == "schema.element.missing");
        Assert.Contains(result.Errors, error => error.Code == "schema.element.unexpected");
        Assert.Contains(result.Errors, error => error.Code == "node.descriptor.required");
        Assert.Contains(result.Errors, error => error.Code == "node.kind.invalid");
        Assert.Contains(result.Errors, error => error.Code == "node.authority.widens-loop");
        Assert.Contains(result.Errors, error => error.Code == "port.direction.invalid");
        Assert.Contains(result.Errors, error => error.Code == "port.binding-kind.invalid");
        Assert.Contains(result.Errors, error => error.Code == "port.schema.missing");
        Assert.Contains(result.Errors, error => error.Code == "edge.condition.invalid");
        Assert.Contains(result.Errors, error => error.Code == "binding.kind.invalid");
        Assert.Contains(result.Errors, error => error.Code == "output.source-node.not-success-terminal");
        Assert.Contains(result.Errors, error => error.Code == "display.coordinates.invalid");
    }

    [Fact]
    public void NormalizeReturnsStructuralErrorsWhenOutputSourceDescriptorIsNull()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[2] = nodes[2] with { Descriptor = null! };

        var result = GovernedLoopGraphNormalizer.Normalize(Candidate(nodes: nodes));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "node.descriptor.required" && error.Element.Id == "exit");
        Assert.Contains(result.Errors, error => error.Code == "output.source-node.not-success-terminal" && error.Element.Id == "result");
    }

    [Fact]
    public void NormalizeEnforcesCondensedDepthAtLimitAndLimitPlusOne()
    {
        var atLimit = GovernedLoopGraphNormalizer.Normalize(DepthCandidate(CustomLoopLimits.MaxGraphControlDepth));
        var overLimit = GovernedLoopGraphNormalizer.Normalize(DepthCandidate(CustomLoopLimits.MaxGraphControlDepth + 1));

        Assert.True(atLimit.IsValid);
        Assert.Contains(overLimit.Errors, error => error.Code == "graph.control-depth");
    }

    private static GovernedLoopGraphCandidate DepthCandidate(int depth)
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes().ToList();
        var edges = new List<GovernedLoopControlEdgeDefinition>();
        var prior = "trigger";
        for (var index = 0; index < depth - 3; index++)
        {
            var id = $"depth-{index:D2}";
            nodes.Add(new GovernedLoopNodeDefinition(id, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "depth-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()));
            edges.Add(new GovernedLoopControlEdgeDefinition($"{prior}-to-{id}", prior, id, GovernedLoopControlCondition.Always));
            prior = id;
        }

        edges.Add(new GovernedLoopControlEdgeDefinition("depth-to-infer", prior, "infer", GovernedLoopControlCondition.Always));
        edges.Add(new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success));
        return Candidate(nodes: nodes, edges: edges);
    }

    private static GovernedLoopGraphCandidate NodeLimitCandidate(int count)
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes().ToList();
        var edges = GovernedLoopGraphTestFixture.Edges().ToList();
        var parents = new Queue<string>();
        var availableChildren = new Dictionary<string, int>(StringComparer.Ordinal) { ["infer"] = CustomLoopLimits.MaxGraphControlFanOut - 1 };
        parents.Enqueue("infer");
        for (var index = 0; index < count - GovernedLoopGraphTestFixture.Nodes().Length; index++)
        {
            while (availableChildren[parents.Peek()] == 0)
            {
                parents.Dequeue();
            }

            var parent = parents.Peek();
            var id = $"node-{index:D3}";
            nodes.Add(new GovernedLoopNodeDefinition(id, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "bounded-transform", 1), [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()));
            edges.Add(new GovernedLoopControlEdgeDefinition($"{parent}-to-{id}", parent, id, GovernedLoopControlCondition.Always));
            edges.Add(new GovernedLoopControlEdgeDefinition($"{id}-to-exit", id, "exit", GovernedLoopControlCondition.Success));
            availableChildren[parent]--;
            availableChildren[id] = CustomLoopLimits.MaxGraphControlFanOut - 1;
            parents.Enqueue(id);
        }

        return Candidate(nodes: nodes, edges: edges);
    }

    private static GovernedLoopGraphCandidate BindingLimitCandidate(bool includeExtra)
    {
        var authority = GovernedLoopAuthorityCeiling.Create([]);
        var nodes = new List<GovernedLoopNodeDefinition>
        {
            new("trigger", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [], authority, new Dictionary<string, string>()),
            new("exit", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1), [GovernedLoopGraphTestFixture.OutputPort("published-result", GovernedLoopBindingKind.Data)], authority, new Dictionary<string, string>())
        };
        var edges = new List<GovernedLoopControlEdgeDefinition>();
        var bindings = new List<GovernedLoopBindingDefinition>();
        for (var nodeIndex = 0; nodeIndex < 16; nodeIndex++)
        {
            var producerId = $"producer-{nodeIndex:D2}";
            var consumerId = $"consumer-{nodeIndex:D2}";
            nodes.Add(new GovernedLoopNodeDefinition(producerId, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "producer", 1), Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode).Select(portIndex => GovernedLoopGraphTestFixture.OutputPort($"output-{portIndex:D2}", GovernedLoopBindingKind.Data)).ToArray(), authority, new Dictionary<string, string>()));
            nodes.Add(new GovernedLoopNodeDefinition(consumerId, new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "consumer", 1), Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode).Select(portIndex => GovernedLoopGraphTestFixture.InputPort($"input-{portIndex:D2}", GovernedLoopBindingKind.Data)).ToArray(), authority, new Dictionary<string, string>()));
            edges.Add(new GovernedLoopControlEdgeDefinition($"trigger-to-{producerId}", "trigger", producerId, GovernedLoopControlCondition.Always));
            edges.Add(new GovernedLoopControlEdgeDefinition($"{producerId}-to-{consumerId}", producerId, consumerId, GovernedLoopControlCondition.Success));
            edges.Add(new GovernedLoopControlEdgeDefinition($"{consumerId}-to-exit", consumerId, "exit", GovernedLoopControlCondition.Success));
            bindings.AddRange(Enumerable.Range(0, CustomLoopLimits.MaxGraphPortsPerNode).Select(portIndex => new GovernedLoopBindingDefinition($"binding-{nodeIndex:D2}-{portIndex:D2}", GovernedLoopBindingKind.Data, producerId, $"output-{portIndex:D2}", consumerId, $"input-{portIndex:D2}")));
        }

        if (includeExtra)
        {
            nodes.Add(new GovernedLoopNodeDefinition("producer-extra", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "producer", 1), [GovernedLoopGraphTestFixture.OutputPort("output", GovernedLoopBindingKind.Data)], authority, new Dictionary<string, string>()));
            nodes.Add(new GovernedLoopNodeDefinition("consumer-extra", new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Transform, "consumer", 1), [GovernedLoopGraphTestFixture.InputPort("input", GovernedLoopBindingKind.Data)], authority, new Dictionary<string, string>()));
            edges.Add(new GovernedLoopControlEdgeDefinition("consumer-00-to-producer-extra", "consumer-00", "producer-extra", GovernedLoopControlCondition.Always));
            edges.Add(new GovernedLoopControlEdgeDefinition("producer-extra-to-consumer-extra", "producer-extra", "consumer-extra", GovernedLoopControlCondition.Success));
            edges.Add(new GovernedLoopControlEdgeDefinition("consumer-extra-to-exit", "consumer-extra", "exit", GovernedLoopControlCondition.Success));
            bindings.Add(new GovernedLoopBindingDefinition("binding-extra", GovernedLoopBindingKind.Data, "producer-extra", "output", "consumer-extra", "input"));
        }

        return new GovernedLoopGraphCandidate(1, "binding-limit", "revision-1", "Validate binding limits.", GovernedLoopGraphTestFixture.Role(), "trigger", ["exit"], authority, GovernedLoopGraphTestFixture.Schemas(), nodes, edges, bindings, new GovernedLoopOutputContract("Return the result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]), new GovernedLoopDisplayMetadata("Binding limit", "Display only.", []));
    }

    private static GovernedLoopNodeDefinition?[] NodesForMalformedContracts()
    {
        var trigger = GovernedLoopGraphTestFixture.Nodes()[0] with { Descriptor = null!, Ports = null!, AuthorityCeiling = null!, Parameters = null! };
        var malformed = GovernedLoopGraphTestFixture.Nodes()[1] with
        {
            Descriptor = new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Unknown, "INVALID", 0),
            AuthorityCeiling = GovernedLoopAuthorityCeiling.Create(["outside-loop"]),
            Ports =
            [
                new GovernedLoopPortDefinition("bad-port", GovernedLoopPortDirection.Unknown, GovernedLoopBindingKind.Unknown, "missing", true),
                new GovernedLoopPortDefinition("bad-port", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "unknown", true)
            ],
            Parameters = new Dictionary<string, string> { ["INVALID"] = " bad" }
        };
        return [null, trigger, malformed];
    }

    private static GovernedLoopGraphCandidate Candidate(
        string? purpose = "Research one question within explicit context and authority.",
        IReadOnlyList<GovernedLoopNodeDefinition?>? nodes = null,
        IReadOnlyList<GovernedLoopControlEdgeDefinition?>? edges = null,
        IReadOnlyList<GovernedLoopBindingDefinition?>? bindings = null)
    {
        return new GovernedLoopGraphCandidate(
            1,
            "research-loop",
            "revision-1",
            purpose,
            GovernedLoopGraphTestFixture.Role(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create(["model-inference", "workspace-read"]),
            GovernedLoopGraphTestFixture.Schemas(),
            nodes ?? GovernedLoopGraphTestFixture.Nodes(),
            edges ?? GovernedLoopGraphTestFixture.Edges(),
            bindings ?? GovernedLoopGraphTestFixture.Bindings(),
            GovernedLoopGraphTestFixture.Output(),
            GovernedLoopGraphTestFixture.Display());
    }
}
