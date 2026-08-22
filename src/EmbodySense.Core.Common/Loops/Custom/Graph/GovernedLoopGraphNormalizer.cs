using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Common.Loops.Custom.Graph;

/// <summary>Normalizes raw schema-1 governed graphs and proves dependency-light structural and control-flow invariants.</summary>
public static class GovernedLoopGraphNormalizer
{
    /// <summary>Validates a raw candidate without throwing for malformed candidate content.</summary>
    /// <param name="candidate">The potentially invalid graph candidate.</param>
    /// <returns>A canonical graph only on success, otherwise bounded deterministic errors tied to exact elements.</returns>
    public static GovernedLoopGraphNormalizationResult Normalize(GovernedLoopGraphCandidate? candidate)
    {
        var errors = new GovernedLoopGraphErrorCollector();
        if (candidate is null)
        {
            errors.Add("graph.required", GovernedLoopGraphElementKind.Graph, null, "graph", "A graph candidate is required.");
            return new GovernedLoopGraphNormalizationResult(null, errors.ToSortedErrors());
        }

        ValidateScalars(candidate, errors);
        if (!GovernedModelContractValidator.IsValid(candidate.DefaultModelRoutingPolicy))
        {
            errors.Add("graph.model-routing.default.invalid", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph.defaultModelRoutingPolicy", "A complete typed loop-default model-routing policy is required.");
        }
        ValidateAuthorityCeiling(candidate.AuthorityCeiling, candidate.GraphId, "graph.authorityCeiling", errors);
        var schemas = Snapshot(candidate.ValueSchemas, CustomLoopLimits.MaxGraphValueSchemas, 1, "valueSchemas", GovernedLoopGraphElementKind.ValueSchema, errors);
        var nodes = Snapshot(candidate.Nodes, CustomLoopLimits.MaxGraphNodes, 2, "nodes", GovernedLoopGraphElementKind.Node, errors);
        var edges = Snapshot(candidate.ControlEdges, CustomLoopLimits.MaxGraphControlEdges, 1, "controlEdges", GovernedLoopGraphElementKind.ControlEdge, errors);
        var bindings = Snapshot(candidate.Bindings, CustomLoopLimits.MaxGraphBindings, 0, "bindings", GovernedLoopGraphElementKind.Binding, errors);
        var terminals = Snapshot(candidate.TerminalNodeIds, CustomLoopLimits.MaxGraphNodes - 1, 1, "terminalNodeIds", GovernedLoopGraphElementKind.Graph, errors);

        ValidateSchemas(schemas, errors);
        ValidateNodes(nodes, schemas, candidate.AuthorityCeiling, candidate.DefaultModelRoutingPolicy, errors);
        ValidateRunWideModelBudget(nodes, candidate.DefaultModelRoutingPolicy, candidate.GraphId, errors);
        ValidateEdges(edges, nodes, errors);
        ValidateBindings(bindings, nodes, edges, candidate.EntryNodeId, errors);
        ValidateTerminalsAndTopology(candidate.EntryNodeId, terminals, nodes, edges, errors);
        ValidateOutput(candidate.OutputContract, nodes, schemas, terminals, errors);
        ValidateDisplay(candidate.DisplayMetadata, nodes, errors);

        if (errors.Any)
        {
            return new GovernedLoopGraphNormalizationResult(null, errors.ToSortedErrors());
        }

        try
        {
            var graph = GovernedLoopGraphDefinition.Create(candidate.SchemaVersion, candidate.GraphId!, candidate.RevisionId!, candidate.Purpose!, candidate.OwningRole!, candidate.EntryNodeId!, terminals.Cast<string>(), candidate.AuthorityCeiling!, schemas.Cast<GovernedLoopValueSchemaDefinition>(), nodes.Cast<GovernedLoopNodeDefinition>(), edges.Cast<GovernedLoopControlEdgeDefinition>(), bindings.Cast<GovernedLoopBindingDefinition>(), candidate.OutputContract!, candidate.DisplayMetadata!, candidate.DefaultModelRoutingPolicy!);
            return new GovernedLoopGraphNormalizationResult(graph, Array.Empty<GovernedLoopGraphValidationError>());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            errors.Add("graph.canonicalization-failed", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph", "The candidate failed the canonical schema-1 value boundary.");
            return new GovernedLoopGraphNormalizationResult(null, errors.ToSortedErrors());
        }
    }

    private static void ValidateScalars(GovernedLoopGraphCandidate candidate, GovernedLoopGraphErrorCollector errors)
    {
        if (candidate.SchemaVersion != GovernedLoopGraphDefinition.CurrentSchemaVersion)
        {
            errors.Add("graph.schema-version.unsupported", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph.schemaVersion", "Only schema version 1 is accepted; compatibility translation is not supported.");
        }

        ValidateId(candidate.GraphId, "graph.id.required", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph.graphId", errors);
        ValidateId(candidate.RevisionId, "graph.revision-id.required", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph.revisionId", errors);
        ValidateOwningRole(candidate.OwningRole, candidate.GraphId, errors);
        ValidateId(candidate.EntryNodeId, "graph.entry-id.required", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph.entryNodeId", errors);
        ValidateText(candidate.Purpose, true, CustomLoopLimits.MaxDescriptionCharacters, "graph.purpose.invalid", GovernedLoopGraphElementKind.Graph, candidate.GraphId, "graph.purpose", errors);
        if (candidate.AuthorityCeiling is null)
        {
            errors.Add("graph.authority.required", GovernedLoopGraphElementKind.Authority, candidate.GraphId, "graph.authorityCeiling", "A non-granting loop authority ceiling is required.");
        }
    }

    private static void ValidateOwningRole(ContextualRoleRevisionPin? owningRole, string? graphId, GovernedLoopGraphErrorCollector errors)
    {
        if (owningRole is null)
        {
            errors.Add("graph.role.required", GovernedLoopGraphElementKind.Graph, graphId, "graph.owningRole", "An exact owning-role revision is required.");
            return;
        }

        if (owningRole.Identity is null)
        {
            errors.Add("graph.role-id.invalid", GovernedLoopGraphElementKind.Graph, graphId, "graph.owningRole.roleId", "The owning role identifier must be canonical.");
            errors.Add("graph.role-revision.invalid", GovernedLoopGraphElementKind.Graph, graphId, "graph.owningRole.revision", "The owning role revision must be positive.");
        }
        else
        {
            if (!ContextualRoleId.IsValid(owningRole.Identity.RoleId))
            {
                errors.Add("graph.role-id.invalid", GovernedLoopGraphElementKind.Graph, graphId, "graph.owningRole.roleId", "The owning role identifier must be canonical.");
            }

            if (owningRole.Identity.Revision < 1)
            {
                errors.Add("graph.role-revision.invalid", GovernedLoopGraphElementKind.Graph, graphId, "graph.owningRole.revision", "The owning role revision must be positive.");
            }
        }

        if (owningRole.ContentHash is not { Length: ContextualRoleLimits.Sha256HexCharacters }
            || owningRole.ContentHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            errors.Add("graph.role-content-hash.invalid", GovernedLoopGraphElementKind.Graph, graphId, "graph.owningRole.contentHash", "The owning role content hash must be a canonical lowercase SHA-256 digest.");
        }
    }

    private static T?[] Snapshot<T>(IReadOnlyList<T?>? values, int maximum, int minimum, string path, GovernedLoopGraphElementKind kind, GovernedLoopGraphErrorCollector errors)
    {
        if (values is null)
        {
            errors.Add($"graph.{ToCode(path)}.required", kind, null, $"graph.{path}", $"{path} is required.");
            return [];
        }

        if (values.Count < minimum || values.Count > maximum)
        {
            errors.Add($"graph.{ToCode(path)}.count", kind, null, $"graph.{path}", $"{path} must contain between {minimum} and {maximum} elements.");
        }

        var count = Math.Min(values.Count, maximum);
        var result = new T?[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = values[index];
            if (values[index] is null)
            {
                errors.Add($"graph.{ToCode(path)}.element-required", kind, null, $"graph.{path}[{index:D4}]", $"{path} cannot contain null elements.");
            }
        }

        return result;
    }

    private static void ValidateSchemas(IReadOnlyList<GovernedLoopValueSchemaDefinition?> schemas, GovernedLoopGraphErrorCollector errors)
    {
        ValidateIdentities(schemas, value => value?.Id, "valueSchemas", GovernedLoopGraphElementKind.ValueSchema, errors);
        var ids = schemas.Where(value => value is not null && CustomLoopArtifactIdentifier.IsValid(value.Id)).Select(value => value!.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (schema, index) in schemas.Select((value, index) => (value, index)))
        {
            if (schema is null)
            {
                continue;
            }

            var path = ElementPath("valueSchemas", schema.Id, index);
            if (!Enum.IsDefined(schema.Kind) || schema.Kind == GovernedLoopValueKind.Unknown)
            {
                errors.Add("schema.kind.invalid", GovernedLoopGraphElementKind.ValueSchema, schema.Id, $"{path}.kind", "The value-schema kind must be defined.");
            }

            if (schema.Format is not null)
            {
                ValidateId(schema.Format, "schema.format.invalid", GovernedLoopGraphElementKind.ValueSchema, schema.Id, $"{path}.format", errors);
            }

            if (schema.Kind == GovernedLoopValueKind.Array)
            {
                ValidateId(schema.ElementSchemaId, "schema.element.required", GovernedLoopGraphElementKind.ValueSchema, schema.Id, $"{path}.elementSchemaId", errors);
                if (CustomLoopArtifactIdentifier.IsValid(schema.ElementSchemaId) && !ids.Contains(schema.ElementSchemaId!))
                {
                    errors.Add("schema.element.missing", GovernedLoopGraphElementKind.ValueSchema, schema.Id, $"{path}.elementSchemaId", "The array element schema does not exist.");
                }
            }
            else if (schema.ElementSchemaId is not null)
            {
                errors.Add("schema.element.unexpected", GovernedLoopGraphElementKind.ValueSchema, schema.Id, $"{path}.elementSchemaId", "Only array schemas may declare an element schema.");
            }
        }
    }

    private static void ValidateNodes(
        IReadOnlyList<GovernedLoopNodeDefinition?> nodes,
        IReadOnlyList<GovernedLoopValueSchemaDefinition?> schemas,
        GovernedLoopAuthorityCeiling? loopAuthority,
        GovernedModelRoutingPolicy? defaultModelRoutingPolicy,
        GovernedLoopGraphErrorCollector errors)
    {
        ValidateIdentities(nodes, value => value?.Id, "nodes", GovernedLoopGraphElementKind.Node, errors);
        var schemaIds = schemas.Where(schema => schema is not null && CustomLoopArtifactIdentifier.IsValid(schema.Id)).Select(schema => schema!.Id).ToHashSet(StringComparer.Ordinal);
        var loopCapabilities = loopAuthority?.CapabilityIds.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var (node, index) in nodes.Select((value, index) => (value, index)))
        {
            if (node is null)
            {
                continue;
            }

            var path = ElementPath("nodes", node.Id, index);
            if (node.Descriptor is null)
            {
                errors.Add("node.descriptor.required", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor", "A node descriptor is required.");
            }
            else
            {
                if (!Enum.IsDefined(node.Descriptor.Kind) || node.Descriptor.Kind == GovernedLoopNodeKind.Unknown)
                {
                    errors.Add("node.kind.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor.kind", "The node kind must be defined.");
                }

                ValidateId(node.Descriptor.TypeId, "node.type-id.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor.typeId", errors);
                if (node.Descriptor.Version < 1)
                {
                    errors.Add("node.descriptor-version.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor.version", "The descriptor version must be positive.");
                }
            }

            if (node.AuthorityCeiling is null)
            {
                errors.Add("node.authority.required", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.authorityCeiling", "A non-granting node authority ceiling is required.");
            }
            else
            {
                ValidateAuthorityCeiling(node.AuthorityCeiling, node.Id, $"{path}.authorityCeiling", errors);
                if (node.AuthorityCeiling.CapabilityIds.Any(capability => !loopCapabilities.Contains(capability)))
                {
                    errors.Add("node.authority.widens-loop", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.authorityCeiling", "The node authority ceiling cannot widen the loop ceiling.");
                }
            }

            ValidatePorts(node, path, schemaIds, errors);
            ValidateParameters(node, path, errors);
            if (node.Descriptor?.Kind == GovernedLoopNodeKind.Inference)
            {
                if (node.ModelRoutingPolicy is not null && !GovernedModelContractValidator.IsValid(node.ModelRoutingPolicy))
                {
                    errors.Add("node.model-routing.override.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.modelRoutingPolicy", "The Inference routing override must be a complete typed policy.");
                }

                var effectivePolicy = node.ModelRoutingPolicy ?? defaultModelRoutingPolicy;
                if (GovernedModelContractValidator.IsValid(effectivePolicy) && node.AuthorityCeiling is not null)
                {
                    var nodeCapabilities = node.AuthorityCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
                    var candidateIds = CandidateProfileIds(effectivePolicy!);
                    if (candidateIds.Any(id => !loopCapabilities.Contains(id) || !nodeCapabilities.Contains(id)))
                    {
                        errors.Add("node.model-routing.authority.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.modelRoutingPolicy", "Every permitted model profile must remain inside both loop and node authority ceilings.");
                    }
                }

                if (node.AuthoredInputDataClasses is { } authoredClasses
                    && (authoredClasses.Count > CapabilityContractLimits.MaxDataClasses
                        || authoredClasses.Any(value => value is null || !CapabilityDataClass.TryParse(value.Value, out var parsed, out _) || !value.Equals(parsed))
                        || !authoredClasses.Select(value => value.Value).SequenceEqual(authoredClasses.Select(value => value.Value).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                        || authoredClasses.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() != authoredClasses.Count))
                {
                    errors.Add("node.model-routing.input-data-classes.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.authoredInputDataClasses", "Authored input-data classes must be a bounded canonical ordered set.");
                }
            }
            else if (node.ModelRoutingPolicy is not null || node.AuthoredInputDataClasses is not null)
            {
                errors.Add("node.model-routing.kind.invalid", GovernedLoopGraphElementKind.Node, node.Id, path, "Only Inference nodes may declare routing or input classification.");
            }
        }
    }

    private static IReadOnlyList<string> CandidateProfileIds(GovernedModelRoutingPolicy policy)
        => (policy.Selector.Kind == GovernedModelSelectorKind.Exact
                ? new[] { policy.Selector.ExactProfileId! }
                : policy.Selector.PermittedInheritedProfileIds)
            .Concat(policy.FallbackProfileIds)
            .Select(value => value.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void ValidateAuthorityCeiling(
        GovernedLoopAuthorityCeiling? ceiling,
        string? elementId,
        string path,
        GovernedLoopGraphErrorCollector errors)
    {
        if (ceiling?.CapabilityIds is null)
        {
            return;
        }

        if (ceiling.CapabilityIds.Count > CustomLoopLimits.MaxGraphAuthorityCapabilities)
        {
            errors.Add("authority.capabilities.count", GovernedLoopGraphElementKind.Authority, elementId, path, $"Authority ceilings may contain at most {CustomLoopLimits.MaxGraphAuthorityCapabilities} capabilities.");
            return;
        }

        var capabilities = ceiling.CapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).ToArray();
        if (capabilities.Any(value => !CapabilityId.TryParse(value, out _, out _))
            || capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length
            || !capabilities.SequenceEqual(capabilities.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            errors.Add("authority.capabilities.invalid", GovernedLoopGraphElementKind.Authority, elementId, path, "Authority ceilings require unique canonical lowercase provider/path capability identifiers in ordinal order.");
        }
    }

    private static void ValidateRunWideModelBudget(
        IReadOnlyList<GovernedLoopNodeDefinition?> nodes,
        EmbodySense.Core.Common.Inference.Profiles.Models.GovernedModelRoutingPolicy? defaultPolicy,
        string? graphId,
        GovernedLoopGraphErrorCollector errors)
    {
        if (!GovernedModelContractValidator.IsValid(defaultPolicy))
        {
            return;
        }

        var hashes = nodes
            .Where(node => node?.Descriptor?.Kind == GovernedLoopNodeKind.Inference
                && (node.ModelRoutingPolicy is null || GovernedModelContractValidator.IsValid(node.ModelRoutingPolicy)))
            .Select(node => (node!.ModelRoutingPolicy ?? defaultPolicy!).Requirements.Budget.PerRun.ContentHash)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (hashes.Length > 1)
        {
            errors.Add("graph.model-routing.run-budget.incompatible", GovernedLoopGraphElementKind.Graph, graphId, "graph.nodes", "Every Inference node must share one exact run-wide usage ceiling and currency.");
        }
    }

    private static void ValidatePorts(GovernedLoopNodeDefinition node, string nodePath, IReadOnlySet<string> schemaIds, GovernedLoopGraphErrorCollector errors)
    {
        if (node.Ports is null)
        {
            errors.Add("node.ports.required", GovernedLoopGraphElementKind.Node, node.Id, $"{nodePath}.ports", "The node port list is required.");
            return;
        }

        if (node.Ports.Count > CustomLoopLimits.MaxGraphPortsPerNode)
        {
            errors.Add("node.ports.count", GovernedLoopGraphElementKind.Node, node.Id, $"{nodePath}.ports", $"A node may declare at most {CustomLoopLimits.MaxGraphPortsPerNode} ports.");
        }

        var ports = node.Ports.Take(CustomLoopLimits.MaxGraphPortsPerNode).Cast<GovernedLoopPortDefinition?>().ToArray();
        ValidateIdentities(ports, value => value?.Id, $"nodes[{SafePathId(node.Id)}].ports", GovernedLoopGraphElementKind.Port, errors, node.Id);
        foreach (var (port, index) in ports.Select((value, index) => (value, index)))
        {
            if (port is null)
            {
                continue;
            }

            var path = $"{nodePath}.ports[{SafePathId(port.Id, index)}]";
            if (!Enum.IsDefined(port.Direction) || port.Direction == GovernedLoopPortDirection.Unknown)
            {
                errors.Add("port.direction.invalid", GovernedLoopGraphElementKind.Port, PortId(node.Id, port.Id), $"{path}.direction", "The port direction must be defined.");
            }

            if (!Enum.IsDefined(port.BindingKind) || port.BindingKind == GovernedLoopBindingKind.Unknown)
            {
                errors.Add("port.binding-kind.invalid", GovernedLoopGraphElementKind.Port, PortId(node.Id, port.Id), $"{path}.bindingKind", "The port binding kind must be defined.");
            }

            ValidateId(port.ValueSchemaId, "port.schema-id.invalid", GovernedLoopGraphElementKind.Port, PortId(node.Id, port.Id), $"{path}.valueSchemaId", errors);
            if (CustomLoopArtifactIdentifier.IsValid(port.ValueSchemaId) && !schemaIds.Contains(port.ValueSchemaId))
            {
                errors.Add("port.schema.missing", GovernedLoopGraphElementKind.Port, PortId(node.Id, port.Id), $"{path}.valueSchemaId", "The port value schema does not exist.");
            }
        }
    }

    private static void ValidateParameters(GovernedLoopNodeDefinition node, string path, GovernedLoopGraphErrorCollector errors)
    {
        if (node.Parameters is null)
        {
            errors.Add("node.parameters.required", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.parameters", "The node parameter map is required.");
            return;
        }

        if (node.Parameters.Count > CustomLoopLimits.MaxGraphDescriptorParameters)
        {
            errors.Add("node.parameters.count", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.parameters", $"A node may declare at most {CustomLoopLimits.MaxGraphDescriptorParameters} parameters.");
        }

        foreach (var parameter in node.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal).Take(CustomLoopLimits.MaxGraphDescriptorParameters))
        {
            ValidateId(parameter.Key, "node.parameter-id.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.parameters[{SafePathId(parameter.Key)}]", errors);
            ValidateText(parameter.Value, false, CustomLoopLimits.MaxGraphParameterValueCharacters, "node.parameter-value.invalid", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.parameters[{SafePathId(parameter.Key)}]", errors);
            if (GovernedLoopGraphRules.IsReservedModelRoutingParameter(parameter.Key))
            {
                errors.Add("node.parameter.model-routing-forbidden", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.parameters[{SafePathId(parameter.Key)}]", "Model routing must use the typed policy field, never generic parameters.");
            }
        }
    }

    private static void ValidateEdges(IReadOnlyList<GovernedLoopControlEdgeDefinition?> edges, IReadOnlyList<GovernedLoopNodeDefinition?> nodes, GovernedLoopGraphErrorCollector errors)
    {
        ValidateIdentities(edges, value => value?.Id, "controlEdges", GovernedLoopGraphElementKind.ControlEdge, errors);
        var nodeIds = ValidNodeIds(nodes);
        foreach (var (edge, index) in edges.Select((value, index) => (value, index)))
        {
            if (edge is null)
            {
                continue;
            }

            var path = ElementPath("controlEdges", edge.Id, index);
            ValidateReference(edge.FromNodeId, nodeIds, "edge.source", GovernedLoopGraphElementKind.ControlEdge, edge.Id, $"{path}.fromNodeId", errors);
            ValidateReference(edge.ToNodeId, nodeIds, "edge.target", GovernedLoopGraphElementKind.ControlEdge, edge.Id, $"{path}.toNodeId", errors);
            if (!Enum.IsDefined(edge.Condition) || edge.Condition == GovernedLoopControlCondition.Unknown)
            {
                errors.Add("edge.condition.invalid", GovernedLoopGraphElementKind.ControlEdge, edge.Id, $"{path}.condition", "The control condition must be defined.");
            }
        }
    }

    private static void ValidateBindings(IReadOnlyList<GovernedLoopBindingDefinition?> bindings, IReadOnlyList<GovernedLoopNodeDefinition?> nodes, IReadOnlyList<GovernedLoopControlEdgeDefinition?> edges, string? entryNodeId, GovernedLoopGraphErrorCollector errors)
    {
        ValidateIdentities(bindings, value => value?.Id, "bindings", GovernedLoopGraphElementKind.Binding, errors);
        var nodeById = ValidNodesById(nodes);
        var ports = nodeById.Values.SelectMany(node => (node.Ports ?? []).Where(port => port is not null && CustomLoopArtifactIdentifier.IsValid(port.Id)).Select(port => (NodeId: node.Id, Port: port))).GroupBy(item => (item.NodeId, item.Port.Id)).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Port);
        var boundInputs = new Dictionary<(string NodeId, string PortId), string>();
        var adjacency = BuildAdjacency(nodeById.Keys, edges);
        foreach (var (binding, index) in bindings.Select((value, index) => (value, index)))
        {
            if (binding is null)
            {
                continue;
            }

            var path = ElementPath("bindings", binding.Id, index);
            ValidateId(binding.FromNodeId, "binding.source-node.invalid", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.fromNodeId", errors);
            ValidateId(binding.FromPortId, "binding.source-port.invalid", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.fromPortId", errors);
            ValidateId(binding.ToNodeId, "binding.target-node.invalid", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.toNodeId", errors);
            ValidateId(binding.ToPortId, "binding.target-port.invalid", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.toPortId", errors);
            if (!Enum.IsDefined(binding.Kind) || binding.Kind == GovernedLoopBindingKind.Unknown)
            {
                errors.Add("binding.kind.invalid", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.kind", "The binding kind must be data or context.");
            }

            var hasSource = ports.TryGetValue((binding.FromNodeId, binding.FromPortId), out var source);
            var hasTarget = ports.TryGetValue((binding.ToNodeId, binding.ToPortId), out var target);
            if (!hasSource)
            {
                errors.Add("binding.source-port.missing", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.fromPortId", "The binding source port does not exist uniquely.");
            }

            if (!hasTarget)
            {
                errors.Add("binding.target-port.missing", GovernedLoopGraphElementKind.Binding, binding.Id, $"{path}.toPortId", "The binding target port does not exist uniquely.");
            }

            if (hasSource && hasTarget && (source!.Direction != GovernedLoopPortDirection.Output || target!.Direction != GovernedLoopPortDirection.Input || source.BindingKind != binding.Kind || target.BindingKind != binding.Kind || !string.Equals(source.ValueSchemaId, target.ValueSchemaId, StringComparison.Ordinal)))
            {
                errors.Add("binding.incompatible", GovernedLoopGraphElementKind.Binding, binding.Id, path, "The binding source and target have incompatible direction, channel, or value schema.");
            }

            if (hasTarget && !boundInputs.TryAdd((binding.ToNodeId, binding.ToPortId), binding.Id))
            {
                errors.Add("binding.input.conflict", GovernedLoopGraphElementKind.Binding, binding.Id, path, "An input port may have only one explicit binding.");
            }

            if (string.Equals(binding.FromNodeId, binding.ToNodeId, StringComparison.Ordinal))
            {
                errors.Add("binding.self-reference.unsupported", GovernedLoopGraphElementKind.Binding, binding.Id, path, "Schema 1 has no loop-carried initialization contract, so a node output cannot bind its own input.");
            }
            else if (nodeById.ContainsKey(binding.FromNodeId) && nodeById.ContainsKey(binding.ToNodeId) && !CanReach(binding.FromNodeId, binding.ToNodeId, adjacency))
            {
                errors.Add("binding.source.not-control-predecessor", GovernedLoopGraphElementKind.Binding, binding.Id, path, "A bound producer must be able to precede its consumer through control flow; values are never ambient.");
            }
            else if (hasTarget && target!.Required && entryNodeId is not null && adjacency.ContainsKey(entryNodeId) && !Dominates(entryNodeId, binding.FromNodeId, binding.ToNodeId, adjacency))
            {
                errors.Add("binding.source.not-control-dominator", GovernedLoopGraphElementKind.Binding, binding.Id, path, "A required input producer must execute on every control path from the graph entry to its consumer.");
            }
        }

        foreach (var node in nodeById.Values.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            foreach (var port in (node.Ports ?? []).Where(port => port is not null && port.Direction == GovernedLoopPortDirection.Input && port.Required).OrderBy(port => port.Id, StringComparer.Ordinal))
            {
                if (!boundInputs.ContainsKey((node.Id, port.Id)))
                {
                    errors.Add("binding.input.required", GovernedLoopGraphElementKind.Port, PortId(node.Id, port.Id), $"graph.nodes[{SafePathId(node.Id)}].ports[{SafePathId(port.Id)}]", "A required input must have exactly one explicit binding; predecessor output is never ambient.");
                }
            }
        }
    }

    private static void ValidateTerminalsAndTopology(string? entryNodeId, IReadOnlyList<string?> terminals, IReadOnlyList<GovernedLoopNodeDefinition?> nodes, IReadOnlyList<GovernedLoopControlEdgeDefinition?> edges, GovernedLoopGraphErrorCollector errors)
    {
        ValidateIdentities(terminals, value => value, "terminalNodeIds", GovernedLoopGraphElementKind.Graph, errors);
        var nodeById = ValidNodesById(nodes);
        if (!CustomLoopArtifactIdentifier.IsValid(entryNodeId) || !nodeById.TryGetValue(entryNodeId!, out var entry))
        {
            errors.Add("graph.entry.missing", GovernedLoopGraphElementKind.Graph, entryNodeId, "graph.entryNodeId", "The entry must reference one declared node.");
        }
        else if (entry.Descriptor?.Kind != GovernedLoopNodeKind.Trigger)
        {
            errors.Add("graph.entry.not-trigger", GovernedLoopGraphElementKind.Node, entry.Id, $"graph.nodes[{SafePathId(entry.Id)}]", "The graph entry must declare the trigger kind.");
        }

        var triggerIds = nodeById.Values.Where(node => node.Descriptor?.Kind == GovernedLoopNodeKind.Trigger).Select(node => node.Id).Order(StringComparer.Ordinal).ToArray();
        if (triggerIds.Length != 1)
        {
            errors.Add("graph.entry.trigger-count", GovernedLoopGraphElementKind.Graph, entryNodeId, "graph.entryNodeId", "A graph must declare exactly one trigger node.");
        }

        var terminalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (terminal, index) in terminals.Select((value, index) => (value, index)))
        {
            ValidateReference(terminal, nodeById.Keys, "graph.terminal", GovernedLoopGraphElementKind.Graph, terminal, $"graph.terminalNodeIds[{SafePathId(terminal, index)}]", errors);
            if (terminal is not null)
            {
                terminalIds.Add(terminal);
            }
        }

        var declaredTerminalKinds = nodeById.Values.Where(node => node.Descriptor?.Kind is GovernedLoopNodeKind.Exit or GovernedLoopNodeKind.Fail).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in terminalIds.Where(id => !declaredTerminalKinds.Contains(id)).Order(StringComparer.Ordinal))
        {
            errors.Add("graph.terminal.kind", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", "A terminal identity must reference an exit or fail node.");
        }

        foreach (var id in declaredTerminalKinds.Where(id => !terminalIds.Contains(id)).Order(StringComparer.Ordinal))
        {
            errors.Add("graph.terminal.undeclared", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", "Every exit or fail node must be declared as a terminal.");
        }

        var validEdges = edges.Where(edge => edge is not null && nodeById.ContainsKey(edge.FromNodeId) && nodeById.ContainsKey(edge.ToNodeId)).Cast<GovernedLoopControlEdgeDefinition>().ToArray();
        var adjacency = BuildAdjacency(nodeById.Keys, validEdges);
        var reverse = BuildReverseAdjacency(nodeById.Keys, validEdges);
        if (entryNodeId is not null && nodeById.ContainsKey(entryNodeId))
        {
            var reachable = Traverse(entryNodeId, adjacency);
            foreach (var id in nodeById.Keys.Where(id => !reachable.Contains(id)).Order(StringComparer.Ordinal))
            {
                errors.Add("graph.node.unreachable", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", "The node is not control-reachable from the entry.");
            }
        }

        var canReachTerminal = new HashSet<string>(StringComparer.Ordinal);
        foreach (var terminal in terminalIds.Where(nodeById.ContainsKey).Order(StringComparer.Ordinal))
        {
            canReachTerminal.UnionWith(Traverse(terminal, reverse));
        }

        foreach (var id in nodeById.Keys.Where(id => !canReachTerminal.Contains(id)).Order(StringComparer.Ordinal))
        {
            errors.Add("graph.node.no-terminal-path", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", "Every node must have a control path to a declared terminal.");
        }

        foreach (var id in nodeById.Keys.Order(StringComparer.Ordinal))
        {
            var outgoing = adjacency[id];
            if (outgoing.Count > CustomLoopLimits.MaxGraphControlFanOut)
            {
                errors.Add("graph.node.fan-out", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", $"Control fan-out cannot exceed {CustomLoopLimits.MaxGraphControlFanOut}.");
            }

            if (terminalIds.Contains(id) && outgoing.Count > 0)
            {
                errors.Add("graph.terminal.outgoing-control", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", "A terminal node cannot have outgoing control edges.");
            }
            else if (!terminalIds.Contains(id) && outgoing.Count == 0)
            {
                errors.Add("graph.node.dead-end", GovernedLoopGraphElementKind.Node, id, $"graph.nodes[{SafePathId(id)}]", "A non-terminal node must have outgoing control flow.");
            }
        }

        if (entryNodeId is not null && reverse.TryGetValue(entryNodeId, out var incoming) && incoming.Count > 0)
        {
            errors.Add("graph.entry.incoming-control", GovernedLoopGraphElementKind.Node, entryNodeId, $"graph.nodes[{SafePathId(entryNodeId)}]", "The trigger entry cannot have incoming control edges.");
        }

        ValidateCondensedDepth(nodeById.Keys, adjacency, errors);
    }

    private static void ValidateOutput(GovernedLoopOutputContract? output, IReadOnlyList<GovernedLoopNodeDefinition?> nodes, IReadOnlyList<GovernedLoopValueSchemaDefinition?> schemas, IReadOnlyList<string?> terminals, GovernedLoopGraphErrorCollector errors)
    {
        if (output is null)
        {
            errors.Add("graph.output-contract.required", GovernedLoopGraphElementKind.Graph, null, "graph.outputContract", "An output contract is required.");
            return;
        }

        ValidateText(output.Summary, true, CustomLoopLimits.MaxDescriptionCharacters, "output.summary.invalid", GovernedLoopGraphElementKind.Graph, null, "graph.outputContract.summary", errors);
        if (output.Outputs is null)
        {
            errors.Add("output.items.required", GovernedLoopGraphElementKind.Output, null, "graph.outputContract.outputs", "The output declaration list is required.");
            return;
        }

        if (output.Outputs.Count > CustomLoopLimits.MaxGraphOutputs)
        {
            errors.Add("output.items.count", GovernedLoopGraphElementKind.Output, null, "graph.outputContract.outputs", $"At most {CustomLoopLimits.MaxGraphOutputs} outputs may be declared.");
        }

        var outputs = output.Outputs.Take(CustomLoopLimits.MaxGraphOutputs).Cast<GovernedLoopOutputDefinition?>().ToArray();
        ValidateIdentities(outputs, value => value?.Id, "outputContract.outputs", GovernedLoopGraphElementKind.Output, errors);
        var schemaIds = schemas.Where(schema => schema is not null).Select(schema => schema!.Id).ToHashSet(StringComparer.Ordinal);
        var nodeById = ValidNodesById(nodes);
        var terminalIds = terminals.Where(value => value is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var (item, index) in outputs.Select((value, index) => (value, index)))
        {
            if (item is null)
            {
                continue;
            }

            var path = ElementPath("outputContract.outputs", item.Id, index);
            ValidateReference(item.ValueSchemaId, schemaIds, "output.schema", GovernedLoopGraphElementKind.Output, item.Id, $"{path}.valueSchemaId", errors);
            ValidateReference(item.SourceNodeId, nodeById.Keys, "output.source-node", GovernedLoopGraphElementKind.Output, item.Id, $"{path}.sourceNodeId", errors);
            if (nodeById.TryGetValue(item.SourceNodeId, out var node))
            {
                var port = (node.Ports ?? []).FirstOrDefault(value => value is not null && string.Equals(value.Id, item.SourcePortId, StringComparison.Ordinal));
                if (port is null || port.Direction != GovernedLoopPortDirection.Output || !string.Equals(port.ValueSchemaId, item.ValueSchemaId, StringComparison.Ordinal))
                {
                    errors.Add("output.source-port.incompatible", GovernedLoopGraphElementKind.Output, item.Id, $"{path}.sourcePortId", "The output source port is missing or incompatible.");
                }

                if (!terminalIds.Contains(item.SourceNodeId) || node.Descriptor?.Kind != GovernedLoopNodeKind.Exit)
                {
                    errors.Add("output.source-node.not-success-terminal", GovernedLoopGraphElementKind.Output, item.Id, $"{path}.sourceNodeId", "A successful graph output must be sourced from a declared exit terminal.");
                }
            }
        }
    }

    private static void ValidateDisplay(GovernedLoopDisplayMetadata? display, IReadOnlyList<GovernedLoopNodeDefinition?> nodes, GovernedLoopGraphErrorCollector errors)
    {
        if (display is null)
        {
            errors.Add("graph.display.required", GovernedLoopGraphElementKind.Graph, null, "graph.displayMetadata", "Display metadata is required by the canonical value contract but has no validation semantics beyond local shape.");
            return;
        }

        ValidateText(display.DisplayName, true, CustomLoopLimits.MaxNameCharacters, "display.name.invalid", GovernedLoopGraphElementKind.Graph, null, "graph.displayMetadata.displayName", errors);
        ValidateText(display.Description, false, CustomLoopLimits.MaxDescriptionCharacters, "display.description.invalid", GovernedLoopGraphElementKind.Graph, null, "graph.displayMetadata.description", errors);
        if (display.Nodes is null)
        {
            errors.Add("display.nodes.required", GovernedLoopGraphElementKind.Graph, null, "graph.displayMetadata.nodes", "The display node list is required.");
            return;
        }

        if (display.Nodes.Count > CustomLoopLimits.MaxGraphNodes)
        {
            errors.Add("display.nodes.count", GovernedLoopGraphElementKind.Graph, null, "graph.displayMetadata.nodes", $"At most {CustomLoopLimits.MaxGraphNodes} display nodes may be declared.");
        }

        var nodeIds = ValidNodeIds(nodes);
        var displayNodes = display.Nodes.Take(CustomLoopLimits.MaxGraphNodes).Cast<GovernedLoopNodeDisplayMetadata?>().ToArray();
        ValidateIdentities(displayNodes, value => value?.NodeId, "displayMetadata.nodes", GovernedLoopGraphElementKind.Node, errors);
        foreach (var (item, index) in displayNodes.Select((value, index) => (value, index)))
        {
            if (item is null)
            {
                continue;
            }

            var path = ElementPath("displayMetadata.nodes", item.NodeId, index);
            if (!nodeIds.Contains(item.NodeId))
            {
                errors.Add("display.node.missing", GovernedLoopGraphElementKind.Node, item.NodeId, path, "Display metadata references a missing node.");
            }

            ValidateText(item.DisplayName, true, CustomLoopLimits.MaxNameCharacters, "display.node-name.invalid", GovernedLoopGraphElementKind.Node, item.NodeId, $"{path}.displayName", errors);
            ValidateText(item.Description, false, CustomLoopLimits.MaxDescriptionCharacters, "display.node-description.invalid", GovernedLoopGraphElementKind.Node, item.NodeId, $"{path}.description", errors);
            if (item.CanvasX is < -CustomLoopLimits.MaxGraphCanvasCoordinate or > CustomLoopLimits.MaxGraphCanvasCoordinate || item.CanvasY is < -CustomLoopLimits.MaxGraphCanvasCoordinate or > CustomLoopLimits.MaxGraphCanvasCoordinate)
            {
                errors.Add("display.coordinates.invalid", GovernedLoopGraphElementKind.Node, item.NodeId, path, "Display coordinates exceed the bounded canonical value contract.");
            }
        }
    }

    private static void ValidateCondensedDepth(IEnumerable<string> nodeIds, IReadOnlyDictionary<string, SortedSet<string>> adjacency, GovernedLoopGraphErrorCollector errors)
    {
        var components = StronglyConnectedComponents(nodeIds, adjacency);
        var componentByNode = components.SelectMany((component, index) => component.Select(node => (node, index))).ToDictionary(item => item.node, item => item.index, StringComparer.Ordinal);
        var componentEdges = components.Select(_ => new SortedSet<int>()).ToArray();
        foreach (var source in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var target in adjacency[source])
            {
                var from = componentByNode[source];
                var to = componentByNode[target];
                if (from != to)
                {
                    componentEdges[from].Add(to);
                }
            }
        }

        var memo = new int[components.Count];
        int Depth(int component)
        {
            if (memo[component] != 0)
            {
                return memo[component];
            }

            memo[component] = 1 + (componentEdges[component].Count == 0 ? 0 : componentEdges[component].Max(Depth));
            return memo[component];
        }

        var depth = components.Count == 0 ? 0 : Enumerable.Range(0, components.Count).Max(Depth);
        if (depth > CustomLoopLimits.MaxGraphControlDepth)
        {
            errors.Add("graph.control-depth", GovernedLoopGraphElementKind.Graph, null, "graph.controlEdges", $"Condensed control-flow depth cannot exceed {CustomLoopLimits.MaxGraphControlDepth}.");
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> StronglyConnectedComponents(IEnumerable<string> nodeIds, IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<IReadOnlyList<string>>();

        void Visit(string node)
        {
            indexes[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var target in adjacency[node])
            {
                if (!indexes.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indexes[target]);
                }
            }

            if (lowLinks[node] != indexes[node])
            {
                return;
            }

            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            }
            while (!string.Equals(current, node, StringComparison.Ordinal));
            components.Add(component.Order(StringComparer.Ordinal).ToArray());
        }

        foreach (var node in nodeIds.Order(StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(node))
            {
                Visit(node);
            }
        }

        return components.OrderBy(component => component[0], StringComparer.Ordinal).ToArray();
    }

    private static Dictionary<string, SortedSet<string>> BuildAdjacency(IEnumerable<string> nodeIds, IEnumerable<GovernedLoopControlEdgeDefinition?> edges)
    {
        var result = nodeIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToDictionary(id => id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in edges.Where(edge => edge is not null).Cast<GovernedLoopControlEdgeDefinition>().OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            if (result.TryGetValue(edge.FromNodeId, out var targets) && result.ContainsKey(edge.ToNodeId))
            {
                targets.Add(edge.ToNodeId);
            }
        }

        return result;
    }

    private static Dictionary<string, SortedSet<string>> BuildReverseAdjacency(IEnumerable<string> nodeIds, IEnumerable<GovernedLoopControlEdgeDefinition?> edges)
    {
        var result = nodeIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToDictionary(id => id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in edges.Where(edge => edge is not null).Cast<GovernedLoopControlEdgeDefinition>().OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            if (result.TryGetValue(edge.ToNodeId, out var sources) && result.ContainsKey(edge.FromNodeId))
            {
                sources.Add(edge.FromNodeId);
            }
        }

        return result;
    }

    private static HashSet<string> Traverse(string start, IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current) || !adjacency.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets.Reverse())
            {
                pending.Push(target);
            }
        }

        return visited;
    }

    private static bool CanReach(string source, string target, IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        return !string.Equals(source, target, StringComparison.Ordinal) && Traverse(source, adjacency).Contains(target);
    }

    private static bool Dominates(string entry, string producer, string consumer, IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        if (string.Equals(entry, producer, StringComparison.Ordinal))
        {
            return true;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(entry);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (string.Equals(current, producer, StringComparison.Ordinal) || !visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, consumer, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var target in adjacency[current].Reverse())
            {
                pending.Push(target);
            }
        }

        return true;
    }

    private static void ValidateIdentities<T>(IReadOnlyList<T?> values, Func<T, string?> getId, string collectionPath, GovernedLoopGraphElementKind kind, GovernedLoopGraphErrorCollector errors, string? ownerId = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (value, index) in values.Select((value, index) => (value, index)))
        {
            if (value is null)
            {
                continue;
            }

            var id = getId(value);
            var attributedId = ownerId is null ? id : PortId(ownerId, id);
            if (!CustomLoopArtifactIdentifier.IsValid(id))
            {
                errors.Add("element.id.invalid", kind, attributedId, $"graph.{collectionPath}[{SafePathId(id, index)}].id", "A stable filename-safe lowercase identity is required.");
            }
            else if (!seen.Add(id!) && duplicates.Add(id!))
            {
                errors.Add("element.id.duplicate", kind, attributedId, $"graph.{collectionPath}[{SafePathId(id)}].id", "Element identities must be unique within their scope.");
            }
        }
    }

    private static void ValidateReference(string? value, IEnumerable<string> validIds, string codePrefix, GovernedLoopGraphElementKind kind, string? id, string path, GovernedLoopGraphErrorCollector errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value))
        {
            errors.Add($"{codePrefix}.invalid", kind, id, path, "The reference identity is required and must be canonical.");
        }
        else if (!validIds.Contains(value!, StringComparer.Ordinal))
        {
            errors.Add($"{codePrefix}.missing", kind, id, path, "The referenced element does not exist uniquely.");
        }
    }

    private static void ValidateId(string? value, string code, GovernedLoopGraphElementKind kind, string? id, string path, GovernedLoopGraphErrorCollector errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value))
        {
            errors.Add(code, kind, id, path, "A stable filename-safe lowercase identity is required.");
        }
    }

    private static void ValidateText(string? value, bool required, int maximum, string code, GovernedLoopGraphElementKind kind, string? id, string path, GovernedLoopGraphErrorCollector errors)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            errors.Add(code, kind, id, path, "A canonical text value is required.");
            return;
        }

        if (value.Length > maximum || value.Contains('\r', StringComparison.Ordinal) || value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) || !HasWellFormedUnicode(value))
        {
            errors.Add(code, kind, id, path, "Text must be bounded, well-formed Unicode with LF line endings and no boundary whitespace.");
            return;
        }

        try
        {
            if (!value.IsNormalized(NormalizationForm.FormC) || value.EnumerateRunes().Any(IsUnsafeRune))
            {
                errors.Add(code, kind, id, path, "Text must be NFC-normalized and contain no unsafe Unicode categories.");
            }
        }
        catch (ArgumentException)
        {
            errors.Add(code, kind, id, path, "Text contains malformed Unicode.");
        }
    }

    private static bool HasWellFormedUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static bool IsUnsafeRune(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control && rune.Value is not '\n' and not '\t' || category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate;
    }

    private static Dictionary<string, GovernedLoopNodeDefinition> ValidNodesById(IEnumerable<GovernedLoopNodeDefinition?> nodes)
    {
        return nodes.Where(node => node is not null && CustomLoopArtifactIdentifier.IsValid(node.Id)).Cast<GovernedLoopNodeDefinition>().GroupBy(node => node.Id, StringComparer.Ordinal).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    private static HashSet<string> ValidNodeIds(IEnumerable<GovernedLoopNodeDefinition?> nodes)
    {
        return ValidNodesById(nodes).Keys.ToHashSet(StringComparer.Ordinal);
    }

    private static string ElementPath(string collection, string? id, int index)
    {
        return $"graph.{collection}[{SafePathId(id, index)}]";
    }

    private static string SafePathId(string? id, int index = 0)
    {
        return CustomLoopArtifactIdentifier.IsValid(id) ? id! : index.ToString("D4", CultureInfo.InvariantCulture);
    }

    private static string? PortId(string? nodeId, string? portId)
    {
        return nodeId is null || portId is null ? null : $"{nodeId}.{portId}";
    }

    private static string ToCode(string value)
    {
        return string.Concat(value.Select(character => char.IsUpper(character) ? $"-{char.ToLowerInvariant(character)}" : character.ToString(CultureInfo.InvariantCulture)));
    }
}
