using EmbodySense.Core.Application.CommandActions;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Projects exact hash-pinned graph command Actions into the canonical effect-attempt protocol.</summary>
public sealed class GovernedLoopCommandActionExecutor : IGovernedLoopCommandActionExecutor
{
    private readonly GovernedLoopEffectAttemptFacade _facade;
    private readonly ICommandActionRegistrationResolver _registrations;

    /// <summary>Creates the graph adapter over one exact registry and canonical effect facade.</summary>
    public GovernedLoopCommandActionExecutor(
        GovernedLoopEffectAttemptFacade facade,
        ICommandActionRegistrationResolver registrations)
    {
        _facade = facade ?? throw new ArgumentNullException(nameof(facade));
        _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(
        GovernedLoopCommandActionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedCommandAction? validated;
        try
        {
            if (!TryValidate(request, out validated))
            {
                return Rejected("The exact structured command Action request is invalid or stale.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Rejected($"The exact structured command Action request is invalid ({exception.GetType().Name}).");
        }

        try
        {
            var command = validated!;
            var dispatch = request.Dispatch;
            var binding = dispatch.Anchor.AdapterBinding;
            var operationId = GovernedCommandActionOperation.CreateOperationId(command.Registration.Template);
            var identity = CommandActionFingerprint.Compute(
                "embodysense.graph-command-effect.v1",
                binding.ExecutionBinding.RunId,
                dispatch.Node.NodeId,
                dispatch.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                request.AttemptOperationId,
                operationId,
                command.CanonicalInput);
            var result = await _facade.ExecuteAsync(
                new GovernedLoopEffectAttemptRequest(
                    binding.AdmissionReceipt,
                    binding.ExecutionBinding,
                    request.GraphArtifact,
                    dispatch.Node.NodeId,
                    dispatch.Attempt,
                    command.CapabilityPin,
                    operationId,
                    "effect-" + identity,
                    "operation-" + identity,
                    1,
                    command.CanonicalInput,
                    command.RequiredAuthority,
                    request.AttemptOperationId,
                    request.HumanReviewRelease),
                cancellationToken).ConfigureAwait(false);
            if (result.Status is GovernedLoopEffectAttemptExecutionStatus.Committed or GovernedLoopEffectAttemptExecutionStatus.Replayed
                && result.Attempt is { Payload.OutcomeEvidenceId: { } outcomeEvidenceId } attempt
                && attempt.Payload.Outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed)
            {
                var output = CommandActionResultContract.Encode(CommandActionResultContract.Create(
                    result.Status == GovernedLoopEffectAttemptExecutionStatus.Committed
                        ? CommandActionResultStatus.Committed
                        : CommandActionResultStatus.Replayed,
                    attempt.Payload.Outcome == GovernedLoopEffectOutcome.Succeeded
                        ? CommandActionResultOutcome.Succeeded
                        : CommandActionResultOutcome.Failed,
                    outcomeEvidenceId,
                    attempt.Payload.EffectGeneration));
                return new GovernedLoopCommandActionExecutionResult(
                    attempt.Payload.Outcome == GovernedLoopEffectOutcome.Succeeded
                        ? GovernedLoopCommandActionExecutionStatus.Completed
                        : GovernedLoopCommandActionExecutionStatus.Failed,
                    output,
                    "The exact structured command Action outcome is durable.");
            }

            return result.Status switch
            {
                GovernedLoopEffectAttemptExecutionStatus.InvalidRequest
                    => Rejected($"The structured command Action stopped before launch because its canonical effect request was invalid: {result.Detail}"),
                GovernedLoopEffectAttemptExecutionStatus.DispatchNotStarted
                    => Rejected(result.Detail),
                GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable
                    or GovernedLoopEffectAttemptExecutionStatus.AuthorityStopped
                    or GovernedLoopEffectAttemptExecutionStatus.Conflict
                    or GovernedLoopEffectAttemptExecutionStatus.Backpressured
                    => Rejected($"The structured command Action stopped before launch with posture `{result.Status}`."),
                GovernedLoopEffectAttemptExecutionStatus.ApprovalRequired
                    => new GovernedLoopCommandActionExecutionResult(
                        GovernedLoopCommandActionExecutionStatus.ApprovalRequired,
                        null,
                        "The exact prepared command Action effect is durably parked for governed Human Review.",
                        result.Attempt),
                GovernedLoopEffectAttemptExecutionStatus.OperationInProgress
                    => new GovernedLoopCommandActionExecutionResult(
                        GovernedLoopCommandActionExecutionStatus.OperationInProgress,
                        null,
                        "Another executor owns the exact command Action effect attempt."),
                _ => Review($"The structured command Action requires reconciliation with posture `{result.Status}`."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            return Review($"The structured command Action adapter failed closed ({exception.GetType().Name}).");
        }
    }

    private bool TryValidate(
        GovernedLoopCommandActionExecutionRequest? request,
        out ValidatedCommandAction? validated)
    {
        validated = null;
        if (request?.Dispatch is not { } dispatch
            || dispatch.Anchor is null
            || dispatch.Node is null
            || dispatch.Activation is null
            || !_registrations.TryResolve(dispatch.Node.Descriptor, out var registration)
            || registration is null
            || !CustomLoopArtifactIdentifier.IsValid(request.AttemptOperationId)
            || !string.Equals(dispatch.Activation.AttemptOperationId, request.AttemptOperationId, StringComparison.Ordinal)
            || dispatch.Activation.Attempt != dispatch.Attempt
            || !TryCreateInput(dispatch.Node.Parameters, registration, out var canonicalInput)
            || !string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(request.GraphArtifact), request.GraphArtifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(request.GraphArtifact.ArtifactHash, dispatch.Anchor.AdapterBinding.GraphArtifactHash, StringComparison.Ordinal)
            || request.GraphArtifact.Graph.Nodes.SingleOrDefault(node => string.Equals(node.Id, dispatch.Node.NodeId, StringComparison.Ordinal)) is not { } graphNode
            || !Equals(graphNode.Descriptor, dispatch.Node.Descriptor)
            || !graphNode.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(dispatch.Node.Parameters.OrderBy(item => item.Key, StringComparer.Ordinal))
            || !GovernedLoopAdmissionValidator.Validate(dispatch.Anchor.AdapterBinding.AdmissionReceipt).IsValid)
        {
            return false;
        }

        var admission = dispatch.Anchor.AdapterBinding.AdmissionReceipt;
        var matches = admission.Evidence.CapabilityAdmission.Pins
            .Where(candidate => Equals(candidate.DescriptorIdentity, registration.Template.Capability)
                && Equals(candidate.Implementation, registration.Template.Implementation))
            .Take(2)
            .ToArray();
        if (matches.Length != 1
            || !graphNode.AuthorityCeiling.CapabilityIds.SequenceEqual([registration.Template.Capability.Id.Value], StringComparer.Ordinal))
        {
            return false;
        }
        var admittedAuthority = admission.Evidence.EffectiveAuthority;
        var capability = registration.Manifest.Descriptor;
        var required = new AuthorityCeiling(
            Array.AsReadOnly(new[] { matches[0].DescriptorIdentity }),
            capability.Requirements.DataClasses,
            1,
            capability.SideEffectClass,
            false,
            capability.SideEffectClass is CapabilitySideEffectClass.ExternalReversible or CapabilitySideEffectClass.Irreversible,
            capability.SideEffectClass == CapabilitySideEffectClass.Irreversible);
        if (!AuthorityProfileValidator.ValidateCeiling(required).IsValid
            || !(AuthorityCeilingSubset.IsEqual(required, admission.Evidence.EffectiveAuthority)
                || AuthorityCeilingSubset.IsStrictSubset(required, admission.Evidence.EffectiveAuthority)))
        {
            return false;
        }
        validated = new ValidatedCommandAction(registration, matches[0], required, canonicalInput!);
        return true;
    }

    private static bool TryCreateInput(
        IReadOnlyDictionary<string, string> parameters,
        CommandActionRegistration registration,
        out string? canonicalInput)
    {
        canonicalInput = null;
        if (parameters.Count != registration.Template.Slots.Count
            || registration.Template.Slots.Any(slot => !parameters.ContainsKey(slot.Name)))
        {
            return false;
        }
        var input = new CommandActionInput(
            1,
            registration.Template.TemplateId,
            registration.Template.TemplateVersion,
            registration.Template.ContentHash,
            Array.AsReadOnly(registration.Template.Slots.Select(slot => new CommandActionSlotValue(slot.Name, slot.Kind, parameters[slot.Name])).ToArray()));
        try
        {
            canonicalInput = CommandActionInputContract.Encode(input, registration.Template);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static GovernedLoopCommandActionExecutionResult Rejected(string detail)
        => new(GovernedLoopCommandActionExecutionStatus.Rejected, null, detail);

    private static GovernedLoopCommandActionExecutionResult Review(string detail)
        => new(GovernedLoopCommandActionExecutionStatus.NeedsReview, null, detail);

    private sealed record ValidatedCommandAction(
        CommandActionRegistration Registration,
        CapabilityAdmissionPin CapabilityPin,
        AuthorityCeiling RequiredAuthority,
        string CanonicalInput);
}
