using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Projects one exact guarded graph Action into the canonical workspace actuator attempt protocol.</summary>
public sealed class GovernedLoopWorkspaceActionExecutor : IGovernedLoopWorkspaceActionExecutor
{
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    private readonly GovernedLoopEffectAttemptFacade _facade;

    /// <summary>Creates the graph Action adapter over one canonical effect facade.</summary>
    public GovernedLoopWorkspaceActionExecutor(GovernedLoopEffectAttemptFacade facade)
    {
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(
        GovernedLoopWorkspaceActionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        WorkspaceActionKind kind;
        WorkspaceActionInput? input;
        CapabilityAdmissionPin? pin;
        try
        {
            if (!TryValidate(request, out kind, out input, out pin))
            {
                return Rejected("The exact workspace Action request is invalid.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Rejected($"The exact workspace Action request is invalid ({exception.GetType().Name}).");
        }

        try
        {
            var dispatch = request.Dispatch;
            var binding = dispatch.Anchor.AdapterBinding;
            var executor = new GovernedWorkspaceMutationToolExecutor(
                _facade,
                binding.AdmissionReceipt,
                binding.ExecutionBinding,
                request.GraphArtifact,
                dispatch.Node.NodeId,
                dispatch.Attempt,
                request.AttemptOperationId,
                pin!,
                request.HumanReviewRelease);
            var result = await executor.ExecuteEffectAsync(
                new ToolRequest(
                    Command(kind),
                    input!.Target.Value,
                    request.InputJson,
                    CorrelationId: request.AttemptOperationId),
                cancellationToken).ConfigureAwait(false);
            if (result.Status is GovernedLoopEffectAttemptExecutionStatus.Committed or GovernedLoopEffectAttemptExecutionStatus.Replayed
                && result.Attempt?.AfterEvidenceId is { } afterEvidenceId)
            {
                var status = result.Status == GovernedLoopEffectAttemptExecutionStatus.Committed
                    ? WorkspaceActionResultStatus.Committed
                    : WorkspaceActionResultStatus.Replayed;
                var output = WorkspaceActionResultContract.Encode(WorkspaceActionResultContract.Create(status, afterEvidenceId, result.Attempt.Payload.EffectGeneration));
                return new GovernedLoopWorkspaceActionExecutionResult(GovernedLoopWorkspaceActionExecutionStatus.Completed, output, "The exact workspace Action outcome is durable.");
            }

            return result.Status switch
            {
                GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted
                    or GovernedLoopEffectAttemptExecutionStatus.InvalidRequest
                    or GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable
                    or GovernedLoopEffectAttemptExecutionStatus.AuthorityStopped
                    or GovernedLoopEffectAttemptExecutionStatus.Conflict
                    or GovernedLoopEffectAttemptExecutionStatus.Backpressured
                    => Rejected($"The workspace Action stopped before mutation with posture `{result.Status}`."),
                GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired
                    => new GovernedLoopWorkspaceActionExecutionResult(
                        GovernedLoopWorkspaceActionExecutionStatus.ApprovalRequired,
                        null,
                        "The exact prepared workspace Action effect is durably parked for governed Human Review.",
                        result.Attempt),
                _ => Review($"The workspace Action requires reconciliation with posture `{result.Status}`."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            return Review($"The workspace Action adapter failed closed ({exception.GetType().Name}).");
        }
    }

    private static bool TryValidate(
        GovernedLoopWorkspaceActionExecutionRequest? request,
        out WorkspaceActionKind kind,
        out WorkspaceActionInput? input,
        out CapabilityAdmissionPin? pin)
    {
        kind = WorkspaceActionKind.Unknown;
        input = null;
        pin = null;
        if (request?.Dispatch is not { } dispatch
            || dispatch.Anchor is null
            || dispatch.Node is null
            || dispatch.Activation is null
            || !WorkspaceActionNodeDescriptors.TryResolve(dispatch.Node.Descriptor, out kind)
            || !CustomLoopArtifactIdentifier.IsValid(request.AttemptOperationId)
            || !string.Equals(dispatch.Activation.AttemptOperationId, request.AttemptOperationId, StringComparison.Ordinal)
            || dispatch.Activation.Attempt != dispatch.Attempt
            || !dispatch.Node.Parameters.TryGetValue("input", out var planInput)
            || !string.Equals(planInput, request.InputJson, StringComparison.Ordinal)
            || !WorkspaceActionInputContract.TryParse(request.InputJson, kind, out input, out _)
            || !string.Equals(WorkspaceActionInputContract.Encode(input!), request.InputJson, StringComparison.Ordinal)
            || !string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(request.GraphArtifact), request.GraphArtifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(request.GraphArtifact.ArtifactHash, dispatch.Anchor.AdapterBinding.GraphArtifactHash, StringComparison.Ordinal)
            || request.GraphArtifact.Graph.Nodes.SingleOrDefault(node => string.Equals(node.Id, dispatch.Node.NodeId, StringComparison.Ordinal)) is not { } graphNode
            || !Equals(graphNode.Descriptor, dispatch.Node.Descriptor)
            || !graphNode.Parameters.TryGetValue("input", out var graphInput)
            || !string.Equals(graphInput, request.InputJson, StringComparison.Ordinal)
            || !GovernedLoopAdmissionValidator.Validate(dispatch.Anchor.AdapterBinding.AdmissionReceipt).IsValid)
        {
            return false;
        }

        var matches = dispatch.Anchor.AdapterBinding.AdmissionReceipt.Evidence.CapabilityAdmission.Pins
            .Where(candidate => string.Equals(candidate.DescriptorIdentity.Id.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1 || !graphNode.AuthorityCeiling.CapabilityIds.SequenceEqual([WorkspaceCommandCapabilityId], StringComparer.Ordinal))
        {
            return false;
        }
        pin = matches[0];
        return true;
    }

    private static ToolCommand Command(WorkspaceActionKind kind)
        => kind switch
        {
            WorkspaceActionKind.Append => ToolCommand.Append,
            WorkspaceActionKind.Write => ToolCommand.Write,
            WorkspaceActionKind.Delete => ToolCommand.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static GovernedLoopWorkspaceActionExecutionResult Rejected(string detail)
        => new(GovernedLoopWorkspaceActionExecutionStatus.Rejected, null, detail);

    private static GovernedLoopWorkspaceActionExecutionResult Review(string detail)
        => new(GovernedLoopWorkspaceActionExecutionStatus.NeedsReview, null, detail);
}
