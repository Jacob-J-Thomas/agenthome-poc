using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Builds one deterministic immutable topology plan while retaining the established sequential projection.</summary>
public static class GovernedLoopSequentialPlanBuilder
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ScheduleTriggerCapabilityId = "org.embodysense/triggers/time";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";

    /// <summary>Builds one exact Trigger-to-terminal topology containing supported inference, pure, Condition, Join, and bounded-cycle nodes.</summary>
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
            || graph.ControlEdges.Count is < 1 or > CustomLoopLimits.MaxGraphControlEdges
            || graph.TerminalNodeIds.Count is < 1 or > 2)
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph");
        }

        var nodeById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (!nodeById.TryGetValue(graph.EntryNodeId, out var entry)
            || !GovernedLoopSequentialNodeDescriptors.IsEntryTrigger(entry.Descriptor))
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.entryNodeId");
        }

        var terminals = graph.TerminalNodeIds.Select(nodeById.GetValueOrDefault).ToArray();
        if (terminals.Any(terminal => terminal is null)
            || terminals.Count(terminal => Equals(terminal!.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)) != 1
            || terminals.Count(terminal => Equals(terminal!.Descriptor, GovernedLoopSequentialNodeDescriptors.FailTerminal)) > 1
            || terminals.Any(terminal => !Equals(terminal!.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)
                && !Equals(terminal.Descriptor, GovernedLoopSequentialNodeDescriptors.FailTerminal)))
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.terminalNodeIds");
        }

        var topology = AnalyzeTopology(graph);
        if (topology is null)
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.controlEdges");
        }

        var planNodes = BuildPlanNodes(graph, topology);
        var inferenceCount = planNodes.Count(node => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference));
        var hasExecutableNode = inferenceCount >= CustomLoopLimits.MinInferenceSteps
            || planNodes.Any(node => GovernedLoopSequentialNodeDescriptors.IsWait(node.Descriptor)
                || GovernedLoopSequentialNodeDescriptors.IsRecoverableAction(node.Descriptor));
        if (inferenceCount > CustomLoopLimits.MaxInferenceSteps
            || !hasExecutableNode)
        {
            return Failure(GovernedLoopSequentialPlanBuildStatus.UnsupportedTopology, "$.graph.nodes");
        }

        var contractFailurePath = ExactContractFailurePath(graph, planNodes, topology);
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
            Array.AsReadOnly(planNodes.ToArray()),
            Array.AsReadOnly(graph.ControlEdges.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(topology.Components.ToArray()),
            GovernedLoopTopologySchedulerPolicy.Create());
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

    private static GovernedLoopTopologyAnalysis? AnalyzeTopology(GovernedLoopGraphDefinition graph)
    {
        var nodeIds = graph.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal).ToArray();
        var adjacency = nodeIds.ToDictionary(nodeId => nodeId, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var reverse = nodeIds.ToDictionary(nodeId => nodeId, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in graph.ControlEdges.OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
            reverse[edge.ToNodeId].Add(edge.FromNodeId);
        }

        var terminalReachability = graph.TerminalNodeIds
            .SelectMany(terminalNodeId => Traverse(terminalNodeId, reverse))
            .ToHashSet(StringComparer.Ordinal);
        if (!Traverse(graph.EntryNodeId, adjacency).SetEquals(nodeIds)
            || !terminalReachability.SetEquals(nodeIds)
            || reverse[graph.EntryNodeId].Count != 0
            || graph.TerminalNodeIds.Any(terminalNodeId => adjacency[terminalNodeId].Count != 0)
            || !HasExactControlOutcomes(graph)
            || HasImpossibleJoin(graph))
        {
            return null;
        }

        var stronglyConnected = StronglyConnectedComponents(nodeIds, adjacency);
        var rawComponentByNode = stronglyConnected
            .SelectMany((nodes, index) => nodes.Select(nodeId => (nodeId, index)))
            .ToDictionary(item => item.nodeId, item => item.index, StringComparer.Ordinal);
        foreach (var node in graph.Nodes.Where(node => !string.Equals(node.Id, graph.EntryNodeId, StringComparison.Ordinal) && node.Descriptor.Kind != GovernedLoopNodeKind.Join))
        {
            var component = stronglyConnected[rawComponentByNode[node.Id]];
            var cyclic = component.Count > 1 || adjacency[component[0]].Contains(component[0]);
            if (!cyclic && graph.ControlEdges.Count(edge => string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal)) != 1)
            {
                return null;
            }
        }

        var componentOutgoing = stronglyConnected.Select(_ => new SortedSet<int>()).ToArray();
        var componentIncoming = stronglyConnected.Select(_ => new SortedSet<int>()).ToArray();
        foreach (var edge in graph.ControlEdges)
        {
            var source = rawComponentByNode[edge.FromNodeId];
            var target = rawComponentByNode[edge.ToNodeId];
            if (source != target)
            {
                componentOutgoing[source].Add(target);
                componentIncoming[target].Add(source);
            }
        }

        var ready = new SortedSet<(string Key, int Index)>(Comparer<(string Key, int Index)>.Create((left, right) =>
        {
            var comparison = string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            return comparison != 0 ? comparison : left.Index.CompareTo(right.Index);
        }));
        for (var index = 0; index < stronglyConnected.Count; index++)
        {
            if (componentIncoming[index].Count == 0)
            {
                ready.Add((stronglyConnected[index][0], index));
            }
        }

        var orderedIndexes = new List<int>(stronglyConnected.Count);
        var remainingIncoming = componentIncoming.Select(value => value.Count).ToArray();
        while (ready.Count > 0)
        {
            var current = ready.Min;
            ready.Remove(current);
            orderedIndexes.Add(current.Index);
            foreach (var target in componentOutgoing[current.Index])
            {
                remainingIncoming[target]--;
                if (remainingIncoming[target] == 0)
                {
                    ready.Add((stronglyConnected[target][0], target));
                }
            }
        }

        if (orderedIndexes.Count != stronglyConnected.Count)
        {
            return null;
        }

        var nodesById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var components = new List<GovernedLoopTopologyComponent>(stronglyConnected.Count);
        var componentByNodeId = new Dictionary<string, GovernedLoopTopologyComponent>(StringComparer.Ordinal);
        foreach (var (rawIndex, staticOrdinal) in orderedIndexes.Select((value, index) => (value, index)))
        {
            var componentNodes = stronglyConnected[rawIndex];
            var cyclic = componentNodes.Count > 1 || adjacency[componentNodes[0]].Contains(componentNodes[0]);
            IReadOnlyList<string> traversalNodes = componentNodes;
            var identitySuffix = ComponentIdentity(componentNodes);
            var componentId = $"component-{identitySuffix}";
            string? cycleId = cyclic ? $"cycle-{identitySuffix}" : null;
            int? maximumIterations = null;
            long? maximumDurationMilliseconds = null;
            if (cyclic)
            {
                var externalIncoming = graph.ControlEdges.Where(edge => rawComponentByNode[edge.ToNodeId] == rawIndex && rawComponentByNode[edge.FromNodeId] != rawIndex).ToArray();
                var externalOutgoing = graph.ControlEdges.Where(edge => rawComponentByNode[edge.FromNodeId] == rawIndex && rawComponentByNode[edge.ToNodeId] != rawIndex).ToArray();
                if (externalIncoming.Length != 1 || externalOutgoing.Length < 1
                    || externalOutgoing.Any(edge =>
                    {
                        var internalEdges = graph.ControlEdges.Where(candidate =>
                            string.Equals(candidate.FromNodeId, edge.FromNodeId, StringComparison.Ordinal)
                            && rawComponentByNode[candidate.ToNodeId] == rawIndex).ToArray();
                        return internalEdges.Length != 1
                            || !GovernedLoopControlTopologySemantics.AreMutuallyExclusive(internalEdges[0].Condition, edge.Condition);
                    })
                    || !TryOrderCycleNodes(componentNodes, externalIncoming[0].ToNodeId, graph, rawComponentByNode, rawIndex, out traversalNodes))
                {
                    return null;
                }

                foreach (var nodeId in traversalNodes)
                {
                    var node = nodesById[nodeId];
                    if (graph.ControlEdges.Count(edge => rawComponentByNode[edge.FromNodeId] == rawIndex && rawComponentByNode[edge.ToNodeId] == rawIndex && string.Equals(edge.FromNodeId, nodeId, StringComparison.Ordinal)) > 1
                        || !TryCycleBounds(node, out var iterations, out var duration))
                    {
                        return null;
                    }

                    maximumIterations = Math.Min(maximumIterations ?? int.MaxValue, checked((int)iterations));
                    maximumDurationMilliseconds = Math.Min(maximumDurationMilliseconds ?? long.MaxValue, duration);
                }
            }

            var component = new GovernedLoopTopologyComponent(
                staticOrdinal,
                componentId,
                cycleId,
                cyclic,
                Array.AsReadOnly(traversalNodes.ToArray()),
                maximumIterations,
                maximumDurationMilliseconds);
            components.Add(component);
            foreach (var nodeId in traversalNodes)
            {
                componentByNodeId.Add(nodeId, component);
            }
        }

        var selectedJoins = graph.Nodes.Where(node => GovernedLoopTopologyNodeCatalogContract.TryResolve(node.Descriptor, out var contract)
            && contract?.JoinPolicy == GovernedLoopJoinPolicy.Selected);
        if (selectedJoins.Any(join => graph.ControlEdges
            .Where(edge => string.Equals(edge.ToNodeId, join.Id, StringComparison.Ordinal))
            .Any(edge => componentByNodeId[edge.FromNodeId].IsCyclic
                && !string.Equals(componentByNodeId[edge.FromNodeId].ComponentId, componentByNodeId[join.Id].ComponentId, StringComparison.Ordinal))))
        {
            // Schema 1 has no cycle-termination artifact that can prove a skipped exit from an
            // earlier iteration will remain pruned for a downstream Selected Join.
            return null;
        }

        return new GovernedLoopTopologyAnalysis(Array.AsReadOnly(components.ToArray()), componentByNodeId);
    }

    private static List<GovernedLoopSequentialPlanNode> BuildPlanNodes(
        GovernedLoopGraphDefinition graph,
        GovernedLoopTopologyAnalysis topology)
    {
        var incoming = graph.ControlEdges
            .GroupBy(edge => edge.ToNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Id).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var outgoing = graph.ControlEdges
            .GroupBy(edge => edge.FromNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(edge => edge.Id, StringComparer.Ordinal).ThenBy(edge => edge.ToNodeId, StringComparer.Ordinal).Select(edge => edge.Id).ToArray(), StringComparer.Ordinal);
        var nodes = new List<GovernedLoopSequentialPlanNode>(graph.Nodes.Count);
        foreach (var component in topology.Components)
        {
            foreach (var (nodeId, traversalOrdinal) in component.NodeIds.Select((value, index) => (value, index)))
            {
                var node = graph.Nodes.Single(value => string.Equals(value.Id, nodeId, StringComparison.Ordinal));
                var incomingEdgeIds = incoming.GetValueOrDefault(node.Id) ?? [];
                var outgoingEdgeIds = outgoing.GetValueOrDefault(node.Id) ?? [];
                nodes.Add(new GovernedLoopSequentialPlanNode(
                    nodes.Count,
                    nodes.Count,
                    node.Id,
                    new GovernedLoopNodeDescriptor(node.Descriptor.Kind, node.Descriptor.TypeId, node.Descriptor.Version),
                    component.ComponentId,
                    component.CycleId,
                    traversalOrdinal,
                    Array.AsReadOnly(incomingEdgeIds),
                    Array.AsReadOnly(outgoingEdgeIds),
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                        new Dictionary<string, string>(node.Parameters, StringComparer.Ordinal)),
                    incomingEdgeIds.Length == 1 ? incomingEdgeIds[0] : null,
                    outgoingEdgeIds.Length == 1 ? outgoingEdgeIds[0] : null));
            }
        }

        return nodes;
    }

    private static bool TryOrderCycleNodes(
        IReadOnlyList<string> componentNodes,
        string entryNodeId,
        GovernedLoopGraphDefinition graph,
        IReadOnlyDictionary<string, int> componentByNodeId,
        int componentIndex,
        out IReadOnlyList<string> ordered)
    {
        var expected = componentNodes.ToHashSet(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>(componentNodes.Count);
        var current = entryNodeId;
        while (visited.Add(current))
        {
            values.Add(current);
            var internalEdges = graph.ControlEdges
                .Where(edge => string.Equals(edge.FromNodeId, current, StringComparison.Ordinal) && componentByNodeId[edge.ToNodeId] == componentIndex)
                .OrderBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray();
            if (internalEdges.Length != 1)
            {
                ordered = Array.Empty<string>();
                return false;
            }

            current = internalEdges[0].ToNodeId;
        }

        if (!string.Equals(current, entryNodeId, StringComparison.Ordinal) || !visited.SetEquals(expected))
        {
            ordered = Array.Empty<string>();
            return false;
        }

        ordered = Array.AsReadOnly(values.ToArray());
        return true;
    }

    private static bool HasExactControlOutcomes(GovernedLoopGraphDefinition graph)
    {
        foreach (var node in graph.Nodes)
        {
            var outgoing = graph.ControlEdges.Where(edge => string.Equals(edge.FromNodeId, node.Id, StringComparison.Ordinal)).ToArray();
            if (GovernedLoopSequentialNodeDescriptors.IsEntryTrigger(node.Descriptor))
            {
                if (outgoing.Length == 0 || outgoing.Any(edge => edge.Condition != GovernedLoopControlCondition.Always))
                {
                    return false;
                }
            }
            else if (node.Descriptor.Kind == GovernedLoopNodeKind.Condition)
            {
                if (outgoing.Count(edge => edge.Condition == GovernedLoopControlCondition.True) != 1
                    || outgoing.Count(edge => edge.Condition == GovernedLoopControlCondition.False) != 1
                    || outgoing.Count(edge => edge.Condition == GovernedLoopControlCondition.Failure) > 1
                    || outgoing.Any(edge => edge.Condition is not (GovernedLoopControlCondition.True or GovernedLoopControlCondition.False or GovernedLoopControlCondition.Failure)))
                {
                    return false;
                }
            }
            else if (Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit)
                || Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.FailTerminal))
            {
                if (outgoing.Length != 0)
                {
                    return false;
                }
            }
            else if (outgoing.Count(edge => edge.Condition == GovernedLoopControlCondition.Success) == 0
                || outgoing.Count(edge => edge.Condition == GovernedLoopControlCondition.Failure) > 1
                || outgoing.Any(edge => edge.Condition == GovernedLoopControlCondition.Failure)
                    && !IsFallibleExecutable(node.Descriptor)
                || outgoing.Any(edge => edge.Condition is not (GovernedLoopControlCondition.Success or GovernedLoopControlCondition.Failure)))
            {
                return false;
            }
        }

        foreach (var node in graph.Nodes.Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Join))
        {
            if (!GovernedLoopTopologyNodeCatalogContract.TryResolve(node.Descriptor, out var descriptor)
                || descriptor is null
                || graph.ControlEdges.Count(edge => string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal)) < descriptor.MinimumIncomingControlEdges)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFallibleExecutable(GovernedLoopNodeDescriptor descriptor)
        => Equals(descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference)
            || GovernedLoopSequentialNodeDescriptors.IsRecoverableAction(descriptor)
            || GovernedLoopSequentialNodeDescriptors.IsPure(descriptor)
            || GovernedLoopSequentialNodeDescriptors.IsWait(descriptor);

    private static bool HasImpossibleJoin(GovernedLoopGraphDefinition graph)
    {
        foreach (var join in graph.Nodes.Where(node => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.AllJoin)))
        {
            var incoming = graph.ControlEdges
                .Where(edge => string.Equals(edge.ToNodeId, join.Id, StringComparison.Ordinal))
                .OrderBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray();
            if (!GovernedLoopControlTopologySemantics.AreAllJoinInputsJointlySatisfiable(graph, join.Id, incoming))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryPositiveBound(GovernedLoopNodeDefinition node, string? parameterId, long maximum, out long value)
    {
        value = default;
        return parameterId is not null
            && node.Parameters.TryGetValue(parameterId, out var raw)
            && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value is >= 1
            && value <= maximum;
    }

    private static bool TryCycleBounds(GovernedLoopNodeDefinition node, out long iterations, out long durationMilliseconds)
    {
        iterations = default;
        durationMilliseconds = default;
        string? iterationParameterId;
        string? durationParameterId;
        if (GovernedLoopTopologyNodeCatalogContract.TryResolve(node.Descriptor, out var descriptor)
            && descriptor is { AllowsCycle: true })
        {
            iterationParameterId = descriptor.CycleIterationBudgetParameterId;
            durationParameterId = descriptor.CycleTimeBudgetMillisecondsParameterId;
        }
        else if (Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
        {
            iterationParameterId = GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter;
            durationParameterId = GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter;
        }
        else
        {
            return false;
        }

        return TryPositiveBound(node, iterationParameterId, CustomLoopLimits.MaxGraphCycleIterations, out iterations)
            && TryPositiveBound(node, durationParameterId, CustomLoopLimits.MaxGraphCycleMilliseconds, out durationMilliseconds);
    }

    private static HashSet<string> Traverse(string start, IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var target in adjacency[current].Reverse())
            {
                pending.Push(target);
            }
        }

        return visited;
    }

    private static IReadOnlyList<IReadOnlyList<string>> StronglyConnectedComponents(
        IEnumerable<string> nodeIds,
        IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var index = 0;
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<IReadOnlyList<string>>();

        void Visit(string nodeId)
        {
            indexes[nodeId] = index;
            lowLinks[nodeId] = index;
            index++;
            stack.Push(nodeId);
            onStack.Add(nodeId);
            foreach (var target in adjacency[nodeId])
            {
                if (!indexes.ContainsKey(target))
                {
                    Visit(target);
                    lowLinks[nodeId] = Math.Min(lowLinks[nodeId], lowLinks[target]);
                }
                else if (onStack.Contains(target))
                {
                    lowLinks[nodeId] = Math.Min(lowLinks[nodeId], indexes[target]);
                }
            }

            if (lowLinks[nodeId] != indexes[nodeId])
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
            while (!string.Equals(current, nodeId, StringComparison.Ordinal));
            components.Add(Array.AsReadOnly(component.Order(StringComparer.Ordinal).ToArray()));
        }

        foreach (var nodeId in nodeIds.Order(StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(nodeId))
            {
                Visit(nodeId);
            }
        }

        return components;
    }

    private static string ComponentIdentity(IReadOnlyList<string> nodeIds)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', nodeIds.Order(StringComparer.Ordinal))));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 12));
    }

    private static string? ExactContractFailurePath(
        GovernedLoopGraphDefinition graph,
        IReadOnlyList<GovernedLoopSequentialPlanNode> planNodes,
        GovernedLoopTopologyAnalysis topology)
    {
        if (!HasExactSchemaSet(graph))
        {
            return "$.graph.valueSchemas";
        }

        var scheduleEntry = Equals(planNodes[0].Descriptor, GovernedLoopSequentialNodeDescriptors.ScheduleTrigger);
        var hasInference = planNodes.Any(node => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference));
        var actionCapabilityIds = graph.Nodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Action)
            .SelectMany(node => node.AuthorityCeiling.CapabilityIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var routedProfileIds = graph.Nodes
            .Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Inference)
            .SelectMany(node => CandidateProfileIds(node.ModelRoutingPolicy ?? graph.DefaultModelRoutingPolicy))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var inferenceCapabilities = new[] { ConversationTurnCapabilityId, ModelInferenceCapabilityId }
            .Concat(scheduleEntry ? [ScheduleTriggerCapabilityId] : [])
            .Concat(routedProfileIds)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var inferenceAllowsWorkspaceTools = graph.Nodes.Any(node => node.Descriptor.Kind == GovernedLoopNodeKind.Inference
            && node.AuthorityCeiling.CapabilityIds.Contains(WorkspaceCommandCapabilityId, StringComparer.Ordinal));
        var expectedCapabilities = inferenceCapabilities
            .Concat(inferenceAllowsWorkspaceTools ? [WorkspaceCommandCapabilityId] : [])
            .Concat(actionCapabilityIds)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var noInferenceCapabilities = new[] { ConversationTurnCapabilityId }
            .Concat(scheduleEntry ? [ScheduleTriggerCapabilityId] : [])
            .Concat(actionCapabilityIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if ((!hasInference
                && !graph.AuthorityCeiling.CapabilityIds.SequenceEqual(noInferenceCapabilities, StringComparer.Ordinal))
            || (hasInference
                && !graph.AuthorityCeiling.CapabilityIds.SequenceEqual(expectedCapabilities, StringComparer.Ordinal)))
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
                GovernedLoopNodeKind.Inference => IsExactInference(node, schemaById, graph.DefaultModelRoutingPolicy, inferenceAllowsWorkspaceTools, topology.ComponentByNodeId[node.Id].IsCyclic),
                GovernedLoopNodeKind.Action => IsExactWorkspaceAction(node, schemaById) || IsExactCommandAction(node, schemaById),
                GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate => IsExactPureNode(node, schemaById),
                GovernedLoopNodeKind.Condition or GovernedLoopNodeKind.Join => IsExactTopologyNode(node, schemaById),
                GovernedLoopNodeKind.Wait => IsExactWaitNode(node),
                GovernedLoopNodeKind.Exit => IsExactExit(node, schemaById),
                GovernedLoopNodeKind.Fail => GovernedLoopFailNodeCatalogContract.HasExactNodeSemantics(
                    node,
                    graph.ControlEdges.Where(edge => string.Equals(edge.ToNodeId, node.Id, StringComparison.Ordinal)).OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray()),
                _ => false,
            };
            if (!exact)
            {
                return "$.graph.nodes";
            }
        }

        if (!HasExactBindings(graph, planNodes, topology))
        {
            return "$.graph.bindings";
        }

        var exitNode = graph.TerminalNodeIds.Select(nodeId => nodeById[nodeId]).Single(node => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit));
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
                || !SchemaTreeIsBounded(schema, schemas, new HashSet<string>(StringComparer.Ordinal), 1))
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
        => (Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger)
                && node.AuthorityCeiling.CapabilityIds.Count == 0
            || Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ScheduleTrigger)
                && node.AuthorityCeiling.CapabilityIds.SequenceEqual([ScheduleTriggerCapabilityId], StringComparer.Ordinal))
            && node.Parameters.Count == 0
            && HasExactPortSet(node, schemas,
                ("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text),
                ("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, GovernedLoopValueKind.Text));

    private static bool IsExactInference(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas,
        GovernedModelRoutingPolicy defaultModelRoutingPolicy,
        bool allowsWorkspaceTools,
        bool isCyclic)
        => node.AuthorityCeiling.CapabilityIds.SequenceEqual(
                new[] { ModelInferenceCapabilityId }
                    .Concat(CandidateProfileIds(node.ModelRoutingPolicy ?? defaultModelRoutingPolicy))
                    .Concat(allowsWorkspaceTools ? [WorkspaceCommandCapabilityId] : [])
                    .Order(StringComparer.Ordinal),
                StringComparer.Ordinal)
            && node.Parameters.Count == (isCyclic ? 3 : 1)
            && node.Parameters.TryGetValue("instruction", out var instruction)
            && !string.IsNullOrWhiteSpace(instruction)
            && (!isCyclic
                || TryPositiveBound(node, GovernedLoopTopologyNodeVocabulary.MaximumIterationsParameter, CustomLoopLimits.MaxGraphCycleIterations, out _)
                && TryPositiveBound(node, GovernedLoopTopologyNodeVocabulary.MaximumDurationMillisecondsParameter, CustomLoopLimits.MaxGraphCycleMilliseconds, out _))
            && HasExactPortSet(node, schemas,
                ("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text),
                ("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context, GovernedLoopValueKind.Text),
                ("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text));

    private static IReadOnlyList<string> CandidateProfileIds(GovernedModelRoutingPolicy policy)
        => (policy.Selector.Kind == GovernedModelSelectorKind.Exact
                ? new[] { policy.Selector.ExactProfileId! }
                : policy.Selector.PermittedInheritedProfileIds)
            .Concat(policy.FallbackProfileIds)
            .Select(value => value.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool IsExactExit(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => node.AuthorityCeiling.CapabilityIds.SequenceEqual([ConversationTurnCapabilityId], StringComparer.Ordinal)
            && node.Parameters.Count == 0
            && HasExactPortSet(node, schemas,
                ("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text),
                ("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text));

    private static bool IsExactWorkspaceAction(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (!WorkspaceActionNodeDescriptors.TryResolve(node.Descriptor, out var kind)
            || !node.AuthorityCeiling.CapabilityIds.SequenceEqual([WorkspaceCommandCapabilityId], StringComparer.Ordinal)
            || node.ModelRoutingPolicy is not null
            || node.AuthoredInputDataClasses is not null
            || node.Parameters.Count != 1
            || !node.Parameters.TryGetValue("input", out var input)
            || !WorkspaceActionInputContract.TryParse(input, kind, out var parsed, out _)
            || !string.Equals(WorkspaceActionInputContract.Encode(parsed!), input, StringComparison.Ordinal))
        {
            return false;
        }

        return HasExactPortSet(node, schemas,
            ("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text));
    }

    private static bool IsExactCommandAction(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => CommandActionNodeDescriptors.IsCommandAction(node.Descriptor)
            && node.AuthorityCeiling.CapabilityIds.Count == 1
            && CapabilityId.TryParse(node.AuthorityCeiling.CapabilityIds[0], out _, out _)
            && node.ModelRoutingPolicy is null
            && node.AuthoredInputDataClasses is null
            && node.Parameters.Count <= CommandActionContractLimits.MaxSlots
            && node.Parameters.All(parameter =>
                CommandActionTemplateContract.IsSlotName(parameter.Key)
                && !parameter.Value.StartsWith('@')
                && CommandActionTemplateContract.IsSafeLiteralToken(
                    parameter.Value,
                    CommandActionContractLimits.MaxValueUtf8Bytes,
                    allowEmpty: true))
            && HasExactPortSet(node, schemas,
                ("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, GovernedLoopValueKind.Text));

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

        return GovernedLoopPureNodeCatalogContract.HasExactSchemaSemantics(node, schemas);
    }

    private static bool IsExactTopologyNode(
        GovernedLoopNodeDefinition node,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
    {
        if (node.AuthorityCeiling.CapabilityIds.Count != 0
            || !GovernedLoopTopologyNodeCatalogContract.TryResolve(node.Descriptor, out var contract)
            || contract is null
            || !HasExactCatalogPorts(node, contract, schemas)
            || !HasExactCatalogParameters(node, contract))
        {
            return false;
        }

        return GovernedLoopTopologyNodeCatalogContract.HasExactSchemaSemantics(node, schemas);
    }

    private static bool IsExactWaitNode(GovernedLoopNodeDefinition node)
    {
        if (node.AuthorityCeiling.CapabilityIds.Count != 0
            || node.Ports.Count != 0
            || !GovernedLoopWaitNodeCatalogContract.TryResolve(node.Descriptor, out var contract)
            || contract is null
            || !HasExactCatalogParameters(node, contract))
        {
            return false;
        }

        var parameterId = node.Descriptor.TypeId == GovernedLoopWaitVocabulary.Timestamp
            ? GovernedLoopWaitVocabulary.DeadlineUtcParameter
            : GovernedLoopWaitVocabulary.EventReferenceParameter;
        return node.Parameters.TryGetValue(parameterId, out var value)
            && GovernedLoopWaitContractValidator.ValidateDescriptor(
                node.Descriptor,
                new Dictionary<string, string>(StringComparer.Ordinal) { [parameterId] = value }).IsValid;
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

    private static bool HasExactCatalogPorts(
        GovernedLoopNodeDefinition node,
        GovernedLoopNodeCatalogDescriptor contract,
        IReadOnlyDictionary<string, GovernedLoopValueSchemaDefinition> schemas)
        => HasExactPurePorts(node, contract, schemas);

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

    private static bool HasExactCatalogParameters(
        GovernedLoopNodeDefinition node,
        GovernedLoopNodeCatalogDescriptor contract)
    {
        var parameters = contract.Parameters.ToDictionary(parameter => parameter.Id, StringComparer.Ordinal);
        if (node.Parameters.Any(parameter => !parameters.TryGetValue(parameter.Key, out var expected) || !IsCompatibleParameter(parameter.Value, expected)))
        {
            return false;
        }

        return contract.Parameters.Where(parameter => parameter.Required).All(parameter => node.Parameters.ContainsKey(parameter.Id));
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
        IReadOnlyList<GovernedLoopSequentialPlanNode> planNodes,
        GovernedLoopTopologyAnalysis topology)
    {
        var expectedInputCount = graph.Nodes.Sum(node => node.Ports.Count(port => port.Direction == GovernedLoopPortDirection.Input));
        if (graph.Bindings.Count != expectedInputCount)
        {
            return false;
        }

        var adjacency = graph.Nodes.ToDictionary(node => node.Id, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var edge in graph.ControlEdges)
        {
            adjacency[edge.FromNodeId].Add(edge.ToNodeId);
        }
        var planByNodeId = planNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);

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
                    || !topology.ComponentByNodeId.TryGetValue(matches[0].FromNodeId, out var sourceComponent)
                    || !topology.ComponentByNodeId.TryGetValue(node.Id, out var targetComponent)
                    || !CanBindAcrossTopology(graph, matches[0].FromNodeId, node.Id, sourceComponent, targetComponent, planByNodeId, adjacency)
                    || !Dominates(graph.EntryNodeId, matches[0].FromNodeId, node.Id, adjacency))
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

    private static bool CanBindAcrossTopology(
        GovernedLoopGraphDefinition graph,
        string sourceNodeId,
        string targetNodeId,
        GovernedLoopTopologyComponent sourceComponent,
        GovernedLoopTopologyComponent targetComponent,
        IReadOnlyDictionary<string, GovernedLoopSequentialPlanNode> planByNodeId,
        IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        var sourcePlanNode = planByNodeId[sourceNodeId];
        var targetPlanNode = planByNodeId[targetNodeId];
        if (string.Equals(sourceComponent.ComponentId, targetComponent.ComponentId, StringComparison.Ordinal))
        {
            return sourceComponent.IsCyclic
                && sourcePlanNode.ComponentTraversalOrdinal < targetPlanNode.ComponentTraversalOrdinal;
        }

        if (sourceComponent.StaticOrdinal >= targetComponent.StaticOrdinal)
        {
            return false;
        }

        if (!sourceComponent.IsCyclic)
        {
            return true;
        }

        var relevantExits = graph.ControlEdges
            .Where(edge => string.Equals(planByNodeId[edge.FromNodeId].ComponentId, sourceComponent.ComponentId, StringComparison.Ordinal)
                && !string.Equals(planByNodeId[edge.ToNodeId].ComponentId, sourceComponent.ComponentId, StringComparison.Ordinal)
                && Traverse(edge.ToNodeId, adjacency).Contains(targetNodeId))
            .ToArray();
        return relevantExits.Length > 0
            && relevantExits.All(edge => planByNodeId[edge.FromNodeId].ComponentTraversalOrdinal > sourcePlanNode.ComponentTraversalOrdinal);
    }

    private static bool Dominates(
        string entryNodeId,
        string sourceNodeId,
        string targetNodeId,
        IReadOnlyDictionary<string, SortedSet<string>> adjacency)
    {
        if (string.Equals(sourceNodeId, entryNodeId, StringComparison.Ordinal))
        {
            return true;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { sourceNodeId };
        var pending = new Stack<string>();
        pending.Push(entryNodeId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (string.Equals(current, targetNodeId, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var successor in adjacency[current].Reverse())
            {
                pending.Push(successor);
            }
        }

        return true;
    }

    private static GovernedLoopSequentialPlanBuildResult Failure(GovernedLoopSequentialPlanBuildStatus status, string path)
        => new(status, null, path);
}
