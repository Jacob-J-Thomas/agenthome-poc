using System.Text.Json;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Materializes only the exact graph-declared data inputs causally available to one deterministic node activation.</summary>
/// <remarks>The resolver has no provider, effect, authority, filesystem, network, clock, or ambient-context dependency.</remarks>
public static class GovernedLoopSequentialBindingResolver
{
    /// <summary>Resolves the exact graph-pinned inputs for the sole current activation of one deterministic node.</summary>
    public static GovernedLoopSequentialBindingResolutionResult Resolve(
        GovernedLoopGraphRevisionArtifact? artifact,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        CustomLoopRunRecord? run)
    {
        var activation = run?.Frontier?.Payload.Nodes
            .Where(candidate => node is not null
                && candidate.PlanOrdinal == node.Ordinal
                && string.Equals(candidate.NodeId, node.NodeId, StringComparison.Ordinal)
                && candidate.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running)
            .Take(2)
            .ToArray();
        return activation is { Length: 1 }
            ? Resolve(artifact, plan, node, activation[0], run)
            : Rejected("pure-node.activation-invalid", "$.frontier");
    }

    /// <summary>Resolves exact graph-pinned inputs for one immutable durable activation and its causal ancestors.</summary>
    public static GovernedLoopSequentialBindingResolutionResult Resolve(
        GovernedLoopGraphRevisionArtifact? artifact,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        CustomLoopRunRecord? run)
    {
        if (!IsExactContext(artifact, plan, node, activation, run))
        {
            return Rejected("pure-node.context-invalid", "$");
        }

        try
        {
            return ResolveExact(artifact!, plan!, node!, activation!, run!);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return Rejected("pure-node.source-evidence-invalid", "$.bindings");
        }
    }

    private static GovernedLoopSequentialBindingResolutionResult ResolveExact(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialPlan plan,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopRunRecord run)
    {
        var graph = artifact.Graph;
        var graphNode = graph.Nodes.SingleOrDefault(value => string.Equals(value.Id, node.NodeId, StringComparison.Ordinal));
        if (graphNode is null
            || graphNode.Descriptor.Kind is not (GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate or GovernedLoopNodeKind.Condition)
            || graph.Bindings.Any(binding => string.Equals(binding.ToNodeId, graphNode.Id, StringComparison.Ordinal) && binding.Kind != GovernedLoopBindingKind.Data))
        {
            return Rejected("pure-node.binding-invalid", "$.bindings");
        }

        var inputs = new List<GovernedLoopTypedBindingValue>();
        foreach (var binding in graph.Bindings
                     .Where(value => string.Equals(value.ToNodeId, graphNode.Id, StringComparison.Ordinal))
                     .OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            if (!TryResolveSourceValue(artifact, plan, run, activation, binding.FromNodeId, binding.FromPortId, out var value))
            {
                return Rejected("pure-node.source-evidence-invalid", $"$.bindings[{binding.Id}]");
            }

            try
            {
                inputs.Add(GovernedLoopTypedBindingValue.Create(graph, binding.Id, value!));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Rejected("pure-node.binding-schema-mismatch", $"$.bindings[{binding.Id}]");
            }
        }

        return new GovernedLoopSequentialBindingResolutionResult(
            true,
            Array.AsReadOnly(inputs.ToArray()),
            null,
            null);
    }

    private static bool TryResolveSourceValue(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialPlan plan,
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence targetActivation,
        string sourceNodeId,
        string sourcePortId,
        out GovernedLoopTypedValue? value)
    {
        value = null;
        if (!TryResolveCausalSourceActivation(plan, run, targetActivation, sourceNodeId, out var sourceActivation))
        {
            return false;
        }

        var source = plan.Nodes[sourceActivation!.PlanOrdinal];
        if (!HasExactCompletedFrontierEvidence(run, source, sourceActivation, out var outcomeEvent))
        {
            return false;
        }

        if (Equals(source.Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger))
        {
            return string.Equals(sourcePortId, "request", StringComparison.Ordinal)
                && run.SequentialInvocationSnapshot is { TriggerPrompt: { } triggerPrompt }
                && GovernedLoopTypedValue.TryCreate(
                    GovernedLoopTypedValue.CurrentSchemaVersion,
                    GovernedLoopValueKind.Text,
                    JsonSerializer.Serialize(triggerPrompt),
                    out value,
                    out _);
        }

        if (Equals(source.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference))
        {
            return string.Equals(sourcePortId, "result", StringComparison.Ordinal)
                && outcomeEvent is { Kind: CustomLoopRunEventKind.NodeAttemptCompleted, CanonicalOutput: { } output, PureNodeOutcomeJson: null }
                && GovernedLoopTypedValue.TryCreate(
                    GovernedLoopTypedValue.CurrentSchemaVersion,
                    GovernedLoopValueKind.Text,
                    JsonSerializer.Serialize(output),
                    out value,
                    out _);
        }

        if (!GovernedLoopSequentialNodeDescriptors.IsPure(source.Descriptor)
            || outcomeEvent is not { Kind: CustomLoopRunEventKind.NodeAttemptCompleted, PureNodeOutcomeJson: { } outcomeJson })
        {
            return false;
        }

        if (!GovernedLoopPureNodeOutcome.TryDeserialize(artifact.Graph, outcomeJson, out var outcome, out _)
            || outcome is null
            || !string.Equals(outcome.NodeId, source.NodeId, StringComparison.Ordinal))
        {
            return false;
        }

        var outputs = outcome.Outputs.Where(output => string.Equals(output.PortId, sourcePortId, StringComparison.Ordinal)).Take(2).ToArray();
        if (outputs.Length != 1)
        {
            return false;
        }

        value = outputs[0].Value;
        return true;
    }

    private static bool TryResolveCausalSourceActivation(
        GovernedLoopSequentialPlan plan,
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence targetActivation,
        string sourceNodeId,
        out GovernedLoopNodeExecutionEvidence? sourceActivation)
    {
        sourceActivation = null;
        if (run.Frontier?.Payload.Nodes is not { } activations
            || activations.ElementAtOrDefault(targetActivation.ActivationOrdinal) is not { } exactTarget
            || exactTarget.ActivationOrdinal != targetActivation.ActivationOrdinal
            || exactTarget.VisitOrdinal != targetActivation.VisitOrdinal)
        {
            return false;
        }

        var ancestorOrdinals = new HashSet<int>();
        var pending = new Queue<GovernedLoopNodeExecutionEvidence>();
        pending.Enqueue(targetActivation);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!TryResolveDirectPredecessors(plan, activations, current, out var predecessors))
            {
                return false;
            }

            foreach (var predecessor in predecessors)
            {
                if (ancestorOrdinals.Add(predecessor.ActivationOrdinal))
                {
                    pending.Enqueue(predecessor);
                }
            }
        }

        sourceActivation = ancestorOrdinals
            .Select(ordinal => activations[ordinal])
            .Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Completed
                && string.Equals(candidate.NodeId, sourceNodeId, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.ActivationOrdinal)
            .FirstOrDefault();
        return sourceActivation is not null;
    }

    private static bool TryResolveDirectPredecessors(
        GovernedLoopSequentialPlan plan,
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> activations,
        GovernedLoopNodeExecutionEvidence target,
        out IReadOnlyList<GovernedLoopNodeExecutionEvidence> predecessors)
    {
        var resolved = new List<GovernedLoopNodeExecutionEvidence>();
        if (target.JoinArrivals.Count > 0)
        {
            foreach (var arrival in target.JoinArrivals)
            {
                if (activations.ElementAtOrDefault(arrival.SourceActivationOrdinal) is not { } source
                    || source.ActivationOrdinal >= target.ActivationOrdinal
                    || source.Status != GovernedLoopNodeExecutionStatus.Completed
                    || !source.SelectedControlEdgeIds.Contains(arrival.ControlEdgeId, StringComparer.Ordinal)
                    || !EdgeReachesActivation(plan, source, target, arrival.ControlEdgeId))
                {
                    predecessors = [];
                    return false;
                }

                resolved.Add(source);
            }

            predecessors = resolved.DistinctBy(candidate => candidate.ActivationOrdinal).ToArray();
            return true;
        }

        foreach (var edgeId in target.IncomingControlEdgeIds)
        {
            var matches = activations
                .Take(target.ActivationOrdinal)
                .Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Completed
                    && candidate.SelectedControlEdgeIds.Contains(edgeId, StringComparer.Ordinal)
                    && EdgeReachesActivation(plan, candidate, target, edgeId))
                .OrderByDescending(candidate => candidate.ActivationOrdinal)
                .Take(2)
                .ToArray();
            if (matches.Length == 0)
            {
                predecessors = [];
                return false;
            }

            resolved.Add(matches[0]);
        }

        predecessors = resolved.DistinctBy(candidate => candidate.ActivationOrdinal).ToArray();
        return true;
    }

    private static bool EdgeReachesActivation(
        GovernedLoopSequentialPlan plan,
        GovernedLoopNodeExecutionEvidence source,
        GovernedLoopNodeExecutionEvidence target,
        string edgeId)
    {
        if (plan.ControlEdges.SingleOrDefault(edge => string.Equals(edge.Id, edgeId, StringComparison.Ordinal)) is not { } edge
            || !string.Equals(edge.FromNodeId, source.NodeId, StringComparison.Ordinal)
            || !string.Equals(edge.ToNodeId, target.NodeId, StringComparison.Ordinal))
        {
            return false;
        }

        var sourcePlan = plan.Nodes[source.PlanOrdinal];
        var targetPlan = plan.Nodes[target.PlanOrdinal];
        if (targetPlan.CycleId is null)
        {
            return target.CycleIteration is null;
        }

        if (!string.Equals(sourcePlan.CycleId, targetPlan.CycleId, StringComparison.Ordinal))
        {
            return target.CycleIteration == 1;
        }

        if (source.CycleIteration is not { } sourceIteration)
        {
            return false;
        }

        var expected = targetPlan.ComponentTraversalOrdinal > sourcePlan.ComponentTraversalOrdinal
            ? sourceIteration
            : checked(sourceIteration + 1);
        return target.CycleIteration == expected;
    }

    private static bool HasExactCompletedFrontierEvidence(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode source,
        GovernedLoopNodeExecutionEvidence sourceActivation,
        out CustomLoopRunEvent? outcomeEvent)
    {
        outcomeEvent = null;
        if (run.Frontier?.Payload.Nodes.ElementAtOrDefault(sourceActivation.ActivationOrdinal) is not
            {
                Status: GovernedLoopNodeExecutionStatus.Completed,
                Attempt: { } attempt,
                OutcomeEvidenceId: { } evidenceId,
                OutcomeEvidenceHash: { } evidenceHash,
            })
        {
            return false;
        }

        var matches = run.Events.Where(item => string.Equals(item.EventId, evidenceId, StringComparison.Ordinal)
            && item.SequentialNodeEvidence is
            {
                Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
                Disposition: CustomLoopSequentialNodeDisposition.Completed,
            } evidence
            && evidence.ActivationOrdinal == sourceActivation.ActivationOrdinal
            && evidence.VisitOrdinal == sourceActivation.VisitOrdinal
            && string.Equals(evidence.NodeId, source.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
            && string.Equals(evidence.CycleId, sourceActivation.CycleId, StringComparison.Ordinal)
            && evidence.CycleIteration == sourceActivation.CycleIteration
            && string.Equals(evidence.OutcomeArtifactHash, evidenceHash, StringComparison.Ordinal)
            && CustomLoopSequentialNodeEvidenceHash.Matches(evidence)
            && CustomLoopSequentialOutcomeArtifactHash.Matches(item)).Take(2).ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        outcomeEvent = matches[0];
        return true;
    }

    private static bool IsExactContext(
        GovernedLoopGraphRevisionArtifact? artifact,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        CustomLoopRunRecord? run)
    {
        if (artifact is null
            || plan is null
            || node is null
            || activation is null
            || run is null
            || run.SequentialAdapterBinding is not { } binding
            || run.SequentialInvocationSnapshot is not { } snapshot)
        {
            return false;
        }

        try
        {
            return string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal)
                && GovernedLoopSequentialContractValidator.Validate(binding).IsValid
                && GovernedLoopSequentialContractValidator.Validate(snapshot).IsValid
                && CustomLoopRunValidator.ValidateForDispatch(run).IsValid
                && Equals(plan.Revision, artifact.RevisionArtifact.Revision)
                && string.Equals(plan.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
                && string.Equals(plan.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
                && Equals(binding.ExecutionBinding.Revision, artifact.RevisionArtifact.Revision)
                && string.Equals(binding.InvocationPayloadHash, snapshot.ContentHash, StringComparison.Ordinal)
                && plan.Nodes.Count(item => string.Equals(item.NodeId, node.NodeId, StringComparison.Ordinal)) == 1
                && Equals(plan.Nodes[node.Ordinal].Descriptor, node.Descriptor)
                && string.Equals(plan.Nodes[node.Ordinal].NodeId, node.NodeId, StringComparison.Ordinal)
                && activation.PlanOrdinal == node.Ordinal
                && string.Equals(activation.NodeId, node.NodeId, StringComparison.Ordinal)
                && activation.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running
                && run.Frontier?.Payload.Nodes.ElementAtOrDefault(activation.ActivationOrdinal) is { } exactActivation
                && exactActivation.ActivationOrdinal == activation.ActivationOrdinal
                && exactActivation.VisitOrdinal == activation.VisitOrdinal
                && exactActivation.CycleIteration == activation.CycleIteration
                && string.Equals(exactActivation.CycleId, activation.CycleId, StringComparison.Ordinal)
                && artifact.Graph.Nodes.SingleOrDefault(item => string.Equals(item.Id, node.NodeId, StringComparison.Ordinal)) is { } graphNode
                && Equals(graphNode.Descriptor, node.Descriptor)
                && string.Equals(binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
                && string.Equals(binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
                && string.Equals(binding.ExecutionBinding.RunId, run.Id, StringComparison.Ordinal)
                && GovernedLoopSequentialFrontierMachine.Validate(run.Frontier, binding, plan);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    private static GovernedLoopSequentialBindingResolutionResult Rejected(string code, string path)
        => new(false, Array.Empty<GovernedLoopTypedBindingValue>(), code, path);
}
