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

/// <summary>Materializes only the exact graph-declared data inputs available to one deterministic pure node.</summary>
/// <remarks>The resolver has no provider, effect, authority, filesystem, network, clock, or ambient-context dependency.</remarks>
public static class GovernedLoopSequentialBindingResolver
{
    /// <summary>Resolves the exact graph-pinned inputs for one Transform or Validate node.</summary>
    public static GovernedLoopSequentialBindingResolutionResult Resolve(
        GovernedLoopGraphRevisionArtifact? artifact,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        CustomLoopRunRecord? run)
    {
        if (!IsExactContext(artifact, plan, node, run))
        {
            return Rejected("pure-node.context-invalid", "$");
        }

        try
        {
            return ResolveExact(artifact!, plan!, node!, run!);
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
        CustomLoopRunRecord run)
    {
        var graph = artifact.Graph;
        var graphNode = graph.Nodes.SingleOrDefault(value => string.Equals(value.Id, node.NodeId, StringComparison.Ordinal));
        if (graphNode is null
            || graphNode.Descriptor.Kind is not (GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate)
            || graph.Bindings.Any(binding => string.Equals(binding.ToNodeId, graphNode.Id, StringComparison.Ordinal) && binding.Kind != GovernedLoopBindingKind.Data))
        {
            return Rejected("pure-node.binding-invalid", "$.bindings");
        }

        var ordinals = plan.Nodes.ToDictionary(value => value.NodeId, value => value.Ordinal, StringComparer.Ordinal);
        var inputs = new List<GovernedLoopTypedBindingValue>();
        foreach (var binding in graph.Bindings
                     .Where(value => string.Equals(value.ToNodeId, graphNode.Id, StringComparison.Ordinal))
                     .OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            if (!ordinals.TryGetValue(binding.FromNodeId, out var sourceOrdinal)
                || sourceOrdinal >= node.Ordinal
                || !TryResolveSourceValue(artifact, plan, run, binding.FromNodeId, binding.FromPortId, out var value))
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

        return GovernedLoopSequentialBindingResolutionResult.Resolved(inputs.ToArray());
    }

    private static bool TryResolveSourceValue(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialPlan plan,
        CustomLoopRunRecord run,
        string sourceNodeId,
        string sourcePortId,
        out GovernedLoopTypedValue? value)
    {
        value = null;
        var source = plan.Nodes.SingleOrDefault(item => string.Equals(item.NodeId, sourceNodeId, StringComparison.Ordinal));
        if (source is null || !HasExactCompletedFrontierEvidence(run, source, out var outcomeEvent))
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

    private static bool HasExactCompletedFrontierEvidence(
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode source,
        out CustomLoopRunEvent? outcomeEvent)
    {
        outcomeEvent = null;
        if (run.Frontier?.Payload.Nodes.SingleOrDefault(item => string.Equals(item.NodeId, source.NodeId, StringComparison.Ordinal)) is not
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
            && string.Equals(evidence.NodeId, source.NodeId, StringComparison.Ordinal)
            && evidence.Attempt == attempt
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
        CustomLoopRunRecord? run)
    {
        if (artifact is null
            || plan is null
            || node is null
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
        => GovernedLoopSequentialBindingResolutionResult.Rejected(code, path);
}
