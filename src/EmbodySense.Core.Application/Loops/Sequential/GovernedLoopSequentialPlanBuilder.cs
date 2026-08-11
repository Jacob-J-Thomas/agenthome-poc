using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Builds one deterministic supported linear plan by traversing canonical control edges from the graph entry.</summary>
public static class GovernedLoopSequentialPlanBuilder
{
    /// <summary>Builds a plan for exactly <c>Manual Trigger -&gt; 1-5 Inference -&gt; Exit</c>.</summary>
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

        if (graph.Nodes.Count is < CustomLoopLimits.MinInferenceSteps + 2 or > CustomLoopLimits.MaxInferenceSteps + 2
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

            if (!isEntry && !isExit && !Equals(current.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
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

    private static GovernedLoopSequentialPlanBuildResult Failure(GovernedLoopSequentialPlanBuildStatus status, string path)
        => new(status, null, path);
}
