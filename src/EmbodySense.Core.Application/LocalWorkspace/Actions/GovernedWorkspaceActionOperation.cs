using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions;

/// <summary>Adapts one exact workspace operation to the canonical catalog, preparation, authority, and effect-attempt protocol.</summary>
public sealed class GovernedWorkspaceActionOperation : IGovernedActuatorOperation, IGovernedActuatorOutcomeProbe, IGovernedActuatorPreparationValidator
{
    private readonly IWorkspaceActionNativeHost _host;
    private readonly WorkspaceActionKind _kind;

    /// <summary>Creates one exact server-registered workspace operation.</summary>
    public GovernedWorkspaceActionOperation(
        CapabilityDescriptorIdentity capability,
        CapabilityImplementationIdentity implementation,
        WorkspaceActionKind kind,
        IWorkspaceActionNativeHost host)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(implementation);
        _host = host ?? throw new ArgumentNullException(nameof(host));
        if (kind is not (WorkspaceActionKind.Append or WorkspaceActionKind.Write or WorkspaceActionKind.Delete))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        _kind = kind;
        Descriptor = GovernedActuatorOperationContract.Create(
            1,
            capability,
            implementation,
            WorkspaceActionOperationIds.For(kind),
            kind == WorkspaceActionKind.Delete
                ? "Move one exact regular file into bounded recoverable quarantine."
                : "Install one exact bounded regular-file after-image through the protected native commit boundary.",
            GovernedActuatorTargetSemantics.ExactWorkspaceTarget,
            GovernedActuatorIdempotencyPosture.StableOperationIdentity,
            requiresOptimisticPrecondition: true,
            GovernedActuatorApprovalPosture.AuthorityOnly,
            unattendedEligible: true,
            GovernedActuatorCancellationPosture.BeforeBoundaryOnly,
            GovernedActuatorAmbiguityPosture.ReconciliationRequired,
            requiresBeforeEvidence: true,
            requiresAfterEvidence: true,
            requiresOutcomeEvidence: true);
    }

    /// <inheritdoc />
    public GovernedActuatorOperationDescriptor Descriptor { get; }

    /// <inheritdoc />
    public string? ValidateInput(GovernedActuatorInputEvidence input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return WorkspaceActionInputContract.TryParse(input.CanonicalJson, _kind, out _, out var reasonCode)
            ? null
            : reasonCode ?? "workspace-input-invalid";
    }

    /// <inheritdoc />
    public async Task<GovernedActuatorPreparationEvidence?> PrepareAsync(
        GovernedActuatorInputEvidence input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!WorkspaceActionInputContract.TryParse(input.CanonicalJson, _kind, out var semantic, out _)
            || WorkspaceActionInputContract.RequiresCredentialBridge(semantic!))
        {
            return null;
        }

        var prepared = await _host.PrepareAsync(semantic!, cancellationToken).ConfigureAwait(false);
        var evidence = prepared?.BeforeEvidence;
        if (WorkspaceActionEvidenceContract.ValidateBefore(evidence) is not null
            || !string.Equals(evidence!.ScopeId, semantic!.ScopeId.Value, StringComparison.Ordinal)
            || !string.Equals(evidence.TargetReference, semantic.Target.Value, StringComparison.Ordinal)
            || !string.Equals(evidence.PreconditionEvidenceHash, WorkspaceActionInputContract.ComputePreconditionHash(semantic.Precondition), StringComparison.Ordinal))
        {
            return null;
        }

        return new GovernedActuatorPreparationEvidence(
            evidence.TargetFingerprint,
            evidence.PreconditionEvidenceHash,
            evidence.EvidenceId);
    }

    /// <inheritdoc />
    public Task<bool> IsPreparationCurrentAsync(
        GovernedActuatorInputEvidence input,
        GovernedActuatorPreparationEvidence preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(preparation);
        if (!WorkspaceActionInputContract.TryParse(input.CanonicalJson, _kind, out var semantic, out _)
            || WorkspaceActionInputContract.RequiresCredentialBridge(semantic!)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(preparation.TargetFingerprint)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(preparation.BeforeEvidenceId))
        {
            return Task.FromResult(false);
        }
        return _host.IsPreparationCurrentAsync(semantic!, preparation.TargetFingerprint, preparation.BeforeEvidenceId!, cancellationToken);
    }

    /// <inheritdoc />
    public Task<GovernedActuatorAdapterResult> ExecuteAsync(
        GovernedActuatorInvocation invocation,
        IGovernedActuatorDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(dispatchBoundary);
        if (!Equals(invocation.Descriptor, Descriptor)
            || !WorkspaceActionInputContract.TryParse(invocation.Input.CanonicalJson, _kind, out var input, out _)
            || WorkspaceActionInputContract.RequiresCredentialBridge(input!)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(invocation.BeforeEvidenceId)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(invocation.TargetFingerprint)
            || !string.Equals(invocation.PreconditionEvidenceHash, WorkspaceActionInputContract.ComputePreconditionHash(input!.Precondition), StringComparison.Ordinal))
        {
            return Task.FromResult(new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null));
        }

        return ExecuteNativeAsync(
            new WorkspaceActionNativeExecutionRequest(
                input!,
                invocation.TargetFingerprint,
                invocation.BeforeEvidenceId!,
                invocation.EffectId,
                invocation.IdempotencyOperationId,
                invocation.EffectGeneration),
            dispatchBoundary,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GovernedActuatorProbeResult> ProbeAsync(
        GovernedActuatorInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!Equals(invocation.Descriptor, Descriptor)
            || !WorkspaceActionInputContract.TryParse(invocation.Input.CanonicalJson, _kind, out var input, out _)
            || WorkspaceActionInputContract.RequiresCredentialBridge(input!)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(invocation.BeforeEvidenceId)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(invocation.TargetFingerprint)
            || !string.Equals(invocation.PreconditionEvidenceHash, WorkspaceActionInputContract.ComputePreconditionHash(input!.Precondition), StringComparison.Ordinal))
        {
            return new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Unavailable, null);
        }

        var result = await _host.ProbeAsync(
            new WorkspaceActionReconciliationProbeRequest(
                input!,
                invocation.TargetFingerprint,
                invocation.BeforeEvidenceId!,
                invocation.EffectId,
                invocation.IdempotencyOperationId,
                invocation.EffectGeneration),
            cancellationToken).ConfigureAwait(false);
        return result.Posture switch
        {
            WorkspaceActionReconciliationPosture.ProvedNotStarted when result.AfterEvidenceId is null
                && result.OutcomeEvidenceId is null
                => new GovernedActuatorProbeResult(GovernedActuatorProbePosture.ProvedNotStarted, null),
            WorkspaceActionReconciliationPosture.ProvedOutcomeObserved when result.AfterEvidenceId is not null
                && result.OutcomeEvidenceId is not null
                => new GovernedActuatorProbeResult(
                    GovernedActuatorProbePosture.OutcomeObserved,
                    new GovernedActuatorExternalOutcome(
                        GovernedLoopEffectOutcome.Succeeded,
                        result.OutcomeEvidenceId,
                        result.AfterEvidenceId)),
            WorkspaceActionReconciliationPosture.Indeterminate
                => new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Indeterminate, null),
            _ => new GovernedActuatorProbeResult(GovernedActuatorProbePosture.Unavailable, null),
        };
    }

    private async Task<GovernedActuatorAdapterResult> ExecuteNativeAsync(
        WorkspaceActionNativeExecutionRequest request,
        IGovernedActuatorDispatchBoundary dispatchBoundary,
        CancellationToken cancellationToken)
    {
        var result = await _host.ExecuteAsync(
            request,
            new WorkspaceActionNativeDispatchBoundaryAdapter(dispatchBoundary),
            cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            WorkspaceActionNativeCommitStatus.DispatchNotStarted when result.Outcome is null
                => new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.DispatchNotStarted, null),
            WorkspaceActionNativeCommitStatus.OutcomeObserved when result.Outcome is not null
                => new GovernedActuatorAdapterResult(
                    GovernedActuatorAdapterStatus.OutcomeObserved,
                    new GovernedActuatorExternalOutcome(
                        GovernedLoopEffectOutcome.Succeeded,
                        result.Outcome.OutcomeEvidenceId,
                        result.Outcome.AfterEvidenceId)),
            _ => throw new InvalidOperationException("The native workspace host returned a malformed closed commit result."),
        };
    }

}
