using System.Globalization;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Builds one deterministic supported linear plan by traversing canonical control edges from the graph entry.</summary>
public static class GovernedLoopSequentialPlanBuilder
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";

    /// <summary>Builds a plan for one linear Trigger, mixed Inference/Transform/Validate sequence, and successful Exit.</summary>
    public static GovernedLoopSequentialPlanBuildResult Build(GovernedLoopGraphRevisionArtifact? artifact)
    {
        if (!IsValidArtifact(artifact))
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.InvalidArtifact, "$");
        }

        var graph = artifact!.Graph;
        if (graph.Nodes.Any(node => !GovernedLoopSequentialNodeDescriptors.IsSupported(node.Descriptor)))
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedDescriptor, "$.graph.nodes");
        }

        if (graph.Nodes.Count is < CustomLoopLimits.MinInferenceSteps + 2 or > CustomLoopLimits.MaxGraphNodes
            || graph.ControlEdges.Count != graph.Nodes.Count - 1
            || graph.TerminalNodeIds.Count != 1)
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph");
        }

        var nodeById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!nodeById.TryGetValue(graph.EntryNodeId, out var entry)
            || !Equals(entry.Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger))
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.entryNodeId");
        }

        var incoming = graph.ControlEdges.GroupBy(edge => edge.ToNodeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var outgoing = graph.ControlEdges.GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var planNodes = new List<GovernedLoopSequentialPlanNode>(graph.Nodes.Count);
        var current = entry;
        while (visited.Add(current.Id))
        {
            var currentIncoming = incoming.GetValueOrDefault(current.Id) ?? [];
            var currentOutgoing = outgoing.GetValueOrDefault(current.Id) ?? [];
            var ordinal = planNodes.Count;
            var isEntry = ordinal == 0;
            var isExit = Equals(current.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit);
            if (isEntry ? currentIncoming.Length != 0 || currentOutgoing.Length != 1 : isExit ? currentIncoming.Length != 1 || currentOutgoing.Length != 0 : currentIncoming.Length != 1 || currentOutgoing.Length != 1)
            {
                return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.controlEdges");
            }

            if (!isEntry
                && !isExit
                && current.Descriptor.Kind is not (GovernedLoopNodeKind.Inference or GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate))
            {
                return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.nodes");
            }

            if (isExit && !string.Equals(graph.TerminalNodeIds[0], current.Id, StringComparison.Ordinal))
            {
                return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.terminalNodeIds");
            }

            var outgoingEdge = currentOutgoing.SingleOrDefault();
            var expectedCondition = isEntry ? GovernedLoopControlCondition.Always : GovernedLoopControlCondition.Success;
            if (outgoingEdge is not null && outgoingEdge.Condition != expectedCondition)
            {
                return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.controlEdges");
            }

            planNodes.Add(new GovernedLoopSequentialPlanNode(
                ordinal,
                current.Id,
                new GovernedLoopNodeDescriptor(current.Descriptor.Kind, current.Descriptor.TypeId, current.Descriptor.Version),
                currentIncoming.SingleOrDefault()?.Id,
                outgoingEdge?.Id));
            if (isExit)
            {
                break;
            }

            if (outgoingEdge is null || !nodeById.TryGetValue(outgoingEdge.ToNodeId, out current))
            {
                return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.controlEdges");
            }
        }

        var inferenceCount = planNodes.Count(node => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference));
        if (visited.Count != graph.Nodes.Count
            || planNodes.Count != graph.Nodes.Count
            || inferenceCount is < CustomLoopLimits.MinInferenceSteps or > CustomLoopLimits.MaxInferenceSteps
            || !Equals(planNodes[^1].Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit))
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph");
        }

        var contractFailurePath = ExactContractFailurePath(graph, planNodes);
        if (contractFailurePath is not null)
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedContract, contractFailurePath);
        }

        var revision = GovernedLoopRevisionReference.Create(
            artifact.RevisionArtifact.Revision.SchemaVersion,
            artifact.RevisionArtifact.Revision.GraphId,
            artifact.RevisionArtifact.Revision.RevisionId,
            artifact.RevisionArtifact.Revision.ExecutableHash);
        var plan = new GovernedLoopSequentialPlan(
            1,
            revision,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            Array.AsReadOnly(planNodes.ToArray()));
        return new GovernedLoopSequentialPlanBuildResult(GovernedLoopSequentialPlanBuildStatus.Ready, plan, null);
    }

    private static bool IsValidArtifact(GovernedLoopGraphRevisionArtifact? artifact)
    {
        if (artifact is null)
        {
            return false;
        }

        try
        {
            return string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? ExactContractFailurePath(
        GovernedLoopGraphDefinition graph,
        IReadOnlyList<GovernedLoopSequentialPlanNode> planNodes)
    {
        if (!HasExactSchemaSet(graph))
        {
            return "$.graph.valueSchemas";
        }

        var allowsWorkspaceTools = graph.AuthorityCeiling.CapabilityIds.SequenceEqual(
            [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId],
            StringComparer.Ordinal);
        if (!allowsWorkspaceTools
            && !graph.AuthorityCeiling.CapabilityIds.SequenceEqual(
                [ConversationTurnCapabilityId, ModelInferenceCapabilityId],
                StringComparer.Ordinal))
        {
            return "$.graph.authorityCeiling";
        }

        var nodeById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var schemaById = graph.ValueSchemas.ToDictionary(schema => schema.Id, StringComparer.Ordinal);
        foreach (var planNode in planNodes)
        {
            var node = nodeById[planNode.NodeId];
            var exact = planNode.Descriptor.Kind switch
            {
                GovernedLoopNodeKind.Trigger => IsExactTrigger(node, schemaById),
                GovernedLoopNodeKind.Inference => IsExactInference(node, schemaById, allowsWorkspaceTools),
                GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate => IsExactPureNode(node, schemaById),
                GovernedLoopNodeKind.Exit => IsExactExit(node, schemaById),
                _ => false,
            };
            if (!exact)
            {
                return "$.graph.nodes";
            }
        }

        if (!HasExactBindings(graph, planNodes))
        {
            return "$.graph.bindings";
        }

        var exitNode = nodeById[planNodes[^1].NodeId];
        var published = exitNode.Ports.Single(port => string.Equals(port.Id, "published-result", StringComparison.Ordinal));
        if (graph.OutputContract.Outputs.Count != 1
            || graph.OutputContract.Outputs[0] is not { Id: "result", SourcePortId: "published-result", Required: true } output
            || !string.Equals(output.SourceNodeId, exitNode.Id, StringComparison.Ordinal)
            || !string.Equals(output.ValueSchemaId, published.ValueSchemaId, StringComparison.Ordinal))
        {
            return "$.graph.outputContract";
        }

        return null;
    }

    private static bool HasExactSchemaSet(GovernedLoopGraphDefinition graph)
    {
        var schemas = graph.ValueSchemas.ToDictionary(schema => schema.Id, StringComparer.Ordinal);
        foreach (var schema in graph.ValueSchemas)
        {
            if (schema.Kind is GovernedLoopValueKind.Unknown or GovernedLoopValueKind.Binary
                || schema.Format is not null
                || !SchemaTreeIsBounded(schema, schemas, new HashSet<string>(StringComparer.Ordinal), 0))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SchemaTreeIsBounded(
        GovernedLoopValueSchemaDefinition schema,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        HashSet<string> path,
        int depth)
    {
        if (depth > CustomLoopLimits.MaxGraphTypedValueDepth || !path.Add(schema.Id))
        {
            return false;
        }

        var valid = schema.Kind != GovernedLoopValueKind.Array
            || schema.ElementSchemaId is not null
                && schemas.TryGetValue(schema.ElementSchemaId, out var element)
                && SchemaTreeIsBounded(element, schemas, path, depth + 1);
        path.Remove(schema.Id);
        return valid;
    }

    private static bool IsExactTrigger(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => node.AuthorityCeiling.CapabilityIds.Count == 0
            && node.Parameters.Count == 0
            && HasExactPortSet(node, schemas,
                ("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text),
                ("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, GovernedLoopValueKind.Text));

    private static bool IsExactInference(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        bool allowsWorkspaceTools)
        => node.AuthorityCeiling.CapabilityIds.SequenceEqual(
                allowsWorkspaceTools
                    ? [ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]
                    : [ModelInferenceCapabilityId],
                StringComparer.Ordinal)
            && node.Parameters.Count == 1
            && node.Parameters.TryGetValue("instruction", out var instruction)
            && !string.IsNullOrWhiteSpace(instruction)
            && HasExactPortSet(node, schemas,
                ("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text),
                ("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context, GovernedLoopValueKind.Text),
                ("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text));

    private static bool IsExactExit(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => node.AuthorityCeiling.CapabilityIds.SequenceEqual([ConversationTurnCapabilityId], StringComparer.Ordinal)
            && node.Parameters.Count == 0
            && HasExactPortSet(node, schemas,
                ("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text),
                ("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text));

    private static bool IsExactPureNode(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (node.AuthorityCeiling.CapabilityIds.Count != 0
            || !GovernedLoopPureNodeCatalogContract.TryResolve(node.Descriptor, out var contract)
            || contract is null
            || !HasExactPurePorts(node, contract, schemas)
            || !HasExactPureParameters(node, contract))
        {
            return false;
        }

        var input = Input(node, GovernedLoopPureNodeVocabulary.InputPort);
        var output = Output(node, GovernedLoopPureNodeVocabulary.OutputPort);
        var result = Output(node, GovernedLoopPureNodeVocabulary.ResultPort);
        return node.Descriptor.TypeId switch
        {
            GovernedLoopPureNodeVocabulary.IdentityTransform => input is not null
                && output is not null
                && string.Equals(input.ValueSchemaId, output.ValueSchemaId, StringComparison.Ordinal),
            GovernedLoopPureNodeVocabulary.StructuredSelect => input is not null
                && !schemas[input.ValueSchemaId].Nullable,
            GovernedLoopPureNodeVocabulary.OrderedTextConcat => IsExactConcat(node, schemas, output),
            GovernedLoopPureNodeVocabulary.SchemaConformance => result is not null
                && IsNonNullable(result, schemas),
            GovernedLoopPureNodeVocabulary.CanonicalEquality => IsExactEquality(node, schemas, result),
            GovernedLoopPureNodeVocabulary.InclusiveIntegerRange or GovernedLoopPureNodeVocabulary.InclusiveNumberRange
                => input is not null && !schemas[input.ValueSchemaId].Nullable && IsNonNullable(result, schemas) && HasOrderedRange(node),
            GovernedLoopPureNodeVocabulary.TextLength or GovernedLoopPureNodeVocabulary.ArrayLength
                => input is not null && !schemas[input.ValueSchemaId].Nullable && IsNonNullable(result, schemas) && HasOrderedIntegerRange(node),
            _ => false,
        };
    }

    private static bool HasExactPurePorts(
        GovernedLoopNodeDefinition node,
        GovernedLoopNodeCatalogDescriptor contract,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (node.Ports.Count != contract.Ports.Count)
        {
            return false;
        }

        var contracts = contract.Ports.ToDictionary(port => port.Id, StringComparer.Ordinal);
        foreach (var port in node.Ports)
        {
            if (!contracts.TryGetValue(port.Id, out var expected)
                || !schemas.TryGetValue(port.ValueSchemaId, out var schema)
                || port.Direction != expected.Direction
                || port.BindingKind != expected.BindingKind
                || port.Required != expected.Required
                || !expected.AllowedValueKinds.Contains(schema.Kind))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactPureParameters(
        GovernedLoopNodeDefinition node,
        GovernedLoopNodeCatalogDescriptor contract)
    {
        if (node.Parameters.Count != contract.Parameters.Count)
        {
            return false;
        }

        var parameters = contract.Parameters.ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        return node.Parameters.All(parameter => parameters.TryGetValue(parameter.Key, out var expected)
            && IsCompatibleParameter(parameter.Value, expected));
    }

    private static bool IsCompatibleParameter(string value, GovernedLoopCatalogParameterContract contract)
    {
        if (value.Length < contract.MinimumCharacters || value.Length > contract.MaximumCharacters)
        {
            return false;
        }

        return contract.ValueKind switch
        {
            GovernedLoopParameterValueKind.Text => true,
            GovernedLoopParameterValueKind.Integer => contract.MinimumInteger.HasValue
                && contract.MaximumInteger.HasValue
                && TryCanonicalInteger(value, out var integer)
                && integer >= contract.MinimumInteger.Value
                && integer <= contract.MaximumInteger.Value,
            GovernedLoopParameterValueKind.Number => TryCanonicalNumber(value, out _),
            GovernedLoopParameterValueKind.JsonPointer => IsJsonPointer(value),
            _ => false,
        };
    }

    private static bool HasOrderedRange(GovernedLoopNodeDefinition node)
        => node.Descriptor.TypeId == GovernedLoopPureNodeVocabulary.InclusiveIntegerRange
            ? HasOrderedIntegerRange(node)
            : TryCanonicalNumber(node.Parameters[GovernedLoopPureNodeVocabulary.MinimumParameter], out var minimum)
                && TryCanonicalNumber(node.Parameters[GovernedLoopPureNodeVocabulary.MaximumParameter], out var maximum)
                && minimum <= maximum;

    private static bool HasOrderedIntegerRange(GovernedLoopNodeDefinition node)
        => TryCanonicalInteger(node.Parameters[GovernedLoopPureNodeVocabulary.MinimumParameter], out var minimum)
            && TryCanonicalInteger(node.Parameters[GovernedLoopPureNodeVocabulary.MaximumParameter], out var maximum)
            && minimum <= maximum;

    private static bool TryCanonicalInteger(string value, out long integer)
        => long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer)
            && string.Equals(integer.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);

    private static bool TryCanonicalNumber(string value, out double number)
    {
        number = default;
        return GovernedLoopTypedValue.TryCreate(
                GovernedLoopTypedValue.CurrentSchemaVersion,
                GovernedLoopValueKind.Number,
                value,
                out var canonical,
                out _)
            && string.Equals(canonical!.CanonicalValueJson, value, StringComparison.Ordinal)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);
    }

    private static bool IsJsonPointer(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value[0] != '/')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '~' && (++index >= value.Length || value[index] is not ('0' or '1')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExactConcat(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        GovernedLoopPortDefinition? output)
    {
        var values = Input(node, GovernedLoopPureNodeVocabulary.ValuesPort);
        if (values is null
            || output is null
            || schemas[values.ValueSchemaId] is not { Nullable: false, ElementSchemaId: { } elementSchemaId }
            || !schemas.TryGetValue(elementSchemaId, out var element))
        {
            return false;
        }

        return element is { Kind: GovernedLoopValueKind.Text, Nullable: false }
            && IsNonNullable(output, schemas);
    }

    private static bool IsExactEquality(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        GovernedLoopPortDefinition? result)
    {
        var left = Input(node, GovernedLoopPureNodeVocabulary.LeftPort);
        var right = Input(node, GovernedLoopPureNodeVocabulary.RightPort);
        return left is not null
            && right is not null
            && schemas[left.ValueSchemaId].Kind == schemas[right.ValueSchemaId].Kind
            && IsNonNullable(result, schemas);
    }

    private static bool IsNonNullable(
        GovernedLoopPortDefinition? port,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => port is not null && !schemas[port.ValueSchemaId].Nullable;

    private static GovernedLoopPortDefinition? Input(GovernedLoopNodeDefinition node, string id)
        => node.Ports.SingleOrDefault(port => port.Direction == GovernedLoopPortDirection.Input && string.Equals(port.Id, id, StringComparison.Ordinal));

    private static GovernedLoopPortDefinition? Output(GovernedLoopNodeDefinition node, string id)
        => node.Ports.SingleOrDefault(port => port.Direction == GovernedLoopPortDirection.Output && string.Equals(port.Id, id, StringComparison.Ordinal));

    private static bool HasExactPortSet(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        params (string Id, GovernedLoopPortDirection Direction, GovernedLoopBindingKind BindingKind, GovernedLoopValueKind ValueKind)[] expected)
    {
        if (node.Ports.Count != expected.Length)
        {
            return false;
        }

        return expected.All(contract => node.Ports.Count(port => string.Equals(port.Id, contract.Id, StringComparison.Ordinal)
            && port.Direction == contract.Direction
            && port.BindingKind == contract.BindingKind
            && port.Required
            && schemas.TryGetValue(port.ValueSchemaId, out var schema)
            && !schema.Nullable
            && schema.Kind == contract.ValueKind) == 1);
    }

    private static bool HasExactBindings(
        GovernedLoopGraphDefinition graph,
        IReadOnlyList<GovernedLoopSequentialPlanNode> planNodes)
    {
        var ordinalByNodeId = planNodes.ToDictionary(node => node.NodeId, node => node.Ordinal, StringComparer.Ordinal);
        var expectedInputCount = graph.Nodes.Sum(node => node.Ports.Count(port => port.Direction == GovernedLoopPortDirection.Input));
        if (graph.Bindings.Count != expectedInputCount)
        {
            return false;
        }

        foreach (var node in graph.Nodes)
        {
            var inputs = node.Ports.Where(port => port.Direction == GovernedLoopPortDirection.Input).ToArray();
            var incoming = graph.Bindings.Where(binding => string.Equals(binding.ToNodeId, node.Id, StringComparison.Ordinal)).ToArray();
            if (incoming.Length != inputs.Length)
            {
                return false;
            }

            foreach (var input in inputs)
            {
                var matches = incoming.Where(binding => string.Equals(binding.ToPortId, input.Id, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1
                    || matches[0].Kind != input.BindingKind
                    || !ordinalByNodeId.TryGetValue(matches[0].FromNodeId, out var sourceOrdinal)
                    || sourceOrdinal >= ordinalByNodeId[node.Id])
                {
                    return false;
                }

                if (input.BindingKind == GovernedLoopBindingKind.Context
                    && (!Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference)
                        || !string.Equals(matches[0].FromNodeId, planNodes[0].NodeId, StringComparison.Ordinal)
                        || !string.Equals(matches[0].FromPortId, "invocation-context", StringComparison.Ordinal)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static GovernedLoopSequentialPlanBuildResult Failure(GovernedLoopSequentialPlanBuildStatus status, string path)
        => new(status, null, path);
}
