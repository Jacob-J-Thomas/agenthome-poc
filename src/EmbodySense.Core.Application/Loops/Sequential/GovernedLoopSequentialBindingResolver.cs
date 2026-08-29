using System.Text.Json;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Materializes only the exact graph-declared data inputs causally available to one deterministic node activation.</summary>
/// <remarks>The resolver has no provider, effect, authority, filesystem, clock, or ambient-context dependency. Human Input values are read only through the exact checkpoint-bound application port.</remarks>
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
            : Rejected("canonical-binding.activation-invalid", "$.frontier");
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
            return Rejected("canonical-binding.context-invalid", "$");
        }

        try
        {
            return ResolveExact(artifact!, plan!, node!, activation!, run!);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return Rejected("canonical-binding.source-evidence-invalid", "$.bindings");
        }
    }

    internal static async Task<GovernedLoopSequentialBindingResolutionResult> ResolveAsync(
        GovernedLoopGraphRevisionArtifact? artifact,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        CustomLoopRunRecord? run,
        GovernedLoopSequentialHumanInputBindingCache humanInputBindings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(humanInputBindings);
        if (!IsExactContext(artifact, plan, node, activation, run))
        {
            return Rejected("canonical-binding.context-invalid", "$");
        }

        try
        {
            return await ResolveExactAsync(artifact!, plan!, node!, activation!, run!, humanInputBindings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return Rejected("canonical-binding.source-evidence-invalid", "$.bindings");
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
            || graphNode.Descriptor.Kind is not (GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate or GovernedLoopNodeKind.Condition or GovernedLoopNodeKind.Inference or GovernedLoopNodeKind.Action or GovernedLoopNodeKind.Exit))
        {
            return Rejected("canonical-binding.node-invalid", "$.bindings");
        }

        var inputs = new List<GovernedLoopTypedBindingValue>();
        foreach (var binding in graph.Bindings
                     .Where(value => string.Equals(value.ToNodeId, graphNode.Id, StringComparison.Ordinal))
                     .OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            var resolved = binding.Kind switch
            {
                GovernedLoopBindingKind.Data => TryResolveSourceValue(artifact, plan, run, activation, binding.FromNodeId, binding.FromPortId, out var value)
                    ? value
                    : null,
                GovernedLoopBindingKind.Context => TryResolveInvocationContextValue(artifact, plan, run, activation, binding, out var contextValue)
                    ? contextValue
                    : null,
                _ => null,
            };
            if (resolved is null)
            {
                return Rejected("canonical-binding.source-evidence-invalid", $"$.bindings[{binding.Id}]");
            }

            try
            {
                inputs.Add(GovernedLoopTypedBindingValue.Create(graph, binding.Id, resolved));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Rejected("canonical-binding.schema-mismatch", $"$.bindings[{binding.Id}]");
            }
        }

        return Resolved(inputs, requiresHumanInputBinding: false);
    }

    private static async Task<GovernedLoopSequentialBindingResolutionResult> ResolveExactAsync(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialPlan plan,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopRunRecord run,
        GovernedLoopSequentialHumanInputBindingCache humanInputBindings,
        CancellationToken cancellationToken)
    {
        var graph = artifact.Graph;
        var graphNode = graph.Nodes.SingleOrDefault(value => string.Equals(value.Id, node.NodeId, StringComparison.Ordinal));
        if (graphNode is null
            || graphNode.Descriptor.Kind is not (GovernedLoopNodeKind.Transform or GovernedLoopNodeKind.Validate or GovernedLoopNodeKind.Condition or GovernedLoopNodeKind.Inference or GovernedLoopNodeKind.Action or GovernedLoopNodeKind.Exit))
        {
            return Rejected("canonical-binding.node-invalid", "$.bindings");
        }

        var inputs = new List<GovernedLoopTypedBindingValue>();
        var requiresHumanInputBinding = false;
        foreach (var binding in graph.Bindings
                     .Where(value => string.Equals(value.ToNodeId, graphNode.Id, StringComparison.Ordinal))
                     .OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            var source = binding.Kind == GovernedLoopBindingKind.Data
                ? await ResolveSourceValueAsync(artifact, plan, run, activation, binding.FromNodeId, binding.FromPortId, humanInputBindings, cancellationToken).ConfigureAwait(false)
                : default;
            requiresHumanInputBinding |= source.RequiresHumanInputBinding;
            var resolved = binding.Kind switch
            {
                GovernedLoopBindingKind.Data when source.Status == GovernedLoopSequentialBindingResolutionStatus.Resolved => source.Value,
                GovernedLoopBindingKind.Data when source.Status == GovernedLoopSequentialBindingResolutionStatus.Unavailable => null,
                GovernedLoopBindingKind.Context => TryResolveInvocationContextValue(artifact, plan, run, activation, binding, out var contextValue)
                    ? contextValue
                    : null,
                _ => null,
            };
            if (binding.Kind == GovernedLoopBindingKind.Data && source.Status == GovernedLoopSequentialBindingResolutionStatus.Unavailable)
            {
                return Unavailable("canonical-binding.human-input-unavailable", $"$.bindings[{binding.Id}]", requiresHumanInputBinding);
            }
            if (resolved is null)
            {
                return Rejected(
                    source.RequiresHumanInputBinding ? "canonical-binding.human-input-invalid" : "canonical-binding.source-evidence-invalid",
                    $"$.bindings[{binding.Id}]",
                    requiresHumanInputBinding);
            }

            try
            {
                inputs.Add(GovernedLoopTypedBindingValue.Create(graph, binding.Id, resolved));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Rejected("canonical-binding.schema-mismatch", $"$.bindings[{binding.Id}]", requiresHumanInputBinding);
            }
        }

        return Resolved(inputs, requiresHumanInputBinding);
    }

    private static bool TryResolveInvocationContextValue(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialPlan plan,
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence targetActivation,
        GovernedLoopBindingDefinition binding,
        out GovernedLoopTypedValue? value)
    {
        value = null;
        if (!string.Equals(binding.FromNodeId, plan.Nodes[0].NodeId, StringComparison.Ordinal)
            || !string.Equals(binding.FromPortId, "invocation-context", StringComparison.Ordinal)
            || !string.Equals(binding.ToPortId, "invocation-context", StringComparison.Ordinal)
            || run.SequentialInvocationSnapshot is not { } snapshot
            || !GovernedLoopSequentialContractValidator.Validate(snapshot).IsValid
            || !CustomLoopContextSnapshotHash.Matches(run.ContextSnapshot)
            || snapshot.ContextCapturedAtUtc != run.ContextSnapshot.CapturedAtUtc
            || !snapshot.ContextManifest.SequenceEqual(run.ContextSnapshot.SourceManifest)
            || !TryResolveCausalSourceActivation(plan, run, targetActivation, binding.FromNodeId, out var triggerActivation)
            || triggerActivation is null
            || !HasExactCompletedFrontierEvidence(run, plan.Nodes[triggerActivation.PlanOrdinal], triggerActivation, out _))
        {
            return false;
        }

        return GovernedLoopTypedValue.TryCreate(
            GovernedLoopTypedValue.CurrentSchemaVersion,
            GovernedLoopValueKind.Text,
            JsonSerializer.Serialize(snapshot.ContentHash),
            out value,
            out _);
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

        if (GovernedLoopSequentialNodeDescriptors.IsEntryTrigger(source.Descriptor))
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

        if (GovernedLoopSequentialNodeDescriptors.IsWorkspaceAction(source.Descriptor))
        {
            return string.Equals(sourcePortId, "result", StringComparison.Ordinal)
                && outcomeEvent is { Kind: CustomLoopRunEventKind.NodeAttemptCompleted, CanonicalOutput: { } actionResult, PureNodeOutcomeJson: null }
                && EmbodySense.Core.Common.LocalWorkspace.Actions.WorkspaceActionResultContract.TryParse(actionResult, out _)
                && GovernedLoopTypedValue.TryCreate(
                    GovernedLoopTypedValue.CurrentSchemaVersion,
                    GovernedLoopValueKind.Text,
                    JsonSerializer.Serialize(actionResult),
                    out value,
                    out _);
        }

        if (GovernedLoopSequentialNodeDescriptors.IsCommandAction(source.Descriptor))
        {
            return string.Equals(sourcePortId, "result", StringComparison.Ordinal)
                && outcomeEvent is { Kind: CustomLoopRunEventKind.NodeAttemptCompleted, CanonicalOutput: { } commandResult, PureNodeOutcomeJson: null }
                && CommandActionResultContract.TryParse(commandResult, out var parsed)
                && parsed!.Outcome == CommandActionResultOutcome.Succeeded
                && GovernedLoopTypedValue.TryCreate(
                    GovernedLoopTypedValue.CurrentSchemaVersion,
                    GovernedLoopValueKind.Text,
                    JsonSerializer.Serialize(commandResult),
                    out value,
                    out _);
        }

        if (GovernedLoopSequentialNodeDescriptors.IsHumanInput(source.Descriptor))
        {
            return false;
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

    private static async Task<(GovernedLoopSequentialBindingResolutionStatus Status, GovernedLoopTypedValue? Value, bool RequiresHumanInputBinding)> ResolveSourceValueAsync(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopSequentialPlan plan,
        CustomLoopRunRecord run,
        GovernedLoopNodeExecutionEvidence targetActivation,
        string sourceNodeId,
        string sourcePortId,
        GovernedLoopSequentialHumanInputBindingCache humanInputBindings,
        CancellationToken cancellationToken)
    {
        if (!TryResolveCausalSourceActivation(plan, run, targetActivation, sourceNodeId, out var sourceActivation)
            || sourceActivation is null)
        {
            return (GovernedLoopSequentialBindingResolutionStatus.Invalid, null, false);
        }

        var source = plan.Nodes[sourceActivation.PlanOrdinal];
        if (!HasExactCompletedFrontierEvidence(run, source, sourceActivation, out var outcomeEvent))
        {
            return (GovernedLoopSequentialBindingResolutionStatus.Invalid, null, false);
        }

        if (!GovernedLoopSequentialNodeDescriptors.IsHumanInput(source.Descriptor))
        {
            return TryResolveSourceValue(artifact, plan, run, targetActivation, sourceNodeId, sourcePortId, out var value)
                ? (GovernedLoopSequentialBindingResolutionStatus.Resolved, value, false)
                : (GovernedLoopSequentialBindingResolutionStatus.Invalid, null, false);
        }

        if (!TryFindExactTerminalHumanInputCheckpoint(artifact, run, source, sourceActivation, outcomeEvent, sourcePortId, out var checkpoint)
            || checkpoint is null)
        {
            return (GovernedLoopSequentialBindingResolutionStatus.Invalid, null, true);
        }

        var resolved = await humanInputBindings.ResolveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        if (resolved.Status == GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable)
        {
            return (GovernedLoopSequentialBindingResolutionStatus.Unavailable, null, true);
        }
        if (resolved.Status != GovernedLoopSequentialHumanInputBindingReadStatus.Ready
            || !BindingMatchesCheckpoint(resolved.Binding, checkpoint))
        {
            return (GovernedLoopSequentialBindingResolutionStatus.Invalid, null, true);
        }

        return (GovernedLoopSequentialBindingResolutionStatus.Resolved, resolved.Binding!.Value, true);
    }

    private static bool TryFindExactTerminalHumanInputCheckpoint(
        GovernedLoopGraphRevisionArtifact artifact,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode source,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopRunEvent? outcomeEvent,
        string sourcePortId,
        out GovernedLoopHumanInputWaitingCheckpoint? checkpoint)
    {
        checkpoint = null;
        var graphNode = artifact.Graph.Nodes.SingleOrDefault(candidate => string.Equals(candidate.Id, source.NodeId, StringComparison.Ordinal));
        var matches = run.HumanInputWaitingCheckpoints.Where(candidate => candidate is not null
            && candidate.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal
            && candidate.Binding.ActivationOrdinal == activation.ActivationOrdinal
            && candidate.Binding.NodeVisitOrdinal == activation.VisitOrdinal
            && string.Equals(candidate.Binding.NodeId, activation.NodeId, StringComparison.Ordinal)
            && string.Equals(candidate.Binding.CycleId, activation.CycleId, StringComparison.Ordinal)
            && candidate.Binding.CycleIteration == activation.CycleIteration).Take(2).ToArray();
        if (graphNode is null
            || !GovernedLoopSequentialNodeDescriptors.IsHumanInput(graphNode.Descriptor)
            || !string.Equals(sourcePortId, GovernedLoopHumanInputVocabulary.ResponsePortId, StringComparison.Ordinal)
            || matches.Length != 1
            || outcomeEvent is not { Kind: CustomLoopRunEventKind.NodeAttemptCompleted, SequentialNodeEvidence: { Kind: CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, Disposition: CustomLoopSequentialNodeDisposition.Completed } evidence }
            || !string.Equals(activation.OutcomeEvidenceId, outcomeEvent.EventId, StringComparison.Ordinal)
            || !string.Equals(activation.OutcomeEvidenceHash, evidence.OutcomeArtifactHash, StringComparison.Ordinal)
            || !string.Equals(matches[0].Binding.Execution.RunId, run.Id, StringComparison.Ordinal)
            || !string.Equals(matches[0].Binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(matches[0].Binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
            || !string.Equals(matches[0].Request.Binding.NodeId, source.NodeId, StringComparison.Ordinal)
            || !string.Equals(matches[0].Request.Binding.RunId, run.Id, StringComparison.Ordinal)
            || !string.Equals(matches[0].Request.Binding.CheckpointId, matches[0].Binding.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(JsonSerializer.Serialize(matches[0].NodeConfiguration), JsonSerializer.Serialize(graphNode.HumanInputConfiguration), StringComparison.Ordinal)
            || matches[0].Evidence is not
        [
        { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published },
        { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, AnswerSelection: not null },
        { Kind: GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized, TerminalizationReceiptHash: not null },
        ])
        {
            return false;
        }

        checkpoint = matches[0];
        return true;
    }

    private static bool BindingMatchesCheckpoint(
        GovernedLoopSequentialHumanInputBinding? binding,
        GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        if (binding is null)
        {
            return false;
        }

        var selection = checkpoint.Evidence.Length > 1 ? checkpoint.Evidence[1].AnswerSelection : null;
        return binding.SchemaVersion == GovernedLoopSequentialHumanInputBinding.CurrentSchemaVersion
            && string.Equals(binding.CheckpointId, checkpoint.Binding.CheckpointId, StringComparison.Ordinal)
            && selection is not null
            && HumanInputResponseSelectionHash.Matches(binding.Selection)
            && Equals(HumanInputResponseSelectionReference.Create(binding.Selection), selection)
            && binding.Selection.Responses.Any(reference => Equals(reference, binding.Response))
            && GovernedLoopTypedValue.TryDeserialize(binding.Value.CanonicalJson, out var canonical, out _)
            && Equals(canonical, binding.Value);
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
        if (target.IncomingControlEdgeIds.Count == 0)
        {
            predecessors = [];
            return target.ActivationOrdinal == 0
                && target.PlanOrdinal == 0
                && GovernedLoopSequentialNodeDescriptors.IsEntryTrigger(plan.Nodes[0].Descriptor);
        }

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

        var directMatches = target.IncomingControlEdgeIds
            .SelectMany(edgeId => activations
                .Take(target.ActivationOrdinal)
                .Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Completed
                    && candidate.SelectedControlEdgeIds.Contains(edgeId, StringComparer.Ordinal)
                    && EdgeReachesActivation(plan, candidate, target, edgeId))
                .Select(candidate => (EdgeId: edgeId, Activation: candidate)))
            .OrderByDescending(candidate => candidate.Activation.ActivationOrdinal)
            .Take(2)
            .ToArray();
        if (directMatches.Length != 1)
        {
            predecessors = [];
            return false;
        }

        predecessors = [directMatches[0].Activation];
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

    private static GovernedLoopSequentialBindingResolutionResult Resolved(
        IReadOnlyList<GovernedLoopTypedBindingValue> inputs,
        bool requiresHumanInputBinding)
        => new(
            GovernedLoopSequentialBindingResolutionStatus.Resolved,
            Array.AsReadOnly(inputs.ToArray()),
            null,
            null,
            requiresHumanInputBinding);

    private static GovernedLoopSequentialBindingResolutionResult Rejected(
        string code,
        string path,
        bool requiresHumanInputBinding = false)
        => new(
            GovernedLoopSequentialBindingResolutionStatus.Invalid,
            Array.Empty<GovernedLoopTypedBindingValue>(),
            code,
            path,
            requiresHumanInputBinding);

    private static GovernedLoopSequentialBindingResolutionResult Unavailable(
        string code,
        string path,
        bool requiresHumanInputBinding)
        => new(
            GovernedLoopSequentialBindingResolutionStatus.Unavailable,
            Array.Empty<GovernedLoopTypedBindingValue>(),
            code,
            path,
            requiresHumanInputBinding);
}
