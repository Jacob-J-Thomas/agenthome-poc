using System.Collections.ObjectModel;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Loops.Custom.Graph;

/// <summary>Defines the dependency-light schema-1 canonical value contract for one governed custom-loop graph.</summary>
/// <remarks>Construction enforces local canonical value invariants only. Graph-wide topology and executability validation remain downstream concerns; this contract owns no persistence, revision lifecycle, traversal, dispatch, provider, UI, or compatibility behavior.</remarks>
public sealed class GovernedLoopGraphDefinition
{
    private GovernedLoopGraphDefinition(
        string graphId,
        string revisionId,
        string purpose,
        ContextualRoleRevisionPin owningRole,
        string entryNodeId,
        string[] terminalNodeIds,
        GovernedLoopAuthorityCeiling authorityCeiling,
        GovernedLoopValueSchemaDefinition[] valueSchemas,
        GovernedLoopNodeDefinition[] nodes,
        GovernedLoopControlEdgeDefinition[] controlEdges,
        GovernedLoopBindingDefinition[] bindings,
        GovernedLoopOutputContract outputContract,
        GovernedLoopDisplayMetadata displayMetadata,
        GovernedModelRoutingPolicy defaultModelRoutingPolicy)
    {
        GraphId = graphId;
        RevisionId = revisionId;
        Purpose = purpose;
        OwningRole = owningRole;
        EntryNodeId = entryNodeId;
        TerminalNodeIds = Array.AsReadOnly(terminalNodeIds);
        AuthorityCeiling = authorityCeiling;
        ValueSchemas = Array.AsReadOnly(valueSchemas);
        Nodes = Array.AsReadOnly(nodes);
        ControlEdges = Array.AsReadOnly(controlEdges);
        Bindings = Array.AsReadOnly(bindings);
        OutputContract = outputContract;
        DisplayMetadata = displayMetadata;
        DefaultModelRoutingPolicy = defaultModelRoutingPolicy;
        ExecutableHash = GovernedLoopExecutableHash.Compute(this);
        RevisionReference = GovernedLoopRevisionReference.Create(GovernedLoopRevisionReference.CurrentSchemaVersion, GraphId, RevisionId, ExecutableHash);
    }

    /// <summary>Schema version required by the canonical governed custom-loop graph contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>Gets the stable graph identifier.</summary>
    /// <value>The canonical graph identifier.</value>
    public string GraphId { get; }
    /// <summary>Gets the immutable revision identifier without owning its lifecycle.</summary>
    /// <value>The canonical revision identifier.</value>
    public string RevisionId { get; }
    /// <summary>Gets the bounded canonical loop purpose.</summary>
    /// <value>The purpose available to governance and execution consumers.</value>
    public string Purpose { get; }
    /// <summary>Gets the exact immutable contextual-role revision that owns the loop.</summary>
    /// <value>The stable role identity, positive revision, and canonical semantic content hash.</value>
    public ContextualRoleRevisionPin OwningRole { get; }
    /// <summary>Gets the explicit control-flow entry node.</summary>
    /// <value>The trigger node identifier.</value>
    public string EntryNodeId { get; }
    /// <summary>Gets the canonical successful and failed terminal node identities.</summary>
    /// <value>The immutable ordinal terminal set.</value>
    public IReadOnlyList<string> TerminalNodeIds { get; }
    /// <summary>Gets the non-granting maximum loop authority.</summary>
    /// <value>The loop authority ceiling.</value>
    public GovernedLoopAuthorityCeiling AuthorityCeiling { get; }
    /// <summary>Gets the canonical value schema declarations.</summary>
    /// <value>The immutable schemas ordered by identifier.</value>
    public IReadOnlyList<GovernedLoopValueSchemaDefinition> ValueSchemas { get; }
    /// <summary>Gets the canonical node declarations.</summary>
    /// <value>The immutable nodes ordered by identifier.</value>
    public IReadOnlyList<GovernedLoopNodeDefinition> Nodes { get; }
    /// <summary>Gets control flow, which is intentionally separate from value binding.</summary>
    /// <value>The immutable control edges ordered by identifier.</value>
    public IReadOnlyList<GovernedLoopControlEdgeDefinition> ControlEdges { get; }
    /// <summary>Gets explicit typed data and context bindings.</summary>
    /// <value>The immutable bindings ordered by identifier.</value>
    public IReadOnlyList<GovernedLoopBindingDefinition> Bindings { get; }
    /// <summary>Gets the declared successful output contract.</summary>
    /// <value>The immutable output contract.</value>
    public GovernedLoopOutputContract OutputContract { get; }
    /// <summary>Gets display and layout metadata excluded from executable identity.</summary>
    /// <value>The validated display-only metadata.</value>
    public GovernedLoopDisplayMetadata DisplayMetadata { get; }
    /// <summary>Gets the required typed loop-default model-routing policy.</summary>
    public GovernedModelRoutingPolicy DefaultModelRoutingPolicy { get; }
    /// <summary>Gets the lowercase SHA-256 digest of executable content.</summary>
    /// <value>A digest excluding graph, revision, display, and layout identity.</value>
    public string ExecutableHash { get; }
    /// <summary>Gets the stable graph revision hand-off reference.</summary>
    /// <value>The graph ID, revision ID, and executable digest.</value>
    public GovernedLoopRevisionReference RevisionReference { get; }

    /// <summary>Creates and deeply snapshots one canonical schema-1 governed custom-loop graph.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="graphId">The stable graph identifier.</param>
    /// <param name="revisionId">The stable immutable revision identifier.</param>
    /// <param name="purpose">The canonical loop purpose.</param>
    /// <param name="owningRole">The exact immutable owning-role revision.</param>
    /// <param name="entryNodeId">The trigger entry node identifier.</param>
    /// <param name="terminalNodeIds">The exit and fail terminal node identities.</param>
    /// <param name="authorityCeiling">The non-granting maximum loop authority.</param>
    /// <param name="valueSchemas">The typed value schemas.</param>
    /// <param name="nodes">The node declarations.</param>
    /// <param name="controlEdges">The control-flow edges.</param>
    /// <param name="bindings">The explicit data and context bindings.</param>
    /// <param name="outputContract">The successful output contract.</param>
    /// <param name="displayMetadata">The display-only metadata.</param>
    /// <param name="defaultModelRoutingPolicy">The required typed loop-default model-routing policy.</param>
    /// <returns>A validated, canonical, immutable graph definition.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required collection or value object is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when any bounded schema, identity, reference, authority, binding, output, Unicode, or display invariant fails.</exception>
    public static GovernedLoopGraphDefinition Create(
        int schemaVersion,
        string graphId,
        string revisionId,
        string purpose,
        ContextualRoleRevisionPin owningRole,
        string entryNodeId,
        IEnumerable<string> terminalNodeIds,
        GovernedLoopAuthorityCeiling authorityCeiling,
        IEnumerable<GovernedLoopValueSchemaDefinition> valueSchemas,
        IEnumerable<GovernedLoopNodeDefinition> nodes,
        IEnumerable<GovernedLoopControlEdgeDefinition> controlEdges,
        IEnumerable<GovernedLoopBindingDefinition> bindings,
        GovernedLoopOutputContract outputContract,
        GovernedLoopDisplayMetadata displayMetadata,
        GovernedModelRoutingPolicy defaultModelRoutingPolicy)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException($"Schema version must be {CurrentSchemaVersion}; compatibility translation is not supported.", nameof(schemaVersion));
        }

        GovernedLoopGraphRules.RequireId(graphId, nameof(graphId));
        GovernedLoopGraphRules.RequireId(revisionId, nameof(revisionId));
        RequireOwningRole(owningRole);
        GovernedLoopGraphRules.RequireId(entryNodeId, nameof(entryNodeId));
        GovernedLoopGraphRules.RequireText(purpose, nameof(purpose), CustomLoopLimits.MaxDescriptionCharacters, required: true);
        ArgumentNullException.ThrowIfNull(authorityCeiling);
        if (!GovernedModelContractValidator.IsValid(defaultModelRoutingPolicy))
        {
            throw new ArgumentException("A complete typed loop-default model-routing policy is required.", nameof(defaultModelRoutingPolicy));
        }
        var canonicalSchemas = ValidateSchemas(valueSchemas);
        var canonicalNodes = ValidateNodes(nodes, authorityCeiling, canonicalSchemas, defaultModelRoutingPolicy);
        if (canonicalNodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Inference)
            .Select(node => (node.ModelRoutingPolicy ?? defaultModelRoutingPolicy).Requirements.Budget.PerRun.ContentHash)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1)
        {
            throw new ArgumentException("Every Inference node in one graph must share one exact run-wide usage ceiling and currency.", nameof(nodes));
        }
        var nodeById = canonicalNodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!nodeById.ContainsKey(entryNodeId))
        {
            throw new ArgumentException("The graph entry must reference a declared node.", nameof(entryNodeId));
        }

        var canonicalTerminals = ValidateTerminals(terminalNodeIds, nodeById);
        var canonicalEdges = ValidateControlEdges(controlEdges, nodeById);
        var canonicalBindings = ValidateBindings(bindings, canonicalNodes);
        var canonicalOutput = ValidateOutput(outputContract, canonicalNodes, canonicalSchemas);
        var canonicalDisplay = ValidateDisplay(displayMetadata, nodeById);
        var canonicalOwningRole = new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(owningRole.Identity.RoleId, owningRole.Identity.Revision),
            owningRole.ContentHash);
        return new GovernedLoopGraphDefinition(graphId, revisionId, purpose, canonicalOwningRole, entryNodeId, canonicalTerminals, authorityCeiling, canonicalSchemas, canonicalNodes, canonicalEdges, canonicalBindings, canonicalOutput, canonicalDisplay, defaultModelRoutingPolicy);
    }

    private static void RequireOwningRole(ContextualRoleRevisionPin owningRole)
    {
        ArgumentNullException.ThrowIfNull(owningRole);
        ArgumentNullException.ThrowIfNull(owningRole.Identity);
        if (!ContextualRoleId.IsValid(owningRole.Identity.RoleId))
        {
            throw new ArgumentException("The owning role identifier must be canonical.", nameof(owningRole));
        }

        if (owningRole.Identity.Revision < 1)
        {
            throw new ArgumentException("The owning role revision must be positive.", nameof(owningRole));
        }

        if (owningRole.ContentHash is not { Length: ContextualRoleLimits.Sha256HexCharacters }
            || owningRole.ContentHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("The owning role content hash must be a canonical lowercase SHA-256 digest.", nameof(owningRole));
        }
    }

    private static GovernedLoopValueSchemaDefinition[] ValidateSchemas(IEnumerable<GovernedLoopValueSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var values = schemas.ToArray();
        RequireCount(values.Length, 1, CustomLoopLimits.MaxGraphValueSchemas, nameof(schemas));
        RequireDistinct(values, schema => schema.Id, nameof(schemas));
        var ids = values.Select(schema => schema.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var schema in values)
        {
            GovernedLoopGraphRules.RequireDefined(schema.Kind, nameof(schemas));
            if (schema.Format is not null)
            {
                GovernedLoopGraphRules.RequireId(schema.Format, nameof(schemas));
            }

            if (schema.Kind == GovernedLoopValueKind.Array)
            {
                GovernedLoopGraphRules.RequireId(schema.ElementSchemaId, nameof(schemas));
                if (!ids.Contains(schema.ElementSchemaId!))
                {
                    throw new ArgumentException($"Array schema `{schema.Id}` references missing element schema `{schema.ElementSchemaId}`.", nameof(schemas));
                }
            }
            else if (schema.ElementSchemaId is not null)
            {
                throw new ArgumentException($"Non-array schema `{schema.Id}` cannot declare an element schema.", nameof(schemas));
            }
        }

        return values.OrderBy(schema => schema.Id, StringComparer.Ordinal).ToArray();
    }

    private static GovernedLoopNodeDefinition[] ValidateNodes(IEnumerable<GovernedLoopNodeDefinition> nodes, GovernedLoopAuthorityCeiling loopCeiling, IReadOnlyList<GovernedLoopValueSchemaDefinition> schemas, GovernedModelRoutingPolicy defaultModelRoutingPolicy)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var values = nodes.ToArray();
        RequireCount(values.Length, 2, CustomLoopLimits.MaxGraphNodes, nameof(nodes));
        RequireDistinct(values, node => node.Id, nameof(nodes));
        var schemaIds = schemas.Select(schema => schema.Id).ToHashSet(StringComparer.Ordinal);
        var loopCapabilities = loopCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var canonical = new List<GovernedLoopNodeDefinition>(values.Length);
        foreach (var node in values)
        {
            if (node.Descriptor is null || node.AuthorityCeiling is null || node.Ports is null || node.Parameters is null)
            {
                throw new ArgumentException($"Node `{node.Id}` has a null descriptor, authority ceiling, port list, or parameter map.", nameof(nodes));
            }

            GovernedLoopGraphRules.RequireDefined(node.Descriptor.Kind, nameof(nodes));
            GovernedLoopGraphRules.RequireId(node.Descriptor.TypeId, nameof(nodes));
            if (node.Descriptor.Version < 1)
            {
                throw new ArgumentException($"Node `{node.Id}` descriptor version must be positive.", nameof(nodes));
            }

            if (node.AuthorityCeiling.CapabilityIds.Any(capability => !loopCapabilities.Contains(capability)))
            {
                throw new ArgumentException($"Node `{node.Id}` widens the loop authority ceiling.", nameof(nodes));
            }

            var ports = node.Ports.ToArray();
            RequireCount(ports.Length, 0, CustomLoopLimits.MaxGraphPortsPerNode, nameof(nodes));
            RequireDistinct(ports, port => port.Id, nameof(nodes));
            foreach (var port in ports)
            {
                GovernedLoopGraphRules.RequireDefined(port.Direction, nameof(nodes));
                GovernedLoopGraphRules.RequireDefined(port.BindingKind, nameof(nodes));
                GovernedLoopGraphRules.RequireId(port.ValueSchemaId, nameof(nodes));
                if (!schemaIds.Contains(port.ValueSchemaId))
                {
                    throw new ArgumentException($"Node `{node.Id}` port `{port.Id}` references missing value schema `{port.ValueSchemaId}`.", nameof(nodes));
                }
            }

            if (node.Parameters.Count > CustomLoopLimits.MaxGraphDescriptorParameters)
            {
                throw new ArgumentException($"Node `{node.Id}` has too many descriptor parameters.", nameof(nodes));
            }

            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in node.Parameters)
            {
                GovernedLoopGraphRules.RequireId(parameter.Key, nameof(nodes));
                if (GovernedLoopGraphRules.IsReservedModelRoutingParameter(parameter.Key))
                {
                    throw new ArgumentException($"Node `{node.Id}` encodes model routing in the generic parameter map.", nameof(nodes));
                }
                GovernedLoopGraphRules.RequireText(parameter.Value, nameof(nodes), CustomLoopLimits.MaxGraphParameterValueCharacters, required: false);
                if (!parameters.TryAdd(parameter.Key, parameter.Value))
                {
                    throw new ArgumentException($"Node `{node.Id}` contains duplicate parameter `{parameter.Key}`.", nameof(nodes));
                }
            }

            if (node.Descriptor.Kind != GovernedLoopNodeKind.Inference)
            {
                if (node.ModelRoutingPolicy is not null || node.AuthoredInputDataClasses is not null)
                {
                    throw new ArgumentException($"Only Inference node `{node.Id}` may declare model routing or authored input classification.", nameof(nodes));
                }
            }
            else
            {
                if (node.ModelRoutingPolicy is not null && !GovernedModelContractValidator.IsValid(node.ModelRoutingPolicy))
                {
                    throw new ArgumentException($"Inference node `{node.Id}` has an invalid routing override.", nameof(nodes));
                }
                var effectivePolicy = node.ModelRoutingPolicy ?? defaultModelRoutingPolicy;
                var profileIds = CandidateProfileIds(effectivePolicy);
                var nodeCapabilities = node.AuthorityCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
                if (profileIds.Any(id => !loopCapabilities.Contains(id) || !nodeCapabilities.Contains(id)))
                {
                    throw new ArgumentException($"Inference node `{node.Id}` routes a profile outside the loop or node authority ceiling.", nameof(nodes));
                }
                ValidateAuthoredInputDataClasses(node.AuthoredInputDataClasses, node.Id, nameof(nodes));
            }

            canonical.Add(node with
            {
                Ports = Array.AsReadOnly(ports.OrderBy(port => port.Id, StringComparer.Ordinal).ToArray()),
                Parameters = new ReadOnlyDictionary<string, string>(parameters)
            });
        }

        return canonical.OrderBy(node => node.Id, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<string> CandidateProfileIds(GovernedModelRoutingPolicy policy)
        => (policy.Selector.Kind == GovernedModelSelectorKind.Exact
                ? new[] { policy.Selector.ExactProfileId! }
                : policy.Selector.PermittedInheritedProfileIds)
            .Concat(policy.FallbackProfileIds)
            .Select(value => value.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void ValidateAuthoredInputDataClasses(IReadOnlyList<CapabilityDataClass>? values, string nodeId, string parameterName)
    {
        if (values is null)
        {
            return;
        }
        var canonical = values.Select(value => value?.Value).ToArray();
        if (canonical.Length > CapabilityContractLimits.MaxDataClasses
            || canonical.Any(value => !CapabilityDataClass.TryParse(value, out _, out _))
            || !canonical.SequenceEqual(canonical.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Length)
        {
            throw new ArgumentException($"Inference node `{nodeId}` has invalid authored input-data classes.", parameterName);
        }
    }

    private static string[] ValidateTerminals(IEnumerable<string> terminalNodeIds, IReadOnlyDictionary<string, GovernedLoopNodeDefinition> nodeById)
    {
        ArgumentNullException.ThrowIfNull(terminalNodeIds);
        var values = terminalNodeIds.ToArray();
        RequireCount(values.Length, 1, CustomLoopLimits.MaxGraphNodes - 1, nameof(terminalNodeIds));
        GovernedLoopGraphRules.RequireDistinctIds(values, nameof(terminalNodeIds));
        foreach (var terminalId in values)
        {
            if (!nodeById.ContainsKey(terminalId))
            {
                throw new ArgumentException($"Terminal `{terminalId}` must reference a declared node.", nameof(terminalNodeIds));
            }
        }

        return values.Order(StringComparer.Ordinal).ToArray();
    }

    private static GovernedLoopControlEdgeDefinition[] ValidateControlEdges(IEnumerable<GovernedLoopControlEdgeDefinition> edges, IReadOnlyDictionary<string, GovernedLoopNodeDefinition> nodeById)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var values = edges.ToArray();
        RequireCount(values.Length, 1, CustomLoopLimits.MaxGraphControlEdges, nameof(edges));
        RequireDistinct(values, edge => edge.Id, nameof(edges));
        foreach (var edge in values)
        {
            GovernedLoopGraphRules.RequireId(edge.FromNodeId, nameof(edges));
            GovernedLoopGraphRules.RequireId(edge.ToNodeId, nameof(edges));
            GovernedLoopGraphRules.RequireDefined(edge.Condition, nameof(edges));
            if (!nodeById.ContainsKey(edge.FromNodeId) || !nodeById.ContainsKey(edge.ToNodeId))
            {
                throw new ArgumentException($"Control edge `{edge.Id}` references a missing node.", nameof(edges));
            }
        }

        return values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
    }

    private static GovernedLoopBindingDefinition[] ValidateBindings(IEnumerable<GovernedLoopBindingDefinition> bindings, IReadOnlyList<GovernedLoopNodeDefinition> nodes)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var values = bindings.ToArray();
        RequireCount(values.Length, 0, CustomLoopLimits.MaxGraphBindings, nameof(bindings));
        RequireDistinct(values, binding => binding.Id, nameof(bindings));
        var ports = nodes.SelectMany(node => node.Ports.Select(port => (NodeId: node.Id, Port: port))).ToDictionary(item => (item.NodeId, item.Port.Id), item => item.Port);
        var boundInputs = new HashSet<(string NodeId, string PortId)>();
        foreach (var binding in values)
        {
            GovernedLoopGraphRules.RequireDefined(binding.Kind, nameof(bindings));
            GovernedLoopGraphRules.RequireId(binding.FromNodeId, nameof(bindings));
            GovernedLoopGraphRules.RequireId(binding.FromPortId, nameof(bindings));
            GovernedLoopGraphRules.RequireId(binding.ToNodeId, nameof(bindings));
            GovernedLoopGraphRules.RequireId(binding.ToPortId, nameof(bindings));
            if (!ports.TryGetValue((binding.FromNodeId, binding.FromPortId), out var source) || !ports.TryGetValue((binding.ToNodeId, binding.ToPortId), out var target))
            {
                throw new ArgumentException($"Binding `{binding.Id}` references a missing port.", nameof(bindings));
            }

            if (source.Direction != GovernedLoopPortDirection.Output || target.Direction != GovernedLoopPortDirection.Input || source.BindingKind != binding.Kind || target.BindingKind != binding.Kind || !string.Equals(source.ValueSchemaId, target.ValueSchemaId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Binding `{binding.Id}` has incompatible direction, channel, or value schema.", nameof(bindings));
            }

            if (!boundInputs.Add((binding.ToNodeId, binding.ToPortId)))
            {
                throw new ArgumentException($"Input port `{binding.ToNodeId}.{binding.ToPortId}` has more than one binding.", nameof(bindings));
            }
        }

        foreach (var input in ports.Where(item => item.Value.Direction == GovernedLoopPortDirection.Input && item.Value.Required))
        {
            if (!boundInputs.Contains((input.Key.Item1, input.Key.Item2)))
            {
                throw new ArgumentException($"Required input port `{input.Key.Item1}.{input.Key.Item2}` must have one explicit binding; predecessor output is never ambient.", nameof(bindings));
            }
        }

        return values.OrderBy(binding => binding.Id, StringComparer.Ordinal).ToArray();
    }

    private static GovernedLoopOutputContract ValidateOutput(GovernedLoopOutputContract contract, IReadOnlyList<GovernedLoopNodeDefinition> nodes, IReadOnlyList<GovernedLoopValueSchemaDefinition> schemas)
    {
        ArgumentNullException.ThrowIfNull(contract);
        GovernedLoopGraphRules.RequireText(contract.Summary, nameof(contract), CustomLoopLimits.MaxDescriptionCharacters, required: true);
        ArgumentNullException.ThrowIfNull(contract.Outputs);
        var outputs = contract.Outputs.ToArray();
        RequireCount(outputs.Length, 0, CustomLoopLimits.MaxGraphOutputs, nameof(contract));
        RequireDistinct(outputs, output => output.Id, nameof(contract));
        var schemaIds = schemas.Select(schema => schema.Id).ToHashSet(StringComparer.Ordinal);
        var ports = nodes.SelectMany(node => node.Ports.Select(port => (NodeId: node.Id, Port: port))).ToDictionary(item => (item.NodeId, item.Port.Id), item => item.Port);
        foreach (var output in outputs)
        {
            GovernedLoopGraphRules.RequireId(output.ValueSchemaId, nameof(contract));
            GovernedLoopGraphRules.RequireId(output.SourceNodeId, nameof(contract));
            GovernedLoopGraphRules.RequireId(output.SourcePortId, nameof(contract));
            if (!schemaIds.Contains(output.ValueSchemaId) || !ports.TryGetValue((output.SourceNodeId, output.SourcePortId), out var source) || source.Direction != GovernedLoopPortDirection.Output || !string.Equals(source.ValueSchemaId, output.ValueSchemaId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Output `{output.Id}` has a missing or incompatible source port or value schema.", nameof(contract));
            }
        }

        return contract with { Outputs = Array.AsReadOnly(outputs.OrderBy(output => output.Id, StringComparer.Ordinal).ToArray()) };
    }

    private static GovernedLoopDisplayMetadata ValidateDisplay(GovernedLoopDisplayMetadata display, IReadOnlyDictionary<string, GovernedLoopNodeDefinition> nodeById)
    {
        ArgumentNullException.ThrowIfNull(display);
        GovernedLoopGraphRules.RequireText(display.DisplayName, nameof(display), CustomLoopLimits.MaxNameCharacters, required: true);
        GovernedLoopGraphRules.RequireText(display.Description, nameof(display), CustomLoopLimits.MaxDescriptionCharacters, required: false);
        ArgumentNullException.ThrowIfNull(display.Nodes);
        var nodes = display.Nodes.ToArray();
        RequireCount(nodes.Length, 0, CustomLoopLimits.MaxGraphNodes, nameof(display));
        RequireDistinct(nodes, node => node.NodeId, nameof(display));
        foreach (var node in nodes)
        {
            if (!nodeById.ContainsKey(node.NodeId))
            {
                throw new ArgumentException($"Display metadata references missing node `{node.NodeId}`.", nameof(display));
            }

            GovernedLoopGraphRules.RequireText(node.DisplayName, nameof(display), CustomLoopLimits.MaxNameCharacters, required: true);
            GovernedLoopGraphRules.RequireText(node.Description, nameof(display), CustomLoopLimits.MaxDescriptionCharacters, required: false);
            if (node.CanvasX is < -CustomLoopLimits.MaxGraphCanvasCoordinate or > CustomLoopLimits.MaxGraphCanvasCoordinate || node.CanvasY is < -CustomLoopLimits.MaxGraphCanvasCoordinate or > CustomLoopLimits.MaxGraphCanvasCoordinate)
            {
                throw new ArgumentException($"Display coordinates for node `{node.NodeId}` exceed the bounded canvas.", nameof(display));
            }
        }

        return display with { Nodes = Array.AsReadOnly(nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray()) };
    }

    private static void RequireDistinct<T>(IReadOnlyList<T> values, Func<T, string> getId, string parameterName)
    {
        if (values.Any(value => value is null))
        {
            throw new ArgumentException("Collections cannot contain null values.", parameterName);
        }

        var ids = values.Select(getId).ToArray();
        GovernedLoopGraphRules.RequireDistinctIds(ids, parameterName);
    }

    private static void RequireCount(int count, int minimum, int maximum, string parameterName)
    {
        if (count < minimum || count > maximum)
        {
            throw new ArgumentException($"{parameterName} count must be between {minimum} and {maximum}.", parameterName);
        }
    }
}
