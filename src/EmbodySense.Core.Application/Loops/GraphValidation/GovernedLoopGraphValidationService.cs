using System.Globalization;
using System.Text;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Validates canonical governed graph candidates against exact current catalog semantics and non-widening role authority.</summary>
/// <remarks>The service performs no persistence, provider dispatch, graph traversal execution, frontier management, migration, or layout interpretation. A catalog entry must explicitly advertise executable support; mere availability never makes a node executable. Cyclic components are admitted only when every node has one effective internal successor edge because no runtime-enforceable SCC-wide activation contract exists.</remarks>
public sealed class GovernedLoopGraphValidationService
{
    private readonly IGovernedLoopNodeCatalog _catalog;
    private readonly IGovernedLoopAuthoritySnapshotProvider _authorityProvider;

    /// <summary>Initializes the deterministic graph validation service.</summary>
    /// <param name="catalog">The Application-owned exact descriptor catalog port.</param>
    /// <param name="authorityProvider">The current role-authority snapshot port.</param>
    public GovernedLoopGraphValidationService(IGovernedLoopNodeCatalog catalog, IGovernedLoopAuthoritySnapshotProvider authorityProvider)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _authorityProvider = authorityProvider ?? throw new ArgumentNullException(nameof(authorityProvider));
    }

    /// <summary>Normalizes and validates one raw graph against one catalog and authority evidence pair.</summary>
    /// <param name="candidate">The raw schema-1 graph candidate.</param>
    /// <param name="cancellationToken">Cancels current snapshot resolution.</param>
    /// <returns>A normalized graph only when every structural and current-admission invariant succeeds, plus deterministic evidence when snapshots are usable.</returns>
    /// <exception cref="OperationCanceledException">Thrown when snapshot resolution is canceled.</exception>
    public async Task<GovernedLoopGraphValidationResult> ValidateAsync(GovernedLoopGraphCandidate? candidate, CancellationToken cancellationToken = default)
    {
        var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
        if (!normalized.IsValid)
        {
            return new GovernedLoopGraphValidationResult(null, null, normalized.Errors);
        }

        GovernedLoopNodeCatalogSnapshot? catalog;
        GovernedLoopAuthoritySnapshot? authority;
        try
        {
            catalog = await _catalog.GetSnapshotAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure("catalog.unavailable", GovernedLoopGraphElementKind.Catalog, "catalog", "The current node catalog is unavailable.");
        }

        if (catalog is null || !catalog.IsAvailable)
        {
            return Failure("catalog.unavailable", GovernedLoopGraphElementKind.Catalog, "catalog", "The current node catalog is unavailable.");
        }

        try
        {
            authority = await _authorityProvider.GetSnapshotAsync(normalized.Graph!.OwningRole, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Failure("authority.unavailable", GovernedLoopGraphElementKind.Authority, "authority", "Current role authority is unavailable.");
        }

        if (authority is null || !authority.IsAvailable)
        {
            return Failure("authority.unavailable", GovernedLoopGraphElementKind.Authority, "authority", "Current role authority is unavailable.");
        }

        var snapshotErrors = new List<GovernedLoopGraphValidationError>();
        ValidateCatalogSnapshot(catalog, snapshotErrors);
        ValidateAuthoritySnapshot(authority, snapshotErrors);
        if (snapshotErrors.Count > 0)
        {
            return new GovernedLoopGraphValidationResult(null, null, Sort(snapshotErrors));
        }

        var evidence = GovernedLoopGraphValidationEvidenceHash.Compute(catalog, authority);
        var errors = new List<GovernedLoopGraphValidationError>();
        ValidateAuthority(normalized.Graph!, authority, errors);
        var catalogByKey = catalog.Descriptors.Take(CustomLoopLimits.MaxGraphNodes).ToDictionary(DescriptorKey);
        ValidateDescriptors(normalized.Graph!, catalogByKey, authority, errors);
        return errors.Count == 0
            ? new GovernedLoopGraphValidationResult(normalized.Graph, evidence, Array.Empty<GovernedLoopGraphValidationError>())
            : new GovernedLoopGraphValidationResult(null, evidence, Sort(errors));
    }

    private static void ValidateCatalogSnapshot(GovernedLoopNodeCatalogSnapshot snapshot, List<GovernedLoopGraphValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(snapshot.SourceEvidenceId) || snapshot.Descriptors is null || snapshot.Descriptors.Count > CustomLoopLimits.MaxGraphNodes)
        {
            Add(errors, "catalog.snapshot.invalid", GovernedLoopGraphElementKind.Catalog, snapshot.SourceEvidenceId, "catalog", "The catalog snapshot identity or bounded descriptor set is invalid.");
            return;
        }

        var keys = new HashSet<(GovernedLoopNodeKind Kind, string TypeId, int Version)>();
        foreach (var descriptor in snapshot.Descriptors.Take(CustomLoopLimits.MaxGraphNodes).OrderBy(item => item?.Descriptor?.TypeId, StringComparer.Ordinal))
        {
            if (descriptor is null || descriptor.Descriptor is null || !Enum.IsDefined(descriptor.Descriptor.Kind) || descriptor.Descriptor.Kind == GovernedLoopNodeKind.Unknown || !CustomLoopArtifactIdentifier.IsValid(descriptor.Descriptor.TypeId) || descriptor.Descriptor.Version < 1 || descriptor.Ports is null || descriptor.Parameters is null || descriptor.RequiredCapabilityIds is null || descriptor.AllowedControlOutcomes is null || descriptor.RequiredControlOutcomes is null || descriptor.ResourceBudget is null)
            {
                Add(errors, "catalog.descriptor.invalid", GovernedLoopGraphElementKind.Catalog, null, "catalog.descriptors", "A catalog descriptor is malformed.");
                continue;
            }

            if (!keys.Add(DescriptorKey(descriptor)))
            {
                Add(errors, "catalog.descriptor.duplicate", GovernedLoopGraphElementKind.Catalog, descriptor.Descriptor.TypeId, $"catalog.descriptors[{descriptor.Descriptor.TypeId}]", "Exact descriptor keys must be unique.");
            }

            ValidateCatalogDescriptor(descriptor, errors);
        }
    }

    private static void ValidateCatalogDescriptor(GovernedLoopNodeCatalogDescriptor descriptor, List<GovernedLoopGraphValidationError> errors)
    {
        var id = descriptor.Descriptor.TypeId;
        var path = $"catalog.descriptors[{id}]";
        if (!Enum.IsDefined(descriptor.JoinPolicy) || descriptor.MinimumIncomingControlEdges < 0 || descriptor.MinimumIncomingControlEdges > CustomLoopLimits.MaxGraphControlEdges)
        {
            Add(errors, "catalog.join-contract.invalid", GovernedLoopGraphElementKind.Catalog, id, path, "The catalog join contract is invalid.");
        }

        if (descriptor.JoinPolicy == GovernedLoopJoinPolicy.None && descriptor.MinimumIncomingControlEdges > 1 || descriptor.JoinPolicy != GovernedLoopJoinPolicy.None && descriptor.MinimumIncomingControlEdges < 1)
        {
            Add(errors, "catalog.join-contract.invalid", GovernedLoopGraphElementKind.Catalog, id, path, "Join arrival requirements do not match the declared join policy.");
        }

        var maximumOutcomes = Enum.GetValues<GovernedLoopControlCondition>().Count(value => value != GovernedLoopControlCondition.Unknown);
        if (descriptor.AllowedControlOutcomes.Count > maximumOutcomes || descriptor.RequiredControlOutcomes.Count > maximumOutcomes)
        {
            Add(errors, "catalog.control-outcomes.count", GovernedLoopGraphElementKind.Catalog, id, path, "Allowed and required control outcomes must fit the defined schema-1 outcome set.");
        }
        else
        {
            var allowed = descriptor.AllowedControlOutcomes.Take(maximumOutcomes).ToHashSet();
            var required = descriptor.RequiredControlOutcomes.Take(maximumOutcomes).ToArray();
            if (allowed.Any(value => !Enum.IsDefined(value) || value == GovernedLoopControlCondition.Unknown) || allowed.Count != descriptor.AllowedControlOutcomes.Count || required.Any(value => !allowed.Contains(value)) || required.Distinct().Count() != descriptor.RequiredControlOutcomes.Count)
            {
                Add(errors, "catalog.control-outcomes.invalid", GovernedLoopGraphElementKind.Catalog, id, path, "Allowed and required control outcomes must be defined, unique, and internally consistent.");
            }
        }

        if (descriptor.AllowsCycle)
        {
            if (!CustomLoopArtifactIdentifier.IsValid(descriptor.CycleIterationBudgetParameterId) || !CustomLoopArtifactIdentifier.IsValid(descriptor.CycleTimeBudgetMillisecondsParameterId) || string.Equals(descriptor.CycleIterationBudgetParameterId, descriptor.CycleTimeBudgetMillisecondsParameterId, StringComparison.Ordinal))
            {
                Add(errors, "catalog.cycle-budget-contract.invalid", GovernedLoopGraphElementKind.Catalog, id, path, "A cyclic descriptor must name distinct canonical iteration and time budget parameters.");
            }
        }
        else if (descriptor.CycleIterationBudgetParameterId is not null || descriptor.CycleTimeBudgetMillisecondsParameterId is not null)
        {
            Add(errors, "catalog.cycle-budget-contract.invalid", GovernedLoopGraphElementKind.Catalog, id, path, "A non-cyclic descriptor cannot declare cycle budget parameters.");
        }

        if (descriptor.Ports.Count > CustomLoopLimits.MaxGraphPortsPerNode)
        {
            Add(errors, "catalog.port-contract.count", GovernedLoopGraphElementKind.Catalog, id, $"{path}.ports", $"A descriptor may declare at most {CustomLoopLimits.MaxGraphPortsPerNode} port contracts.");
        }
        else
        {
            var portIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in descriptor.Ports.Take(CustomLoopLimits.MaxGraphPortsPerNode))
            {
                if (port is null
                    || !CustomLoopArtifactIdentifier.IsValid(port.Id)
                    || !portIds.Add(port.Id)
                    || !Enum.IsDefined(port.Direction)
                    || port.Direction == GovernedLoopPortDirection.Unknown
                    || !Enum.IsDefined(port.BindingKind)
                    || port.BindingKind == GovernedLoopBindingKind.Unknown
                    || !IsValidKindSet(port.AllowedValueKinds))
                {
                    Add(errors, "catalog.port-contract.invalid", GovernedLoopGraphElementKind.Catalog, id, $"{path}.ports", "Catalog port contracts must be canonical, unique, and fully defined.");
                }
            }
        }

        ValidateParameterContracts(descriptor, path, errors);

        if (descriptor.RequiredCapabilityIds.Count > CustomLoopLimits.MaxGraphAuthorityCapabilities)
        {
            Add(errors, "catalog.capabilities.count", GovernedLoopGraphElementKind.Catalog, id, $"{path}.requiredCapabilityIds", $"A descriptor may require at most {CustomLoopLimits.MaxGraphAuthorityCapabilities} capabilities.");
        }
        else if (descriptor.RequiredCapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).Any(capability => !CapabilityId.TryParse(capability, out _, out _)) || descriptor.RequiredCapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).Distinct(StringComparer.Ordinal).Count() != descriptor.RequiredCapabilityIds.Count)
        {
            Add(errors, "catalog.capabilities.invalid", GovernedLoopGraphElementKind.Catalog, id, $"{path}.requiredCapabilityIds", "Catalog capabilities must be canonical and unique.");
        }

        ValidateResourceBudget(descriptor.ResourceBudget, CustomLoopLimits.MaxGraphNodeAttempts, CustomLoopLimits.MaxGraphNodePayloadCharacters, CustomLoopLimits.MaxGraphNodeEvidenceItems, CustomLoopLimits.MaxGraphNodeResourceUnits, "catalog.resource-budget.invalid", GovernedLoopGraphElementKind.Catalog, id, $"{path}.resourceBudget", errors);
    }

    private static void ValidateParameterContracts(GovernedLoopNodeCatalogDescriptor descriptor, string descriptorPath, List<GovernedLoopGraphValidationError> errors)
    {
        if (descriptor.Parameters.Count > CustomLoopLimits.MaxGraphDescriptorParameters)
        {
            Add(errors, "catalog.parameter-contract.count", GovernedLoopGraphElementKind.Catalog, descriptor.Descriptor.TypeId, $"{descriptorPath}.parameters", $"A descriptor may declare at most {CustomLoopLimits.MaxGraphDescriptorParameters} parameter contracts.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in descriptor.Parameters.Take(CustomLoopLimits.MaxGraphDescriptorParameters).OrderBy(parameter => parameter?.Id, StringComparer.Ordinal))
        {
            if (parameter is null || !CustomLoopArtifactIdentifier.IsValid(parameter.Id) || !ids.Add(parameter.Id) || !Enum.IsDefined(parameter.ValueKind) || parameter.ValueKind == GovernedLoopParameterValueKind.Unknown || parameter.MinimumCharacters < 0 || parameter.MinimumCharacters > parameter.MaximumCharacters || parameter.MaximumCharacters > CustomLoopLimits.MaxGraphParameterValueCharacters || parameter.AllowedValues is null)
            {
                Add(errors, "catalog.parameter-contract.invalid", GovernedLoopGraphElementKind.Catalog, descriptor.Descriptor.TypeId, $"{descriptorPath}.parameters", "Parameter contracts must have unique canonical identities, defined value semantics, and bounded character ranges.");
                continue;
            }

            var hasIntegerRange = parameter.MinimumInteger.HasValue && parameter.MaximumInteger.HasValue && parameter.MinimumInteger <= parameter.MaximumInteger;
            var allowedValuesValid = parameter.AllowedValues.Count > 0 && parameter.AllowedValues.Count <= CustomLoopLimits.MaxGraphDescriptorParameters && parameter.AllowedValues.Take(CustomLoopLimits.MaxGraphDescriptorParameters).All(value => IsCanonicalParameterText(value, parameter.MinimumCharacters, parameter.MaximumCharacters)) && parameter.AllowedValues.Take(CustomLoopLimits.MaxGraphDescriptorParameters).Distinct(StringComparer.Ordinal).Count() == parameter.AllowedValues.Count;
            if (parameter.ValueKind == GovernedLoopParameterValueKind.Integer != hasIntegerRange || parameter.ValueKind == GovernedLoopParameterValueKind.Enumeration != allowedValuesValid || parameter.ValueKind != GovernedLoopParameterValueKind.Integer && (parameter.MinimumInteger.HasValue || parameter.MaximumInteger.HasValue) || parameter.ValueKind != GovernedLoopParameterValueKind.Enumeration && parameter.AllowedValues.Count > 0)
            {
                Add(errors, "catalog.parameter-contract.semantics", GovernedLoopGraphElementKind.Catalog, descriptor.Descriptor.TypeId, $"{descriptorPath}.parameters[{parameter.Id}]", "Integer ranges and enumeration values must be present only for their matching canonical value semantics.");
            }
        }

        if (descriptor.AllowsCycle)
        {
            ValidateCycleBudgetContract(descriptor, descriptor.CycleIterationBudgetParameterId!, CustomLoopLimits.MaxGraphCycleIterations, descriptorPath, errors);
            ValidateCycleBudgetContract(descriptor, descriptor.CycleTimeBudgetMillisecondsParameterId!, CustomLoopLimits.MaxGraphCycleMilliseconds, descriptorPath, errors);
        }
    }

    private static void ValidateCycleBudgetContract(GovernedLoopNodeCatalogDescriptor descriptor, string parameterId, long maximum, string descriptorPath, List<GovernedLoopGraphValidationError> errors)
    {
        var parameter = descriptor.Parameters.Take(CustomLoopLimits.MaxGraphDescriptorParameters).FirstOrDefault(value => value is not null && string.Equals(value.Id, parameterId, StringComparison.Ordinal));
        if (parameter is null || parameter.ValueKind != GovernedLoopParameterValueKind.Integer || !parameter.Required || parameter.MinimumInteger is null or < 1 || parameter.MaximumInteger is null || parameter.MaximumInteger > maximum)
        {
            Add(errors, "catalog.cycle-budget-parameter.invalid", GovernedLoopGraphElementKind.Catalog, descriptor.Descriptor.TypeId, $"{descriptorPath}.parameters[{parameterId}]", "Cycle budget parameters must be required positive bounded integer contracts.");
        }
    }

    private static void ValidateAuthoritySnapshot(GovernedLoopAuthoritySnapshot snapshot, List<GovernedLoopGraphValidationError> errors)
    {
        var role = snapshot.RoleRevision;
        var lifecycle = snapshot.RoleLifecycle;
        if (!IsSha256(snapshot.SourceEvidenceId)
            || snapshot.OwningRole?.Identity is null
            || role is null
            || lifecycle is null
            || !ContextualRoleWorkspaceId.IsValid(snapshot.WorkspaceId)
            || snapshot.SourceStatus != ContextualRoleInstructionSourceProbeStatus.Ready
            || !ContextualRoleRevisionValidator.Validate(role).IsValid
            || !Equals(role.Identity, snapshot.OwningRole.Identity)
            || !string.Equals(role.ContentHash, snapshot.OwningRole.ContentHash, StringComparison.Ordinal)
            || !role.WorkspaceApplicability.AppliesTo(snapshot.WorkspaceId)
            || !IsExactActiveLifecycle(lifecycle, snapshot.OwningRole.Identity)
            || snapshot.CapabilityIds is null)
        {
            Add(errors, "authority.snapshot.invalid", GovernedLoopGraphElementKind.Authority, snapshot.OwningRole?.Identity.RoleId, "authority", "The authority snapshot identity, role, workspace, source, lifecycle, or capabilities are invalid.");
        }
        else if (snapshot.CapabilityIds.Count > CustomLoopLimits.MaxGraphAuthorityCapabilities)
        {
            Add(errors, "authority.capabilities.count", GovernedLoopGraphElementKind.Authority, snapshot.OwningRole.Identity.RoleId, "authority.capabilityIds", $"Current role authority may contain at most {CustomLoopLimits.MaxGraphAuthorityCapabilities} capabilities.");
        }
        else if (snapshot.CapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).Any(capability => !CapabilityId.TryParse(capability, out _, out _))
            || snapshot.CapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).Distinct(StringComparer.Ordinal).Count() != snapshot.CapabilityIds.Count
            || !SameCapabilitySet(snapshot.CapabilityIds, role.PolicyMaxima.CapabilityIds))
        {
            Add(errors, "authority.snapshot.invalid", GovernedLoopGraphElementKind.Authority, snapshot.OwningRole.Identity.RoleId, "authority", "The authority snapshot capability maximum must exactly match the pinned contextual-role revision.");
        }

        ValidateResourceBudget(new GovernedLoopNodeResourceBudget(snapshot.MaxAttempts, snapshot.MaxPayloadCharacters, snapshot.MaxEvidenceItems, snapshot.MaxResourceUnits), CustomLoopLimits.MaxGraphAggregateAttempts, CustomLoopLimits.MaxGraphAggregatePayloadCharacters, CustomLoopLimits.MaxGraphAggregateEvidenceItems, CustomLoopLimits.MaxGraphAggregateResourceUnits, "authority.resource-limits.invalid", GovernedLoopGraphElementKind.Authority, snapshot.OwningRole?.Identity.RoleId, "authority.resourceLimits", errors);
    }

    private static void ValidateAuthority(GovernedLoopGraphDefinition graph, GovernedLoopAuthoritySnapshot authority, List<GovernedLoopGraphValidationError> errors)
    {
        if (!Equals(graph.OwningRole, authority.OwningRole))
        {
            Add(errors, "authority.role.mismatch", GovernedLoopGraphElementKind.Authority, authority.OwningRole?.Identity.RoleId, "authority.owningRole", "Authority evidence must belong to the graph's exact owning-role revision.");
        }

        var current = authority.CapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).ToHashSet(StringComparer.Ordinal);
        foreach (var capability in graph.AuthorityCeiling.CapabilityIds.Where(capability => !current.Contains(capability)).Order(StringComparer.Ordinal))
        {
            Add(errors, "authority.loop.widens-current-role", GovernedLoopGraphElementKind.Authority, capability, "graph.authorityCeiling", "The loop ceiling cannot widen current role authority.");
        }
    }

    private static bool SameCapabilitySet(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Count == right.Count && left.ToHashSet(StringComparer.Ordinal).SetEquals(right);

    private static bool IsExactActiveLifecycle(ContextualRoleLifecycleSnapshot lifecycle, ContextualRoleRevisionIdentity identity)
        => lifecycle.SchemaVersion == 1
            && string.Equals(lifecycle.RoleId, identity.RoleId, StringComparison.Ordinal)
            && Equals(lifecycle.CurrentIdentity, identity)
            && lifecycle.State == ContextualRoleLifecycleState.Active
            && ContextualRoleId.IsValid(lifecycle.LastOperationId)
            && Enum.IsDefined(lifecycle.LastMutationKind)
            && lifecycle.LastMutationKind != ContextualRoleRevisionMutationKind.Unknown
            && lifecycle.UpdatedAtUtc != default
            && lifecycle.UpdatedAtUtc.Offset == TimeSpan.Zero;

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateDescriptors(GovernedLoopGraphDefinition graph, IReadOnlyDictionary<(GovernedLoopNodeKind Kind, string TypeId, int Version), GovernedLoopNodeCatalogDescriptor> catalog, GovernedLoopAuthoritySnapshot authority, List<GovernedLoopGraphValidationError> errors)
    {
        var semantics = new Dictionary<string, GovernedLoopNodeCatalogDescriptor>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            var path = $"graph.nodes[{node.Id}]";
            if (!catalog.TryGetValue(DescriptorKey(node.Descriptor), out var descriptor))
            {
                Add(errors, "node.descriptor.not-advertised", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor", "The exact kind, type, and version are not currently advertised and cannot execute.");
                continue;
            }

            semantics[node.Id] = descriptor;
            if (!descriptor.IsAdvertised)
            {
                Add(errors, "node.descriptor.not-advertised", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor", "The exact descriptor is not currently advertised.");
            }

            if (!descriptor.IsExecutable)
            {
                Add(errors, "node.descriptor.not-executable", GovernedLoopGraphElementKind.Node, node.Id, $"{path}.descriptor", "The exact descriptor is not explicitly executable; no downgrade or substitution is allowed.");
            }

            if (string.Equals(node.Id, graph.EntryNodeId, StringComparison.Ordinal) && !descriptor.IsLegalEntry)
            {
                Add(errors, "node.entry.illegal", GovernedLoopGraphElementKind.Node, node.Id, path, "The exact descriptor is not a legal graph entry.");
            }

            if (graph.TerminalNodeIds.Contains(node.Id, StringComparer.Ordinal) != descriptor.IsLegalTerminal)
            {
                Add(errors, "node.terminal.contract", GovernedLoopGraphElementKind.Node, node.Id, path, "The graph terminal declaration does not match the exact descriptor contract.");
            }

            ValidateNodePorts(graph, node, descriptor, errors);
            ValidateNodeParameters(node, descriptor, errors);
            ValidatePureNodeSchemaSemantics(graph, node, errors);
            ValidateNodeAuthority(node, descriptor, errors);
        }

        ValidateControlOutcomes(graph, semantics, errors);
        ValidateJoins(graph, semantics, errors);
        ValidateCycles(graph, semantics, errors);
        ValidateResources(graph, semantics, authority, errors);
    }

    private static void ValidatePureNodeSchemaSemantics(
        GovernedLoopGraphDefinition graph,
        GovernedLoopNodeDefinition node,
        List<GovernedLoopGraphValidationError> errors)
    {
        if (!GovernedLoopPureNodeCatalogContract.TryResolve(node.Descriptor, out _))
        {
            return;
        }

        var schemas = graph.ValueSchemas.ToDictionary(schema => schema.Id, StringComparer.Ordinal);
        if (!GovernedLoopPureNodeCatalogContract.HasExactSchemaSemantics(node, schemas))
        {
            Add(
                errors,
                "node.pure-schema-contract.incompatible",
                GovernedLoopGraphElementKind.Node,
                node.Id,
                $"graph.nodes[{node.Id}]",
                "The pure-node schema relationships, bounded topology, nullability, element schema, or ordered bounds conflict with the exact executable descriptor semantics.");
        }
    }

    private static void ValidateNodePorts(GovernedLoopGraphDefinition graph, GovernedLoopNodeDefinition node, GovernedLoopNodeCatalogDescriptor descriptor, List<GovernedLoopGraphValidationError> errors)
    {
        var schemas = graph.ValueSchemas.ToDictionary(schema => schema.Id, StringComparer.Ordinal);
        var actual = node.Ports.ToDictionary(port => port.Id, StringComparer.Ordinal);
        var expected = descriptor.Ports.Take(CustomLoopLimits.MaxGraphPortsPerNode).ToDictionary(port => port.Id, StringComparer.Ordinal);
        foreach (var portId in actual.Keys.Union(expected.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var path = $"graph.nodes[{node.Id}].ports[{portId}]";
            if (!actual.TryGetValue(portId, out var port) || !expected.TryGetValue(portId, out var contract))
            {
                Add(errors, "node.port-contract.mismatch", GovernedLoopGraphElementKind.Port, $"{node.Id}.{portId}", path, "The node ports must exactly match the advertised descriptor contract.");
                continue;
            }

            if (!schemas.TryGetValue(port.ValueSchemaId, out var schema)
                || port.Direction != contract.Direction
                || port.BindingKind != contract.BindingKind
                || port.Required != contract.Required
                || contract.AllowedValueKinds is null
                || !contract.AllowedValueKinds.Contains(schema.Kind))
            {
                Add(errors, "node.port-contract.incompatible", GovernedLoopGraphElementKind.Port, $"{node.Id}.{portId}", path, "The port direction, channel, requiredness, or portable value kind conflicts with the descriptor contract.");
            }
        }
    }

    private static void ValidateNodeAuthority(GovernedLoopNodeDefinition node, GovernedLoopNodeCatalogDescriptor descriptor, List<GovernedLoopGraphValidationError> errors)
    {
        var ceiling = node.AuthorityCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        foreach (var capability in descriptor.RequiredCapabilityIds.Take(CustomLoopLimits.MaxGraphAuthorityCapabilities).Where(capability => !ceiling.Contains(capability)).Order(StringComparer.Ordinal))
        {
            Add(errors, "node.authority.missing-capability", GovernedLoopGraphElementKind.Node, node.Id, $"graph.nodes[{node.Id}].authorityCeiling", "The node ceiling does not contain a capability required by its exact descriptor.");
        }
    }

    private static void ValidateNodeParameters(GovernedLoopNodeDefinition node, GovernedLoopNodeCatalogDescriptor descriptor, List<GovernedLoopGraphValidationError> errors)
    {
        var contracts = descriptor.Parameters.Take(CustomLoopLimits.MaxGraphDescriptorParameters).ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        foreach (var parameter in node.Parameters.OrderBy(parameter => parameter.Key, StringComparer.Ordinal))
        {
            var path = $"graph.nodes[{node.Id}].parameters[{parameter.Key}]";
            if (!contracts.TryGetValue(parameter.Key, out var contract))
            {
                Add(errors, "node.parameter.undeclared", GovernedLoopGraphElementKind.Node, node.Id, path, "Executable parameters must be explicitly declared by the exact descriptor contract.");
            }
            else if (!IsCompatibleParameterValue(parameter.Value, contract))
            {
                Add(errors, "node.parameter.incompatible", GovernedLoopGraphElementKind.Node, node.Id, path, "The executable parameter value is not canonical or violates its exact descriptor semantics.");
            }
        }

        foreach (var contract in descriptor.Parameters.Take(CustomLoopLimits.MaxGraphDescriptorParameters).Where(contract => contract.Required && !node.Parameters.ContainsKey(contract.Id)).OrderBy(contract => contract.Id, StringComparer.Ordinal))
        {
            Add(errors, "node.parameter.required", GovernedLoopGraphElementKind.Node, node.Id, $"graph.nodes[{node.Id}].parameters[{contract.Id}]", "A required executable parameter is missing.");
        }
    }

    private static bool IsCompatibleParameterValue(string value, GovernedLoopCatalogParameterContract contract)
    {
        if (!IsCanonicalParameterText(value, contract.MinimumCharacters, contract.MaximumCharacters))
        {
            return false;
        }

        return contract.ValueKind switch
        {
            GovernedLoopParameterValueKind.Text => true,
            GovernedLoopParameterValueKind.Boolean => value is "true" or "false",
            GovernedLoopParameterValueKind.Integer => contract.MinimumInteger.HasValue && contract.MaximumInteger.HasValue && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) && string.Equals(integer.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal) && integer >= contract.MinimumInteger.Value && integer <= contract.MaximumInteger.Value,
            GovernedLoopParameterValueKind.Number => IsCanonicalFiniteNumber(value),
            GovernedLoopParameterValueKind.Identifier => CustomLoopArtifactIdentifier.IsValid(value),
            GovernedLoopParameterValueKind.JsonPointer => IsCanonicalJsonPointer(value),
            GovernedLoopParameterValueKind.Enumeration => contract.AllowedValues.Contains(value, StringComparer.Ordinal),
            _ => false
        };
    }

    private static bool IsValidKindSet(GovernedLoopValueKindSet? kinds)
    {
        if (kinds?.Kinds is not { Count: > 0 } values)
        {
            return false;
        }

        var maximum = Enum.GetValues<GovernedLoopValueKind>().Count(value => value != GovernedLoopValueKind.Unknown);
        return values.Count <= maximum
            && values.All(value => Enum.IsDefined(value) && value != GovernedLoopValueKind.Unknown)
            && values.Distinct().Count() == values.Count
            && values.SequenceEqual(values.Order());
    }

    private static bool IsCanonicalFiniteNumber(string value)
    {
        return GovernedLoopTypedValue.TryCreate(
                GovernedLoopTypedValue.CurrentSchemaVersion,
                GovernedLoopValueKind.Number,
                value,
                out var canonical,
                out _)
            && !canonical!.IsNull
            && string.Equals(canonical.CanonicalValueJson, value, StringComparison.Ordinal)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number);
    }

    private static bool IsCanonicalJsonPointer(string value)
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
            if (value[index] != '~')
            {
                continue;
            }

            if (++index >= value.Length || value[index] is not ('0' or '1'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCanonicalParameterText(string? value, int minimumCharacters, int maximumCharacters)
    {
        if (value is null || value.Length < minimumCharacters || value.Length > maximumCharacters || value.Contains('\r', StringComparison.Ordinal) || value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])) || !HasWellFormedUnicode(value))
        {
            return false;
        }

        try
        {
            return value.IsNormalized(NormalizationForm.FormC) && !value.EnumerateRunes().Any(IsUnsafeRune);
        }
        catch (ArgumentException)
        {
            return false;
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

    private static void ValidateControlOutcomes(GovernedLoopGraphDefinition graph, IReadOnlyDictionary<string, GovernedLoopNodeCatalogDescriptor> semantics, List<GovernedLoopGraphValidationError> errors)
    {
        foreach (var node in graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!semantics.TryGetValue(node.Id, out var descriptor))
            {
                continue;
            }

            var outgoing = graph.ControlEdges.Where(edge => string.Equals(edge.FromNodeId, node.Id, StringComparison.Ordinal)).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
            var allowed = descriptor.AllowedControlOutcomes.Take(Enum.GetValues<GovernedLoopControlCondition>().Length - 1).ToHashSet();
            foreach (var edge in outgoing.Where(edge => !allowed.Contains(edge.Condition)))
            {
                Add(errors, "edge.outcome.not-allowed", GovernedLoopGraphElementKind.ControlEdge, edge.Id, $"graph.controlEdges[{edge.Id}].condition", "The edge outcome is not emitted by the exact source descriptor.");
            }

            var actual = outgoing.Select(edge => edge.Condition).ToHashSet();
            foreach (var required in descriptor.RequiredControlOutcomes.Take(Enum.GetValues<GovernedLoopControlCondition>().Length - 1).Where(required => !actual.Contains(required)).OrderBy(value => value))
            {
                Add(errors, "node.branch-outcome.missing", GovernedLoopGraphElementKind.Node, node.Id, $"graph.nodes[{node.Id}]", $"Required branch outcome `{required}` has no outgoing control edge.");
            }
        }
    }

    private static void ValidateJoins(GovernedLoopGraphDefinition graph, IReadOnlyDictionary<string, GovernedLoopNodeCatalogDescriptor> semantics, List<GovernedLoopGraphValidationError> errors)
    {
        foreach (var node in graph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!semantics.TryGetValue(node.Id, out var descriptor) || descriptor.JoinPolicy == GovernedLoopJoinPolicy.None)
            {
                continue;
            }

            var incoming = graph.ControlEdges.Where(edge => string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal)).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
            if (incoming.Length < descriptor.MinimumIncomingControlEdges)
            {
                Add(errors, "node.join.incoming-insufficient", GovernedLoopGraphElementKind.Node, node.Id, $"graph.nodes[{node.Id}]", "The join has fewer incoming control paths than its exact descriptor requires.");
            }

            if (descriptor.JoinPolicy == GovernedLoopJoinPolicy.All && !AreJoinInputsJointlySatisfiable(graph, node.Id, incoming))
            {
                Add(errors, "node.join.unsatisfiable", GovernedLoopGraphElementKind.Node, node.Id, $"graph.nodes[{node.Id}]", "An all-path join cannot require a self-produced arrival or control paths gated by mutually exclusive outcomes.");
            }
        }
    }

    private static bool AreJoinInputsJointlySatisfiable(GovernedLoopGraphDefinition graph, string joinNodeId, IReadOnlyList<GovernedLoopControlEdgeDefinition> incoming)
    {
        if (incoming.Any(edge => string.Equals(edge.FromNodeId, joinNodeId, StringComparison.Ordinal)) || incoming.GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal).Any(group => group.Select(edge => edge.Condition).Distinct().Any(first => group.Any(second => AreMutuallyExclusive(first, second.Condition)))))
        {
            return false;
        }

        var adjacency = graph.Nodes.ToDictionary(node => node.Id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in graph.ControlEdges)
        {
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
        }

        foreach (var branch in graph.ControlEdges.GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal).Where(group => !string.Equals(group.Key, joinNodeId, StringComparison.Ordinal) && group.Select(edge => edge.Condition).Distinct().Count() > 1).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            foreach (var left in incoming)
            {
                var leftOutcomes = branch.Where(edge => CanReachBeforeJoin(edge.ToNodeId, left.FromNodeId, joinNodeId, adjacency)).Select(edge => edge.Condition).Distinct().ToArray();
                if (leftOutcomes.Length == 0)
                {
                    continue;
                }

                foreach (var right in incoming.Where(edge => string.CompareOrdinal(edge.Id, left.Id) > 0))
                {
                    var rightOutcomes = branch.Where(edge => CanReachBeforeJoin(edge.ToNodeId, right.FromNodeId, joinNodeId, adjacency)).Select(edge => edge.Condition).Distinct().ToArray();
                    if (rightOutcomes.Length > 0 && leftOutcomes.All(leftOutcome => rightOutcomes.All(rightOutcome => AreMutuallyExclusive(leftOutcome, rightOutcome))))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool AreMutuallyExclusive(GovernedLoopControlCondition left, GovernedLoopControlCondition right)
    {
        return left != right && (IsTimeoutPair(left, right) || (left, right) is
            (GovernedLoopControlCondition.Success, GovernedLoopControlCondition.Failure) or
            (GovernedLoopControlCondition.Failure, GovernedLoopControlCondition.Success) or
            (GovernedLoopControlCondition.True, GovernedLoopControlCondition.False) or
            (GovernedLoopControlCondition.False, GovernedLoopControlCondition.True) or
            (GovernedLoopControlCondition.Approved, GovernedLoopControlCondition.Rejected) or
            (GovernedLoopControlCondition.Rejected, GovernedLoopControlCondition.Approved));
    }

    private static bool IsTimeoutPair(GovernedLoopControlCondition left, GovernedLoopControlCondition right)
    {
        return left == GovernedLoopControlCondition.Timeout && IsExclusiveWithTimeout(right) || right == GovernedLoopControlCondition.Timeout && IsExclusiveWithTimeout(left);
    }

    private static bool IsExclusiveWithTimeout(GovernedLoopControlCondition condition) => condition is GovernedLoopControlCondition.Success or GovernedLoopControlCondition.Failure or GovernedLoopControlCondition.True or GovernedLoopControlCondition.False or GovernedLoopControlCondition.Approved or GovernedLoopControlCondition.Rejected;

    private static bool CanReachBeforeJoin(string source, string target, string joinNodeId, IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (string.Equals(current, joinNodeId, StringComparison.Ordinal) || !visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, target, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var next in adjacency[current].Reverse())
            {
                pending.Push(next);
            }
        }

        return false;
    }

    private static void ValidateCycles(GovernedLoopGraphDefinition graph, IReadOnlyDictionary<string, GovernedLoopNodeCatalogDescriptor> semantics, List<GovernedLoopGraphValidationError> errors)
    {
        var adjacency = graph.Nodes.ToDictionary(node => node.Id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in graph.ControlEdges.OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
        }

        foreach (var component in StronglyConnectedComponents(graph.Nodes.Select(node => node.Id), adjacency))
        {
            var cyclic = component.Count > 1 || adjacency[component[0]].Contains(component[0]);
            if (!cyclic)
            {
                continue;
            }

            var componentNodeIds = component.ToHashSet(StringComparer.Ordinal);
            foreach (var nodeId in component.Order(StringComparer.Ordinal))
            {
                if (InternalControlFanOut(nodeId, componentNodeIds, graph) > 1)
                {
                    Add(errors, "node.cycle.internal-fan-out-unsupported", GovernedLoopGraphElementKind.Node, nodeId, $"graph.nodes[{nodeId}]", "A cyclic node may have only one effective internal successor edge until an SCC-wide activation contract is runtime-enforceable.");
                }

                var node = graph.Nodes.Single(value => string.Equals(value.Id, nodeId, StringComparison.Ordinal));
                if (!semantics.TryGetValue(nodeId, out var descriptor) || !descriptor.AllowsCycle)
                {
                    Add(errors, "node.cycle.not-allowed", GovernedLoopGraphElementKind.Node, nodeId, $"graph.nodes[{nodeId}]", "Every node participating in a cycle must explicitly advertise bounded-cycle semantics.");
                    continue;
                }

                ValidateCycleBudget(node, descriptor.CycleIterationBudgetParameterId!, CustomLoopLimits.MaxGraphCycleIterations, "node.cycle.iteration-budget", errors);
                ValidateCycleBudget(node, descriptor.CycleTimeBudgetMillisecondsParameterId!, CustomLoopLimits.MaxGraphCycleMilliseconds, "node.cycle.time-budget", errors);
            }
        }
    }

    private static void ValidateCycleBudget(GovernedLoopNodeDefinition node, string parameterId, long maximum, string code, List<GovernedLoopGraphValidationError> errors)
    {
        if (!node.Parameters.TryGetValue(parameterId, out var raw) || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1 || value > maximum)
        {
            Add(errors, code, GovernedLoopGraphElementKind.Node, node.Id, $"graph.nodes[{node.Id}].parameters[{parameterId}]", $"A cycle requires an explicit positive budget no greater than {maximum}.");
        }
    }

    private static void ValidateResources(GovernedLoopGraphDefinition graph, IReadOnlyDictionary<string, GovernedLoopNodeCatalogDescriptor> semantics, GovernedLoopAuthoritySnapshot authority, List<GovernedLoopGraphValidationError> errors)
    {
        var totals = CalculateResourceTotals(graph, semantics);
        if (totals.IsSaturated)
        {
            Add(errors, "graph.resources.activation-envelope", GovernedLoopGraphElementKind.Graph, null, "graph.controlEdges", "The conservative activation envelope is invalid or exceeds supported arithmetic.");
        }

        ValidateAggregate(totals.Attempts, authority.MaxAttempts, "graph.resources.attempts", errors);
        ValidateAggregate(totals.PayloadCharacters, authority.MaxPayloadCharacters, "graph.resources.payload", errors);
        ValidateAggregate(totals.EvidenceItems, authority.MaxEvidenceItems, "graph.resources.evidence", errors);
        ValidateAggregate(totals.ResourceUnits, authority.MaxResourceUnits, "graph.resources.units", errors);
    }

    private static (long Attempts, long PayloadCharacters, long EvidenceItems, long ResourceUnits, bool IsSaturated) CalculateResourceTotals(GovernedLoopGraphDefinition graph, IReadOnlyDictionary<string, GovernedLoopNodeCatalogDescriptor> semantics)
    {
        var adjacency = graph.Nodes.ToDictionary(node => node.Id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in graph.ControlEdges.OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
        }

        var components = StronglyConnectedComponents(graph.Nodes.Select(node => node.Id), adjacency);
        var componentByNode = components.SelectMany((component, componentIndex) => component.Select(nodeId => (nodeId, componentIndex))).ToDictionary(item => item.nodeId, item => item.componentIndex, StringComparer.Ordinal);
        var outgoing = components.Select(_ => new List<(int Target, string EdgeId)>()).ToArray();
        var indegrees = new int[components.Count];
        foreach (var edge in graph.ControlEdges.OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            var source = componentByNode[edge.FromNodeId];
            var target = componentByNode[edge.ToNodeId];
            if (source == target)
            {
                continue;
            }

            outgoing[source].Add((target, edge.Id));
            indegrees[target]++;
        }

        foreach (var edges in outgoing)
        {
            edges.Sort((left, right) => left.Target != right.Target ? left.Target.CompareTo(right.Target) : string.CompareOrdinal(left.EdgeId, right.EdgeId));
        }

        var entries = new long[components.Count];
        entries[componentByNode[graph.EntryNodeId]] = 1;
        var ready = new SortedSet<int>(Enumerable.Range(0, components.Count).Where(index => indegrees[index] == 0));
        var isSaturated = false;
        long attempts = 0;
        long payloadCharacters = 0;
        long evidenceItems = 0;
        long resourceUnits = 0;
        var processed = 0;
        while (ready.Count > 0)
        {
            var componentIndex = ready.Min;
            ready.Remove(componentIndex);
            processed++;
            var component = components[componentIndex];
            var cyclic = component.Count > 1 || adjacency[component[0]].Contains(component[0]);
            if (cyclic)
            {
                var componentNodeIds = component.ToHashSet(StringComparer.Ordinal);
                isSaturated |= component.Any(nodeId => InternalControlFanOut(nodeId, componentNodeIds, graph) > 1);
            }

            var internalMultiplier = cyclic ? CycleIterationProduct(component, graph, semantics, ref isSaturated) : 1;
            var executionMultiplicity = SaturatingMultiply(entries[componentIndex], internalMultiplier, ref isSaturated);
            long componentAttempts = 0;
            long componentPayloadCharacters = 0;
            long componentEvidenceItems = 0;
            long componentResourceUnits = 0;
            foreach (var nodeId in component.Order(StringComparer.Ordinal))
            {
                if (!semantics.TryGetValue(nodeId, out var descriptor))
                {
                    continue;
                }

                componentAttempts = SaturatingAdd(componentAttempts, descriptor.ResourceBudget.Attempts, ref isSaturated);
                componentPayloadCharacters = SaturatingAdd(componentPayloadCharacters, descriptor.ResourceBudget.PayloadCharacters, ref isSaturated);
                componentEvidenceItems = SaturatingAdd(componentEvidenceItems, descriptor.ResourceBudget.EvidenceItems, ref isSaturated);
                componentResourceUnits = SaturatingAdd(componentResourceUnits, descriptor.ResourceBudget.ResourceUnits, ref isSaturated);
            }

            attempts = SaturatingAdd(attempts, SaturatingMultiply(componentAttempts, executionMultiplicity, ref isSaturated), ref isSaturated);
            payloadCharacters = SaturatingAdd(payloadCharacters, SaturatingMultiply(componentPayloadCharacters, executionMultiplicity, ref isSaturated), ref isSaturated);
            evidenceItems = SaturatingAdd(evidenceItems, SaturatingMultiply(componentEvidenceItems, executionMultiplicity, ref isSaturated), ref isSaturated);
            resourceUnits = SaturatingAdd(resourceUnits, SaturatingMultiply(componentResourceUnits, executionMultiplicity, ref isSaturated), ref isSaturated);
            foreach (var edge in outgoing[componentIndex])
            {
                entries[edge.Target] = SaturatingAdd(entries[edge.Target], executionMultiplicity, ref isSaturated);
                if (--indegrees[edge.Target] == 0)
                {
                    ready.Add(edge.Target);
                }
            }
        }

        return (attempts, payloadCharacters, evidenceItems, resourceUnits, isSaturated || processed != components.Count);
    }

    private static int InternalControlFanOut(string nodeId, IReadOnlySet<string> componentNodeIds, GovernedLoopGraphDefinition graph)
    {
        return graph.ControlEdges.Count(edge => string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal) && componentNodeIds.Contains(edge.ToNodeId));
    }

    private static long CycleIterationProduct(IReadOnlyList<string> component, GovernedLoopGraphDefinition graph, IReadOnlyDictionary<string, GovernedLoopNodeCatalogDescriptor> semantics, ref bool isSaturated)
    {
        long product = 1;
        foreach (var nodeId in component.Order(StringComparer.Ordinal))
        {
            var node = graph.Nodes.Single(node => string.Equals(node.Id, nodeId, StringComparison.Ordinal));
            if (!semantics.TryGetValue(nodeId, out var descriptor) || !descriptor.AllowsCycle || descriptor.CycleIterationBudgetParameterId is null || !node.Parameters.TryGetValue(descriptor.CycleIterationBudgetParameterId, out var raw) || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1 || iterations > CustomLoopLimits.MaxGraphCycleIterations)
            {
                isSaturated = true;
                return long.MaxValue;
            }

            product = SaturatingMultiply(product, iterations, ref isSaturated);
        }

        return product;
    }

    private static long SaturatingAdd(long left, long right, ref bool isSaturated)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException)
        {
            isSaturated = true;
            return long.MaxValue;
        }
    }

    private static long SaturatingMultiply(long left, long right, ref bool isSaturated)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException)
        {
            isSaturated = true;
            return long.MaxValue;
        }
    }

    private static void ValidateAggregate(long value, int maximum, string code, List<GovernedLoopGraphValidationError> errors)
    {
        if (value > maximum)
        {
            Add(errors, code, GovernedLoopGraphElementKind.Graph, null, "graph.nodes", "The graph's catalog-defined resource envelope exceeds current role authority.");
        }
    }

    private static void ValidateResourceBudget(GovernedLoopNodeResourceBudget budget, int maximumAttempts, int maximumPayload, int maximumEvidence, int maximumUnits, string code, GovernedLoopGraphElementKind kind, string? id, string path, List<GovernedLoopGraphValidationError> errors)
    {
        if (budget.Attempts < 0 || budget.Attempts > maximumAttempts || budget.PayloadCharacters < 0 || budget.PayloadCharacters > maximumPayload || budget.EvidenceItems < 0 || budget.EvidenceItems > maximumEvidence || budget.ResourceUnits < 0 || budget.ResourceUnits > maximumUnits)
        {
            Add(errors, code, kind, id, path, "Resource limits must be non-negative and within the supported schema-1 maxima.");
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
            lowLinks[node] = index++;
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

    private static (GovernedLoopNodeKind Kind, string TypeId, int Version) DescriptorKey(GovernedLoopNodeCatalogDescriptor descriptor) => DescriptorKey(descriptor.Descriptor);

    private static (GovernedLoopNodeKind Kind, string TypeId, int Version) DescriptorKey(GovernedLoopNodeDescriptor descriptor) => (descriptor.Kind, descriptor.TypeId, descriptor.Version);

    private static GovernedLoopGraphValidationResult Failure(string code, GovernedLoopGraphElementKind kind, string path, string message)
    {
        return new GovernedLoopGraphValidationResult(null, null, [new GovernedLoopGraphValidationError(code, new GovernedLoopGraphElementReference(kind, null, path), message)]);
    }

    private static void Add(List<GovernedLoopGraphValidationError> errors, string code, GovernedLoopGraphElementKind kind, string? id, string path, string message)
    {
        errors.Add(new GovernedLoopGraphValidationError(code.Length <= CustomLoopLimits.MaxGraphValidationErrorCodeCharacters ? code : code[..CustomLoopLimits.MaxGraphValidationErrorCodeCharacters], new GovernedLoopGraphElementReference(kind, id is null || id.Length <= CustomLoopLimits.MaxArtifactIdCharacters ? id : id[..CustomLoopLimits.MaxArtifactIdCharacters], path.Length <= CustomLoopLimits.MaxGraphValidationErrorPathCharacters ? path : path[..CustomLoopLimits.MaxGraphValidationErrorPathCharacters]), message.Length <= CustomLoopLimits.MaxGraphValidationErrorMessageCharacters ? message : message[..CustomLoopLimits.MaxGraphValidationErrorMessageCharacters]));
    }

    private static IReadOnlyList<GovernedLoopGraphValidationError> Sort(IEnumerable<GovernedLoopGraphValidationError> errors)
    {
        return Array.AsReadOnly(errors.OrderBy(error => error.Element.Path, StringComparer.Ordinal).ThenBy(error => error.Code, StringComparer.Ordinal).ThenBy(error => error.Element.Id, StringComparer.Ordinal).Take(CustomLoopLimits.MaxGraphValidationErrors).ToArray());
    }
}
