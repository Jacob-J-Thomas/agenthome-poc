using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.CommandActions;

/// <summary>Adapts one structured command template to the canonical preparation, authority, and effect-attempt protocol.</summary>
public sealed class GovernedCommandActionOperation : IGovernedActuatorOperation, IGovernedActuatorOutcomeProbe, IGovernedActuatorPreparationValidator
{
    private readonly ICommandActionNativeHost _host;
    private readonly CommandActionRegistration _registration;

    /// <summary>Creates one exact immutable command operation.</summary>
    public GovernedCommandActionOperation(CommandActionRegistration registration, ICommandActionNativeHost host)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (CommandActionRegistrationContract.Validate(registration) is { } reasonCode)
        {
            throw new ArgumentException(reasonCode, nameof(registration));
        }
        var template = registration.Template;
        Descriptor = GovernedActuatorOperationContract.Create(
            1,
            template.Capability,
            template.Implementation,
            CreateOperationId(template),
            "Execute one exact immutable command template through pre-launch process isolation.",
            GovernedActuatorTargetSemantics.ExactOpaqueFingerprint,
            GovernedActuatorIdempotencyPosture.ReconciliationOnly,
            requiresOptimisticPrecondition: true,
            GovernedActuatorApprovalPosture.AuthorityOnly,
            unattendedEligible: true,
            GovernedActuatorCancellationPosture.CooperativeAfterBoundary,
            GovernedActuatorAmbiguityPosture.ReconciliationRequired,
            requiresBeforeEvidence: true,
            requiresAfterEvidence: false,
            requiresOutcomeEvidence: true);
    }

    /// <summary>Creates the exact actuator operation identity for one immutable command template content hash.</summary>
    /// <param name="template">The validated immutable command template whose complete content hash is pinned into the identity.</param>
    /// <returns>The exact slash-separated operation identity used to register and dispatch the template.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is <see langword="null"/>.</exception>
    public static string CreateOperationId(CommandActionTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return "command/" + template.ContentHash[..32] + "/" + template.ContentHash[32..];
    }

    /// <inheritdoc />
    public GovernedActuatorOperationDescriptor Descriptor { get; }

    /// <inheritdoc />
    public string? ValidateInput(GovernedActuatorInputEvidence input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return CommandActionInputContract.TryParse(input.CanonicalJson, _registration.Template, out _, out var reasonCode)
            ? null
            : reasonCode ?? "command-input-invalid";
    }

    /// <inheritdoc />
    public async Task<GovernedActuatorPreparationEvidence?> PrepareAsync(GovernedActuatorInputEvidence input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_registration.Template.RequiresCredentialChannel
            || !CommandActionInputContract.TryMaterialize(input.CanonicalJson, _registration.Template, out var materialized, out _))
        {
            return null;
        }
        var prepared = await _host.PrepareAsync(_registration, input, cancellationToken).ConfigureAwait(false);
        var evidence = prepared?.Evidence;
        if (CommandActionEvidenceContract.ValidatePreparation(evidence) is not null
            || !string.Equals(evidence!.TemplateHash, _registration.Template.ContentHash, StringComparison.Ordinal)
            || !evidence.ArtifactDigest.FixedTimeEquals(_registration.Template.ArtifactDigest)
            || evidence.ActivationRevision != _registration.Template.ActivationRevision
            || !string.Equals(evidence.InputFingerprint, materialized!.InputFingerprint, StringComparison.Ordinal))
        {
            return null;
        }
        return new GovernedActuatorPreparationEvidence(evidence.TargetFingerprint, evidence.PreconditionEvidenceHash, evidence.EvidenceId);
    }

    /// <inheritdoc />
    public Task<bool> IsPreparationCurrentAsync(
        GovernedActuatorInputEvidence input,
        GovernedActuatorPreparationEvidence preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(preparation);
        return _host.IsPreparationCurrentAsync(
            _registration,
            input,
            preparation.TargetFingerprint,
            preparation.PreconditionEvidenceHash ?? string.Empty,
            preparation.BeforeEvidenceId ?? string.Empty,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GovernedActuatorAdapterResult> ExecuteAsync(GovernedActuatorInvocation invocation, IGovernedActuatorDispatchBoundary dispatchBoundary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(dispatchBoundary);
        if (!Equals(invocation.Descriptor, Descriptor)
            || !CommandActionFingerprint.IsCanonicalSha256(invocation.TargetFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(invocation.PreconditionEvidenceHash)
            || !CommandActionFingerprint.IsEvidenceIdentifier(invocation.BeforeEvidenceId)
            || !CommandActionInputContract.TryParse(invocation.Input.CanonicalJson, _registration.Template, out _, out _))
        {
            return Task.FromResult(new GovernedActuatorAdapterResult(
                GovernedActuatorAdapterStatus.DispatchNotStarted,
                null,
                GovernedActuatorDispatchNotStartedReason.InvalidRequest));
        }
        return ExecuteNativeAsync(
            new CommandActionNativeExecutionRequest(
                _registration,
                invocation.Input,
                invocation.EffectId,
                invocation.IdempotencyOperationId,
                invocation.EffectGeneration,
                invocation.TargetFingerprint,
                invocation.PreconditionEvidenceHash!,
                invocation.BeforeEvidenceId!),
            new CommandActionNativeLaunchBoundaryAdapter(dispatchBoundary),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GovernedActuatorProbeResult> ProbeAsync(
        GovernedActuatorInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!Equals(invocation.Descriptor, Descriptor)
            || !CommandActionFingerprint.IsCanonicalSha256(invocation.TargetFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(invocation.PreconditionEvidenceHash)
            || !CommandActionFingerprint.IsEvidenceIdentifier(invocation.BeforeEvidenceId)
            || !CommandActionInputContract.TryParse(invocation.Input.CanonicalJson, _registration.Template, out _, out _))
        {
            return new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Unavailable, null);
        }
        var result = await _host.ProbeAsync(
            new CommandActionNativeExecutionRequest(
                _registration,
                invocation.Input,
                invocation.EffectId,
                invocation.IdempotencyOperationId,
                invocation.EffectGeneration,
                invocation.TargetFingerprint,
                invocation.PreconditionEvidenceHash!,
                invocation.BeforeEvidenceId!),
            cancellationToken).ConfigureAwait(false);
        return result.Posture == CommandActionReconciliationPosture.OutcomeObserved && result.Outcome is not null
            ? new GovernedActuatorProbeResult(
                GovernedActuatorProbePosture.OutcomeObserved,
                new GovernedActuatorExternalOutcome(
                    result.Outcome.Kind == CommandActionNativeOutcomeKind.Succeeded
                        ? EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Succeeded
                        : EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Failed,
                    result.Outcome.OutcomeEvidenceId,
                    null))
            : new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Indeterminate, null);
    }

    private async Task<GovernedActuatorAdapterResult> ExecuteNativeAsync(
        CommandActionNativeExecutionRequest request,
        ICommandActionNativeLaunchBoundary launchBoundary,
        CancellationToken cancellationToken)
    {
        var result = await _host.ExecuteAsync(request, launchBoundary, cancellationToken).ConfigureAwait(false);
        if (result.Status == CommandActionNativeExecutionStatus.DispatchNotStarted && result.Outcome is null)
        {
            return new GovernedActuatorAdapterResult(
                GovernedActuatorAdapterStatus.DispatchNotStarted,
                null,
                result.DispatchNotStartedReason switch
                {
                    CommandActionDispatchNotStartedReason.InvalidRequest => GovernedActuatorDispatchNotStartedReason.InvalidRequest,
                    CommandActionDispatchNotStartedReason.PreparationUnavailable => GovernedActuatorDispatchNotStartedReason.PreparationUnavailable,
                    CommandActionDispatchNotStartedReason.ArtifactUnavailable => GovernedActuatorDispatchNotStartedReason.ArtifactUnavailable,
                    CommandActionDispatchNotStartedReason.ConcurrencyUnavailable => GovernedActuatorDispatchNotStartedReason.ConcurrencyUnavailable,
                    CommandActionDispatchNotStartedReason.LaunchAuthorityUnavailable => GovernedActuatorDispatchNotStartedReason.LaunchAuthorityUnavailable,
                    _ => throw new InvalidOperationException("The native command host omitted its closed pre-dispatch reason."),
                });
        }
        if (result.Status != CommandActionNativeExecutionStatus.OutcomeObserved
            || result.Outcome is null
            || !Enum.IsDefined(result.Outcome.Kind)
            || !CommandActionFingerprint.IsEvidenceIdentifier(result.Outcome.OutcomeEvidenceId))
        {
            throw new InvalidOperationException("The native command host returned an incoherent execution result.");
        }
        return new GovernedActuatorAdapterResult(
            GovernedActuatorAdapterStatus.OutcomeObserved,
            new GovernedActuatorExternalOutcome(
                result.Outcome.Kind == CommandActionNativeOutcomeKind.Succeeded
                    ? EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Succeeded
                    : EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Failed,
                result.Outcome.OutcomeEvidenceId,
                null));
    }
}
