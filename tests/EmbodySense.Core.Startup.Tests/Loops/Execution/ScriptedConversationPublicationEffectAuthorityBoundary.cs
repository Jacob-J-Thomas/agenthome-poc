using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal sealed class ScriptedConversationPublicationEffectAuthorityBoundary(
    ScriptedConversationPublicationAuthorityBehavior behavior) : IGovernedLoopEffectAuthorityBoundary
{
    private Func<Task>? _lateCallback;

    internal GovernedLoopEffectAuthorityRequest? LastRequest { get; private set; }

    internal int CallbackInvocations { get; private set; }

    internal Task? CapturedCallbackTask { get; private set; }

    public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return behavior switch
        {
            ScriptedConversationPublicationAuthorityBehavior.Direct => await DirectAsync(request, commit, cancellationToken),
            ScriptedConversationPublicationAuthorityBehavior.DenyRevoked => Stopped<TResult>(
                request,
                Decision(request, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantRevoked)),
            ScriptedConversationPublicationAuthorityBehavior.Pause => Stopped<TResult>(
                request,
                Decision(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous)),
            ScriptedConversationPublicationAuthorityBehavior.DenyUnrelatedCeiling => Stopped<TResult>(
                request,
                Decision(request, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.CapabilityInactive, Narrowing.UnrelatedCapability)),
            ScriptedConversationPublicationAuthorityBehavior.DenyExternalPublication => Stopped<TResult>(
                request,
                Decision(request, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.EffectOutsideCeiling, Narrowing.ExternalPublication)),
            ScriptedConversationPublicationAuthorityBehavior.Invalid => Unresolved<TResult>(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest),
            ScriptedConversationPublicationAuthorityBehavior.Unavailable => Unresolved<TResult>(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable),
            ScriptedConversationPublicationAuthorityBehavior.Replay => Rejected<TResult>(request, GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent),
            ScriptedConversationPublicationAuthorityBehavior.EvidenceUnavailable => Rejected<TResult>(request, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable),
            ScriptedConversationPublicationAuthorityBehavior.DirectAlreadyPresent => DirectAlreadyPresent<TResult>(request),
            ScriptedConversationPublicationAuthorityBehavior.DoubleCallback => await DoubleCallbackAsync(request, commit, cancellationToken),
            ScriptedConversationPublicationAuthorityBehavior.LateCallback => CaptureLate<TResult>(request, commit),
            ScriptedConversationPublicationAuthorityBehavior.UnawaitedCallback => CaptureUnawaited(request, commit, cancellationToken),
            ScriptedConversationPublicationAuthorityBehavior.NoCallbackDirect => DirectWithoutCallback<TResult>(request),
            ScriptedConversationPublicationAuthorityBehavior.NullResult => null!,
            ScriptedConversationPublicationAuthorityBehavior.MalformedResult => Malformed<TResult>(),
            ScriptedConversationPublicationAuthorityBehavior.MismatchedDecision => Mismatched<TResult>(request),
            ScriptedConversationPublicationAuthorityBehavior.SwallowCallbackFailure => await SwallowCallbackFailureAsync(request, commit, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported scripted conversation-publication authority behavior."),
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
        return Direct(request, result);
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
        catch (InvalidOperationException)
        {
            // Deliberately swallowed: the adapter must retain and surface the protocol violation.
        }

        return Direct(request, result);
    }

    private async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> SwallowCallbackFailureAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken)
    {
        CallbackInvocations++;
        try
        {
            _ = await commit(cancellationToken);
        }
        catch (Exception)
        {
            // Deliberately swallowed: the adapter must preserve the publisher-owned callback failure.
        }

        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            true,
            default,
            "The hostile boundary swallowed the callback failure.");
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
        return Stopped<TResult>(
            request,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous));
    }

    private GovernedLoopEffectAuthorityExecutionResult<TResult> CaptureUnawaited<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken)
    {
        CallbackInvocations++;
        CapturedCallbackTask = commit(cancellationToken);
        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            true,
            default,
            "The hostile boundary returned without awaiting the callback.");
    }

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> DirectWithoutCallback<TResult>(
        GovernedLoopEffectAuthorityRequest request)
        => new(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            false,
            default,
            "The hostile boundary skipped the callback.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> DirectAlreadyPresent<TResult>(
        GovernedLoopEffectAuthorityRequest request)
        => new(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent,
            false,
            default,
            "The exact direct decision was already durable and requires reconciliation.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Malformed<TResult>()
        => new(
            (GovernedLoopEffectAuthorityExecutionStatus)999,
            null,
            (GovernedLoopEffectAuthorityEvidenceStoreStatus)999,
            false,
            default,
            "Malformed hostile result.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Mismatched<TResult>(
        GovernedLoopEffectAuthorityRequest request)
    {
        var exact = Decision(request, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantRevoked);
        var mismatched = GovernedLoopEffectAuthorityContractHash.Apply(exact with
        {
            RunId = "run-publication-other",
            ContentHash = string.Empty,
        });
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(mismatched).IsValid);
        return Stopped<TResult>(request, mismatched);
    }

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Direct<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        TResult result)
        => new(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Direct, GovernedLoopEffectAuthorityReason.ActiveExact),
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            true,
            result,
            "The durable direct decision invoked the protected continuation once.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Stopped<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityDecision decision)
        => new(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            decision,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            false,
            default,
            "The durable decision stopped the publication.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Rejected<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityEvidenceStoreStatus evidenceStatus)
        => new(
            GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
            Decision(request, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous),
            evidenceStatus,
            false,
            default,
            "The durable authority evidence requires reconciliation.");

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Unresolved<TResult>(
        GovernedLoopEffectAuthorityExecutionStatus status)
        => new(
            status,
            null,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown,
            false,
            default,
            "Authority could not be resolved.");

    private static GovernedLoopEffectAuthorityDecision Decision(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityDisposition disposition,
        GovernedLoopEffectAuthorityReason reason,
        Narrowing narrowing = Narrowing.None)
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
        var current = CurrentProof(admitted, disposition, narrowing);
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
            ConversationPublicationAuthorityTestFixture.Now,
            string.Empty));
        if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            throw new InvalidOperationException($"The scripted {disposition}/{reason}/{narrowing} decision was not canonical.");
        }

        return decision;
    }

    private static GovernedLoopEffectAuthorityProof CurrentProof(
        GovernedLoopEffectAuthorityProof admitted,
        GovernedLoopEffectAuthorityDisposition disposition,
        Narrowing narrowing)
    {
        if (disposition == GovernedLoopEffectAuthorityDisposition.Deny && narrowing == Narrowing.None)
        {
            return new GovernedLoopEffectAuthorityProof(
                admitted.SchemaVersion,
                admitted.Grant,
                admitted.Binding,
                AuthorityGrantLifecycleStatus.Revoked,
                GovernedLoopEffectAuthorityGrantPosture.Revoked,
                admitted.Boundary,
                admitted.Ceiling,
                [],
                [],
                admitted.DependencyEvidenceHash);
        }

        var ceiling = narrowing switch
        {
            Narrowing.UnrelatedCapability => new AuthorityCeiling(
                admitted.Ceiling.Capabilities.Where(item => item.Id.Value == ConversationPublicationAuthorityTestFixture.ModelInferenceCapabilityId).ToArray(),
                admitted.Ceiling.DataClasses,
                admitted.Ceiling.MaxTargetCount,
                admitted.Ceiling.MaxSideEffectClass,
                admitted.Ceiling.AllowsRecurrence,
                admitted.Ceiling.AllowsExternalPublication,
                admitted.Ceiling.AllowsIrreversibleAction),
            Narrowing.ExternalPublication => new AuthorityCeiling(
                admitted.Ceiling.Capabilities,
                admitted.Ceiling.DataClasses,
                admitted.Ceiling.MaxTargetCount,
                admitted.Ceiling.MaxSideEffectClass,
                admitted.Ceiling.AllowsRecurrence,
                false,
                admitted.Ceiling.AllowsIrreversibleAction),
            _ => admitted.Ceiling,
        };
        return new GovernedLoopEffectAuthorityProof(
            admitted.SchemaVersion,
            admitted.Grant,
            admitted.Binding,
            AuthorityGrantLifecycleStatus.Active,
            GovernedLoopEffectAuthorityGrantPosture.Active,
            admitted.Boundary,
            ceiling,
            narrowing == Narrowing.UnrelatedCapability
                ? admitted.CapabilityPins.Where(item => item.DescriptorIdentity.Id.Value == ConversationPublicationAuthorityTestFixture.ModelInferenceCapabilityId).ToArray()
                : admitted.CapabilityPins,
            [],
            admitted.DependencyEvidenceHash);
    }

    private enum Narrowing
    {
        None,
        UnrelatedCapability,
        ExternalPublication,
    }
}
