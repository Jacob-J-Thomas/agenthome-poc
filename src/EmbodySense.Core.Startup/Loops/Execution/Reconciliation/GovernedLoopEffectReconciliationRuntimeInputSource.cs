using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Startup.Loops.Execution.Effects;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationRuntimeInputSource : IGovernedLoopEffectReconciliationInputSource
{
    private readonly CommandActionRegistrationRegistry? _commands;
    private readonly IGovernedLoopEffectAttemptReadStore _effects;
    private readonly IGovernedLoopGraphRevisionStore _graphs;
    private readonly ICustomLoopRunStore _runs;
    private readonly GovernedActuatorOperationRegistry? _workspaceActions;

    internal GovernedLoopEffectReconciliationRuntimeInputSource(
        ICustomLoopRunStore runs,
        IGovernedLoopGraphRevisionStore graphs,
        IGovernedLoopEffectAttemptReadStore effects,
        CommandActionRegistrationRegistry? commands,
        GovernedActuatorOperationRegistry? workspaceActions = null)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _graphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        _commands = commands;
        _workspaceActions = workspaceActions;
    }

    public async Task<GovernedLoopEffectReconciliationInputReadResult> ReadAsync(GovernedLoopEffectReconciliationInputReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        CustomLoopRunRecord? run;
        try
        {
            run = await _runs.GetAsync(request.Binding.Execution.RunId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Corrupt);
        }
        catch
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
        }

        if (run is null)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.NotFound);
        }
        if (!CustomLoopRunValidator.Validate(run).IsValid)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Corrupt);
        }
        if (!TryMatchRun(run, request.Binding, out var frontierNode))
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Conflict);
        }

        GovernedLoopEffectAttemptReadResult effectRead;
        try
        {
            effectRead = await _effects.ReadAsync(request.Binding.WorkspaceId, request.Binding.OperationId, request.Binding.EffectGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
        }

        if (effectRead.Status == GovernedLoopEffectAttemptReadStatus.Missing)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.NotFound);
        }
        if (effectRead.Status == GovernedLoopEffectAttemptReadStatus.Corrupt)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Corrupt);
        }
        if (effectRead.Status != GovernedLoopEffectAttemptReadStatus.Current || effectRead.Attempt is null)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
        }
        if (!MatchesEffect(request.Binding, effectRead.Attempt))
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Conflict);
        }

        EmbodySense.Core.Application.Loops.GraphAuthoring.Models.GovernedLoopGraphRevisionArtifactReadResult graphRead;
        try
        {
            graphRead = await _graphs.ReadArtifactAsync(request.Binding.Execution.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
        }

        if (graphRead.Status == GovernedLoopRevisionStoreReadStatus.NotFound)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.NotFound);
        }
        if (graphRead.Status is GovernedLoopRevisionStoreReadStatus.Ambiguous)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Corrupt);
        }
        if (graphRead.Status != GovernedLoopRevisionStoreReadStatus.Ready || graphRead.Artifact is null)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Unavailable);
        }

        var adapter = run.SequentialAdapterBinding!;
        var artifact = graphRead.Artifact;
        var graphNodes = artifact.Graph.Nodes.Where(node => string.Equals(node.Id, request.Binding.NodeId, StringComparison.Ordinal)).Take(2).ToArray();
        if (!string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(artifact.ArtifactHash, adapter.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(artifact.LayoutHash, adapter.GraphLayoutHash, StringComparison.Ordinal)
            || graphNodes.Length != 1
            || !Equals(graphNodes[0].Descriptor, frontierNode!.Descriptor)
            || !TryCreateInput(graphNodes[0], out var input)
            || !string.Equals(input!.Fingerprint, effectRead.Attempt.InputFingerprint, StringComparison.Ordinal))
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Conflict);
        }

        try
        {
            return new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Found, request.Case, request.Binding, effectRead.Attempt, run.Frontier, input);
        }
        catch (ArgumentException)
        {
            return Closed(GovernedLoopEffectReconciliationInputReadStatus.Corrupt);
        }
    }

    private static bool TryMatchRun(CustomLoopRunRecord run, EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationBinding binding, out GovernedLoopNodeExecutionEvidence? frontierNode)
    {
        frontierNode = null;
        var adapter = run.SequentialAdapterBinding;
        var frontier = run.Frontier;
        if (adapter is null
            || frontier is null
            || run.Status != CustomLoopRunStatus.NeedsReview
            || frontier.Payload.Status != GovernedLoopFrontierStatus.ReviewBlocked
            || !string.Equals(run.Id, binding.Execution.RunId, StringComparison.Ordinal)
            || !string.Equals(adapter.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !Equals(adapter.ExecutionBinding, binding.Execution)
            || !Equals(frontier.Binding, binding.Execution)
            || !string.Equals(frontier.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(frontier.GraphArtifactHash, adapter.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(frontier.GraphLayoutHash, adapter.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(frontier.AdmissionReceiptHash, adapter.AdmissionReceiptHash, StringComparison.Ordinal))
        {
            return false;
        }

        var matches = frontier.Payload.Nodes.Where(node => node.ActivationOrdinal == binding.ActivationOrdinal
            && node.VisitOrdinal == binding.VisitOrdinal
            && string.Equals(node.NodeId, binding.NodeId, StringComparison.Ordinal)
            && node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked
            && node.Attempt == binding.NodeAttempt).Take(2).ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        frontierNode = matches[0];
        return true;
    }

    private static bool MatchesEffect(EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationBinding binding, EmbodySense.Core.Common.Loops.Execution.Effects.Models.GovernedLoopEffectAttempt effect)
        => string.Equals(effect.ContentHash, binding.CurrentAttemptHash, StringComparison.Ordinal)
            && effect.Payload.Phase == GovernedLoopEffectPhase.ReconciliationRequired
            && Equals(effect.Binding, binding.Execution)
            && string.Equals(effect.NodeId, binding.NodeId, StringComparison.Ordinal)
            && effect.NodeAttempt == binding.NodeAttempt
            && string.Equals(effect.Payload.EffectId, binding.EffectId, StringComparison.Ordinal)
            && string.Equals(effect.Payload.OperationId, binding.OperationId, StringComparison.Ordinal)
            && effect.Payload.EffectGeneration == binding.EffectGeneration
            && string.Equals(effect.Payload.IntentHash, binding.IntentHash, StringComparison.Ordinal);

    private static bool TryCreateInput(IReadOnlyDictionary<string, string> parameters, EmbodySense.Core.Application.CommandActions.Models.CommandActionRegistration registration, out GovernedActuatorInputEvidence? input)
    {
        input = null;
        if (parameters.Count != registration.Template.Slots.Count || registration.Template.Slots.Any(slot => !parameters.ContainsKey(slot.Name)))
        {
            return false;
        }

        try
        {
            var commandInput = new CommandActionInput(
                1,
                registration.Template.TemplateId,
                registration.Template.TemplateVersion,
                registration.Template.ContentHash,
                Array.AsReadOnly(registration.Template.Slots.Select(slot => new CommandActionSlotValue(slot.Name, slot.Kind, parameters[slot.Name])).ToArray()));
            var canonical = CommandActionInputContract.Encode(commandInput, registration.Template);
            return GovernedActuatorInputContract.TryCanonicalize(canonical, out input, out _);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool TryCreateInput(GovernedLoopNodeDefinition node, out GovernedActuatorInputEvidence? input)
    {
        input = null;
        GovernedActuatorInputEvidence? commandInput = null;
        GovernedActuatorInputEvidence? workspaceEvidence = null;
        var commandMatched = _commands is not null
            && _commands.TryResolve(node.Descriptor, out var command)
            && command is not null
            && TryCreateInput(node.Parameters, command, out commandInput);
        var workspaceKindMatched = WorkspaceActionNodeDescriptors.TryResolve(node.Descriptor, out var kind);
        var workspaceDescriptors = workspaceKindMatched && _workspaceActions is not null
            ? _workspaceActions.Descriptors.Where(descriptor => string.Equals(descriptor.OperationId, WorkspaceActionOperationIds.For(kind), StringComparison.Ordinal)).Take(2).ToArray()
            : [];
        var workspaceMatched = _workspaceActions is not null
            && workspaceDescriptors.Length == 1
            && _workspaceActions.TryResolve(workspaceDescriptors[0], out var workspaceOperation)
            && workspaceOperation is not null
            && node.Parameters.Count == 1
            && node.Parameters.TryGetValue("input", out var canonicalWorkspaceInput)
            && WorkspaceActionInputContract.TryParse(canonicalWorkspaceInput, kind, out var workspaceInput, out _)
            && string.Equals(WorkspaceActionInputContract.Encode(workspaceInput!), canonicalWorkspaceInput, StringComparison.Ordinal)
            && GovernedActuatorInputContract.TryCanonicalize(canonicalWorkspaceInput, out workspaceEvidence, out _);
        if (commandMatched == workspaceMatched)
        {
            return false;
        }

        input = commandMatched ? commandInput : workspaceEvidence;
        return input is not null;
    }

    private static GovernedLoopEffectReconciliationInputReadResult Closed(GovernedLoopEffectReconciliationInputReadStatus status)
        => new(status, null, null, null, null, null);
}
