using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal sealed class ScriptedGovernedLoopEffectAuthorityBoundary(ScriptedEffectAuthorityBehavior behavior) : IGovernedLoopEffectAuthorityBoundary
{
    private Func<Task>? _lateCallback;

    internal GovernedLoopEffectAuthorityRequest? LastRequest { get; private set; }

    internal int CallbackInvocations { get; private set; }

    public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return behavior switch
        {
            ScriptedEffectAuthorityBehavior.Direct => await DirectAsync(request, commit, cancellationToken),
            ScriptedEffectAuthorityBehavior.Deny => Stopped<TResult>(request, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantRevoked),
            ScriptedEffectAuthorityBehavior.Pause => Stopped<TResult>(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous),
            ScriptedEffectAuthorityBehavior.Invalid => Unresolved<TResult>(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest),
            ScriptedEffectAuthorityBehavior.Unavailable => Unresolved<TResult>(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable),
            ScriptedEffectAuthorityBehavior.ReplayAmbiguous => new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
                Decision(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous),
                GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
                false,
                default,
                "The exact direct decision was already present."),
            ScriptedEffectAuthorityBehavior.DoubleCallback => await DoubleCallbackAsync(request, commit, cancellationToken),
            ScriptedEffectAuthorityBehavior.LateCallback => CaptureLate<TResult>(request, commit),
            _ => throw new InvalidOperationException("Unsupported scripted effect-authority behavior."),
        };
    }

    internal Task InvokeLateAsync()
        => _lateCallback?.Invoke() ?? throw new InvalidOperationException("No late callback was captured.");

    private async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> DirectAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken)
    {
        CallbackInvocations++;
        var result = await commit(cancellationToken);
        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            true,
            result,
            "The durable direct decision invoked the protected continuation once.");
    }

    private async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> DoubleCallbackAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken)
    {
        CallbackInvocations++;
        var result = await commit(cancellationToken);
        CallbackInvocations++;
        try
        {
            _ = await commit(cancellationToken);
        }
        catch (ToolActuationAuthorityProtocolException)
        {
            // The hostile boundary deliberately swallows the callback failure. The adapter must retain
            // and surface it instead of accepting this apparently direct result.
        }

        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            true,
            result,
            "The hostile boundary attempted two callbacks.");
    }

    private GovernedLoopEffectAuthorityExecutionResult<TResult> CaptureLate<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit)
    {
        _lateCallback = async () =>
        {
            CallbackInvocations++;
            _ = await commit(CancellationToken.None);
        };
        return Stopped<TResult>(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous);
    }

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Stopped<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityDisposition disposition,
        GovernedLoopEffectAuthorityReason reason)
        => new(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, disposition, reason),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            false,
            default,
            "The durable decision stopped the effect.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Unresolved<TResult>(GovernedLoopEffectAuthorityExecutionStatus status)
        => new(status, null, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, false, default, "Authority could not be resolved.");

    private static GovernedLoopEffectAuthorityDecision Decision(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityDisposition disposition,
        GovernedLoopEffectAuthorityReason reason)
    {
        var receipt = request.AdmissionReceipt;
        var admitted = new GovernedLoopEffectAuthorityProof(
            GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
            receipt.Intent.AuthorityGrant,
            new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication),
            AuthorityGrantLifecycleStatus.Active,
            GovernedLoopEffectAuthorityGrantPosture.Active,
            receipt.Evidence.GrantBoundary,
            receipt.Evidence.EffectiveAuthority,
            receipt.Evidence.CapabilityAdmission.Pins,
            [],
            receipt.Evidence.GrantDependencyEvidenceHash);
        var current = disposition == GovernedLoopEffectAuthorityDisposition.Deny
            ? new GovernedLoopEffectAuthorityProof(
                admitted.SchemaVersion,
                admitted.Grant,
                admitted.Binding,
                AuthorityGrantLifecycleStatus.Revoked,
                GovernedLoopEffectAuthorityGrantPosture.Revoked,
                admitted.Boundary,
                admitted.Ceiling,
                [],
                [],
                admitted.DependencyEvidenceHash)
            : admitted;
        var effective = disposition == GovernedLoopEffectAuthorityDisposition.Direct
            ? request.RequiredAuthority
            : AuthorityCeilingIntersection.EmptyCeiling();
        var decision = GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
            GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
            request.ExecutionBinding.RunId,
            request.ExecutionBinding.ExecutionGeneration,
            request.NodeId,
            request.NodeAttempt,
            request.EffectOperationId,
            request.CorrelationId,
            request.BoundaryKind,
            request.AdmissionReceipt.ContentHash,
            admitted,
            current,
            request.RequiredAuthority,
            effective,
            request.RequiredCapabilityPins,
            disposition,
            reason,
            WorkspaceToolAuthorityTestFixture.Now,
            string.Empty));
        if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            throw new InvalidOperationException("The scripted effect-authority decision was not canonical.");
        }

        return decision;
    }
}
