using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Revalidates exact admitted authority and durably fences one governed-loop effect continuation.</summary>
public sealed class GovernedLoopEffectAuthorityBoundary : IGovernedLoopEffectAuthorityDecisionBoundary
{
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly ICapabilityAdmissionService _capabilityAdmissionService;
    private readonly IGovernedLoopEffectAuthorityEvidenceStore _evidenceStore;
    private readonly IGovernedLoopEffectAuthorityUsageStore _usageStore;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one reusable boundary over the shared workspace authority transaction.</summary>
    /// <param name="grantResolver">The exact immutable grant resolver.</param>
    /// <param name="capabilityAdmissionService">The immutable capability-pin revalidator.</param>
    /// <param name="evidenceStore">The append-only authority-decision store.</param>
    /// <param name="usageStore">The authenticated non-renewable target and completion ledger.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <param name="timeProvider">The optional trusted UTC clock shared with authority composition.</param>
    public GovernedLoopEffectAuthorityBoundary(
        IAuthorityGrantResolver grantResolver,
        ICapabilityAdmissionService capabilityAdmissionService,
        IGovernedLoopEffectAuthorityEvidenceStore evidenceStore,
        IGovernedLoopEffectAuthorityUsageStore usageStore,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _capabilityAdmissionService = capabilityAdmissionService ?? throw new ArgumentNullException(nameof(capabilityAdmissionService));
        _evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        _usageStore = usageStore ?? throw new ArgumentNullException(nameof(usageStore));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ICapabilityAuthorityTransaction AuthorityTransaction => _authorityTransaction;

    /// <inheritdoc />
    public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commit);
        return _authorityTransaction.ExecuteAsync(
            transactionToken => ExecuteUnderAuthorityAsync(request, (_, token) => commit(token), transactionToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commit);
        return _authorityTransaction.ExecuteAsync(
            transactionToken => ExecuteUnderAuthorityAsync(request, commit, transactionToken),
            cancellationToken);
    }

    private async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteUnderAuthorityAsync<TResult>(
        GovernedLoopEffectAuthorityRequest request,
        Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit,
        CancellationToken cancellationToken)
    {
        if (!TryGetUtcNow(out var evaluatedAtUtc))
        {
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, null, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, "Trusted UTC time was unavailable; no effect crossed the boundary.");
        }

        if (!GovernedLoopEffectAuthorityRequestValidator.IsValid(request)
            || !TryCreateAdmittedProof(request, out var admitted))
        {
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, null, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, "The effect-authority request or retained admission proof was invalid.");
        }

        var resolution = await _grantResolver.ResolveAsync(admitted!.Grant, cancellationToken).ConfigureAwait(false);
        if (resolution is null || !TryGetUtcNow(out evaluatedAtUtc))
        {
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, null, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, "Current grant posture or trusted UTC time was unavailable.");
        }

        GovernedLoopEffectAuthorityDecision decision;
        if (resolution.Status != AuthorityGrantResolutionStatus.Active)
        {
            decision = CreateGrantStoppedDecision(request, admitted, resolution, evaluatedAtUtc);
        }
        else if (!TryCreateActiveGrantProof(admitted, resolution, evaluatedAtUtc, out var currentGrantProof, out var activeFailure))
        {
            var disposition = currentGrantProof is null && activeFailure == GovernedLoopEffectAuthorityReason.GrantAmbiguous
                ? GovernedLoopEffectAuthorityDisposition.Pause
                : GovernedLoopEffectAuthorityDisposition.Deny;
            decision = CreateDecision(
                request,
                admitted,
                currentGrantProof,
                disposition,
                activeFailure,
                evaluatedAtUtc);
        }
        else
        {
            var allowedIds = admitted.Ceiling.Capabilities.Select(item => item.Id).ToArray();
            var capability = await _capabilityAdmissionService.RevalidateAsync(request.AdmissionReceipt.Evidence.CapabilityAdmission, allowedIds, cancellationToken).ConfigureAwait(false);
            if (!TryGetUtcNow(out evaluatedAtUtc))
            {
                return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.AuthorityUnavailable, null, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, "Trusted UTC time was unavailable after capability revalidation.");
            }

            currentGrantProof = ApplyCapabilityPosture(currentGrantProof!, capability);
            if (currentGrantProof.Boundary.ExpiresAtUtc is { } expiry && expiry <= evaluatedAtUtc)
            {
                currentGrantProof = new GovernedLoopEffectAuthorityProof(
                    currentGrantProof.SchemaVersion,
                    currentGrantProof.Grant,
                    currentGrantProof.Binding,
                    currentGrantProof.GrantStatus,
                    GovernedLoopEffectAuthorityGrantPosture.Expired,
                    currentGrantProof.Boundary,
                    currentGrantProof.Ceiling,
                    [],
                    [],
                    null);
                decision = CreateDecision(request, admitted, currentGrantProof, GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantExpired, evaluatedAtUtc);
            }
            else
            {
                var (disposition, reason) = CapabilityDisposition(request, capability, currentGrantProof);
                if (disposition == GovernedLoopEffectAuthorityDisposition.Direct)
                {
                    var usage = await ReserveUsageAsync(request, admitted, currentGrantProof, evaluatedAtUtc, cancellationToken).ConfigureAwait(false);
                    (currentGrantProof, disposition, reason) = ApplyUsagePosture(currentGrantProof, reason, usage);
                }

                decision = CreateDecision(request, admitted, currentGrantProof, disposition, reason, evaluatedAtUtc);
            }
        }

        if (!GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
        {
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.InvalidRequest, null, GovernedLoopEffectAuthorityEvidenceStoreStatus.Unknown, "The effect-authority decision could not be proved canonical.");
        }

        var stored = await AppendAsync(decision, cancellationToken).ConfigureAwait(false);
        stored = NormalizeEvidenceResult(stored, decision);
        if (!IsExactStoredDecision(stored, decision))
        {
            var evidenceDecision = decision.Disposition == GovernedLoopEffectAuthorityDisposition.Direct
                ? CreateEvidenceStoppedDecision(request, admitted, decision.CurrentAuthority!, stored.Status, evaluatedAtUtc)
                : decision;
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, evidenceDecision, stored.Status, "Authority evidence was not durably appended; no effect crossed the boundary.");
        }

        if (decision.Disposition != GovernedLoopEffectAuthorityDisposition.Direct)
        {
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.Decided, decision, stored.Status, "The durable authority decision stopped the effect before its boundary.", decision.ContentHash);
        }

        // An exact prior Direct decision proves only that authority was durably admitted. It cannot prove
        // whether a previous process crossed the continuation before crashing, so replay must stop for
        // reconciliation rather than risk duplicating an external effect.
        if (stored.Status == GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent)
        {
            var replayDecision = CreateEvidenceStoppedDecision(request, admitted, decision.CurrentAuthority!, stored.Status, evaluatedAtUtc);
            return Result<TResult>(GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected, replayDecision, stored.Status, "The exact direct decision was already present; effect completion is ambiguous and the continuation was not retried.", decision.ContentHash);
        }

        if (cancellationToken.IsCancellationRequested
            || !TryGetUtcNow(out var commitAtUtc)
            || !IsActiveAt(admitted.Boundary, commitAtUtc)
            || !IsActiveAt(decision.CurrentAuthority!.Boundary, commitAtUtc))
        {
            var stoppedDecision = CreateEvidenceStoppedDecision(
                request,
                admitted,
                decision.CurrentAuthority!,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous,
                decision.EvaluatedAtUtc);
            return Result<TResult>(
                GovernedLoopEffectAuthorityExecutionStatus.EvidenceRejected,
                stoppedDecision,
                stored.Status,
                "Authority expired, trusted time became unavailable, or cancellation arrived after the direct decision was appended; the protected continuation was not invoked and reconciliation is required.",
                decision.ContentHash);
        }

        var commitResult = await commit(decision, cancellationToken).ConfigureAwait(false);
        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            decision,
            stored.Status,
            true,
            commitResult,
            "The durable direct decision invoked the protected continuation exactly once.",
            decision.ContentHash);
    }

    private static bool TryCreateAdmittedProof(
        GovernedLoopEffectAuthorityRequest request,
        out GovernedLoopEffectAuthorityProof? proof)
    {
        proof = null;
        try
        {
            var receipt = request.AdmissionReceipt;
            proof = new GovernedLoopEffectAuthorityProof(
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
            return GovernedLoopEffectAuthorityContractValidator.Validate(proof).IsValid;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            proof = null;
            return false;
        }
    }

    private static bool TryCreateActiveGrantProof(
        GovernedLoopEffectAuthorityProof admitted,
        AuthorityGrantResolution resolution,
        DateTimeOffset evaluatedAtUtc,
        out GovernedLoopEffectAuthorityProof? proof,
        out GovernedLoopEffectAuthorityReason failure)
    {
        proof = null;
        failure = GovernedLoopEffectAuthorityReason.GrantAmbiguous;
        var grant = resolution.CurrentGrant ?? resolution.Grant;
        if (grant is null || resolution.RequestedReference is null || !TryReference(grant, out var reference))
        {
            return false;
        }

        var currentCeiling = Intersect(admitted.Ceiling, resolution.EffectiveCeiling);
        var currentPins = admitted.CapabilityPins.Where(pin => currentCeiling.Capabilities.Contains(pin.DescriptorIdentity)).ToArray();
        proof = new GovernedLoopEffectAuthorityProof(
            GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
            reference!,
            grant.Binding,
            grant.Status,
            GovernedLoopEffectAuthorityGrantPosture.Active,
            grant.Boundary,
            currentCeiling,
            currentPins,
            [],
            IsHash(resolution.DependencyEvidenceHash) ? resolution.DependencyEvidenceHash : null);

        if (!Equals(resolution.RequestedReference, admitted.Grant)
            || !Equals(reference, admitted.Grant))
        {
            failure = GovernedLoopEffectAuthorityReason.GrantStale;
            return false;
        }

        if (!Equals(grant.Binding, admitted.Binding))
        {
            failure = GovernedLoopEffectAuthorityReason.BindingMismatch;
            return false;
        }

        if (!string.Equals(resolution.DependencyEvidenceHash, admitted.DependencyEvidenceHash, StringComparison.Ordinal))
        {
            failure = GovernedLoopEffectAuthorityReason.DependencyMismatch;
            return false;
        }

        if (!IsTrustedUtc(resolution.EvaluatedAtUtc) || resolution.EvaluatedAtUtc > evaluatedAtUtc)
        {
            failure = GovernedLoopEffectAuthorityReason.GrantAmbiguous;
            proof = null;
            return false;
        }

        if (evaluatedAtUtc < grant.Boundary.EffectiveAtUtc)
        {
            proof = ReplaceGrantPosture(proof, GovernedLoopEffectAuthorityGrantPosture.NotEffective);
            failure = GovernedLoopEffectAuthorityReason.GrantNotEffective;
            return false;
        }

        if (grant.Boundary.ExpiresAtUtc is { } expiry && expiry <= evaluatedAtUtc)
        {
            proof = ReplaceGrantPosture(proof, GovernedLoopEffectAuthorityGrantPosture.Expired);
            failure = GovernedLoopEffectAuthorityReason.GrantExpired;
            return false;
        }

        if (GovernedLoopEffectAuthorityContractValidator.Validate(proof).IsValid)
        {
            return true;
        }

        proof = null;
        failure = GovernedLoopEffectAuthorityReason.GrantAmbiguous;
        return false;
    }

    private static GovernedLoopEffectAuthorityProof ApplyCapabilityPosture(
        GovernedLoopEffectAuthorityProof current,
        CapabilityRevalidationResult? capability)
    {
        var reported = capability?.EffectivePins ?? [];
        var active = reported.Where(pin => current.Ceiling.Capabilities.Contains(pin.DescriptorIdentity)).ToArray();
        var observed = capability?.ObservedPins ?? [];
        var activeIdentities = active.Select(pin => pin.DescriptorIdentity).ToHashSet();
        var ceiling = new AuthorityCeiling(
            current.Ceiling.Capabilities.Where(activeIdentities.Contains).ToArray(),
            current.Ceiling.DataClasses,
            current.Ceiling.MaxTargetCount,
            current.Ceiling.MaxSideEffectClass,
            current.Ceiling.AllowsRecurrence,
            current.Ceiling.AllowsExternalPublication,
            current.Ceiling.AllowsIrreversibleAction);
        return new GovernedLoopEffectAuthorityProof(
            current.SchemaVersion,
            current.Grant,
            current.Binding,
            current.GrantStatus,
            current.GrantPosture,
            current.Boundary,
            ceiling,
            active,
            observed,
            current.DependencyEvidenceHash);
    }

    private static (GovernedLoopEffectAuthorityDisposition Disposition, GovernedLoopEffectAuthorityReason Reason) CapabilityDisposition(
        GovernedLoopEffectAuthorityRequest request,
        CapabilityRevalidationResult? capability,
        GovernedLoopEffectAuthorityProof current)
    {
        if (capability is null)
        {
            return (GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.CapabilityAmbiguous);
        }

        var status = capability.Status == CapabilityRevalidationStatus.Unknown && capability.IsValid
            ? CapabilityRevalidationStatus.Active
            : capability.Status;
        if (capability.IsValid != (status == CapabilityRevalidationStatus.Active))
        {
            return (GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.CapabilityAmbiguous);
        }

        if (status == CapabilityRevalidationStatus.CatalogUnavailable)
        {
            return (GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.CapabilityUnavailable);
        }

        if (status is CapabilityRevalidationStatus.InvalidSnapshot or CapabilityRevalidationStatus.WorkspaceMismatch or CapabilityRevalidationStatus.CatalogAmbiguous or CapabilityRevalidationStatus.Unknown)
        {
            return (GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.CapabilityAmbiguous);
        }

        if (request.RequiredCapabilityPins.Any(required => current.ObservedCapabilityPins.Any(observed => observed.DescriptorIdentity.Id.Equals(required.DescriptorIdentity.Id))))
        {
            return (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.CapabilityDrifted);
        }

        if (request.RequiredCapabilityPins.Any(required => !current.CapabilityPins.Contains(required)))
        {
            return (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.CapabilityInactive);
        }

        if (!IsEqualOrNarrow(request.RequiredAuthority, current.Ceiling))
        {
            return (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.EffectOutsideCeiling);
        }

        return (GovernedLoopEffectAuthorityDisposition.Direct, IsExactCurrent(request, current) ? GovernedLoopEffectAuthorityReason.ActiveExact : GovernedLoopEffectAuthorityReason.ActiveNarrowed);
    }

    private async Task<GovernedLoopEffectAuthorityUsageStoreResult> ReserveUsageAsync(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityProof admitted,
        GovernedLoopEffectAuthorityProof current,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var usageRequest = new GovernedLoopEffectAuthorityUsageRequest(
            GovernedLoopEffectAuthorityUsageRequest.CurrentSchemaVersion,
            admitted.Grant,
            admitted.Boundary.CompletionConstraint,
            request.AdmissionReceipt.ContentHash,
            request.ExecutionBinding.RunId,
            request.ExecutionBinding.ExecutionGeneration,
            request.NodeId,
            request.NodeAttempt,
            request.EffectOperationId,
            request.BoundaryKind,
            current.Ceiling.MaxTargetCount,
            request.TargetFingerprint,
            evaluatedAtUtc);
        try
        {
            return await _usageStore.ReserveAsync(usageRequest, cancellationToken).ConfigureAwait(false)
                ?? new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.Ambiguous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopEffectAuthorityUsageStoreResult(GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable);
        }
    }

    private static (
        GovernedLoopEffectAuthorityProof Proof,
        GovernedLoopEffectAuthorityDisposition Disposition,
        GovernedLoopEffectAuthorityReason Reason) ApplyUsagePosture(
            GovernedLoopEffectAuthorityProof current,
            GovernedLoopEffectAuthorityReason directReason,
            GovernedLoopEffectAuthorityUsageStoreResult usage)
    {
        return usage.Status switch
        {
            GovernedLoopEffectAuthorityUsageStoreStatus.Allowed
                or GovernedLoopEffectAuthorityUsageStoreStatus.TargetReserved
                or GovernedLoopEffectAuthorityUsageStoreStatus.TargetAlreadyReserved
                => (current, GovernedLoopEffectAuthorityDisposition.Direct, directReason),
            GovernedLoopEffectAuthorityUsageStoreStatus.TargetLimitExceeded
                => (ReplaceTargetCount(current, 0), GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.EffectOutsideCeiling),
            GovernedLoopEffectAuthorityUsageStoreStatus.GrantCompleted
                => (ReplaceGrantPosture(current, GovernedLoopEffectAuthorityGrantPosture.Completed), GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantCompleted),
            GovernedLoopEffectAuthorityUsageStoreStatus.Unavailable
                => (current, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceUnavailable),
            GovernedLoopEffectAuthorityUsageStoreStatus.Conflict
                => (current, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceConflict),
            _ => (current, GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.EvidenceAmbiguous),
        };
    }

    private static bool IsExactCurrent(GovernedLoopEffectAuthorityRequest request, GovernedLoopEffectAuthorityProof current)
        => AuthorityCeilingSubset.IsEqual(current.Ceiling, request.AdmissionReceipt.Evidence.EffectiveAuthority)
            && current.CapabilityPins.Count == request.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Count
            && current.CapabilityPins.All(request.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Contains)
            && Equals(current.Boundary, request.AdmissionReceipt.Evidence.GrantBoundary);

    private static GovernedLoopEffectAuthorityDecision CreateGrantStoppedDecision(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityProof admitted,
        AuthorityGrantResolution resolution,
        DateTimeOffset evaluatedAtUtc)
    {
        var (disposition, reason, posture) = GrantDisposition(resolution.Status);
        GovernedLoopEffectAuthorityProof? current = null;
        var grant = resolution.Status == AuthorityGrantResolutionStatus.Stale
            ? resolution.CurrentGrant
            : resolution.CurrentGrant ?? resolution.Grant;
        if (grant is not null && TryReference(grant, out var reference) && posture is not null)
        {
            var ceiling = Intersect(admitted.Ceiling, grant.RequestedCeiling);
            current = new GovernedLoopEffectAuthorityProof(
                GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
                reference!,
                grant.Binding,
                grant.Status,
                posture.Value,
                grant.Boundary,
                ceiling,
                [],
                [],
                IsHash(resolution.DependencyEvidenceHash) ? resolution.DependencyEvidenceHash : null);
        }

        if (current is null && reason is not (GovernedLoopEffectAuthorityReason.GrantMissing or GovernedLoopEffectAuthorityReason.GrantInvalid or GovernedLoopEffectAuthorityReason.GrantUnavailable or GovernedLoopEffectAuthorityReason.GrantAmbiguous))
        {
            disposition = GovernedLoopEffectAuthorityDisposition.Pause;
            reason = GovernedLoopEffectAuthorityReason.GrantAmbiguous;
        }

        return CreateDecision(request, admitted, current, disposition, reason, evaluatedAtUtc);
    }

    private static (GovernedLoopEffectAuthorityDisposition Disposition, GovernedLoopEffectAuthorityReason Reason, GovernedLoopEffectAuthorityGrantPosture? Posture) GrantDisposition(
        AuthorityGrantResolutionStatus status)
    {
        return status switch
        {
            AuthorityGrantResolutionStatus.NotEffective => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantNotEffective, GovernedLoopEffectAuthorityGrantPosture.NotEffective),
            AuthorityGrantResolutionStatus.Suspended => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantSuspended, GovernedLoopEffectAuthorityGrantPosture.Suspended),
            AuthorityGrantResolutionStatus.Revoked => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantRevoked, GovernedLoopEffectAuthorityGrantPosture.Revoked),
            AuthorityGrantResolutionStatus.Expired => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantExpired, GovernedLoopEffectAuthorityGrantPosture.Expired),
            AuthorityGrantResolutionStatus.Stale => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantStale, GovernedLoopEffectAuthorityGrantPosture.Stale),
            AuthorityGrantResolutionStatus.ProfileUnavailable => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.ProfileUnavailable, GovernedLoopEffectAuthorityGrantPosture.ProfileUnavailable),
            AuthorityGrantResolutionStatus.RoleUnavailable => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.RoleUnavailable, GovernedLoopEffectAuthorityGrantPosture.RoleUnavailable),
            AuthorityGrantResolutionStatus.LoopUnavailable => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.LoopUnavailable, GovernedLoopEffectAuthorityGrantPosture.LoopUnavailable),
            AuthorityGrantResolutionStatus.CeilingExceeded => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.CeilingExceeded, GovernedLoopEffectAuthorityGrantPosture.CeilingExceeded),
            AuthorityGrantResolutionStatus.NotFound => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantMissing, null),
            AuthorityGrantResolutionStatus.Invalid => (GovernedLoopEffectAuthorityDisposition.Deny, GovernedLoopEffectAuthorityReason.GrantInvalid, null),
            AuthorityGrantResolutionStatus.Unavailable => (GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.GrantUnavailable, null),
            _ => (GovernedLoopEffectAuthorityDisposition.Pause, GovernedLoopEffectAuthorityReason.GrantAmbiguous, null),
        };
    }

    private static GovernedLoopEffectAuthorityDecision CreateDecision(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityProof admitted,
        GovernedLoopEffectAuthorityProof? current,
        GovernedLoopEffectAuthorityDisposition disposition,
        GovernedLoopEffectAuthorityReason reason,
        DateTimeOffset evaluatedAtUtc)
    {
        var effective = disposition == GovernedLoopEffectAuthorityDisposition.Direct
            ? request.RequiredAuthority
            : AuthorityCeilingIntersection.EmptyCeiling();
        return GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
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
            evaluatedAtUtc,
            string.Empty));
    }

    private static GovernedLoopEffectAuthorityDecision? CreateEvidenceStoppedDecision(
        GovernedLoopEffectAuthorityRequest request,
        GovernedLoopEffectAuthorityProof admitted,
        GovernedLoopEffectAuthorityProof current,
        GovernedLoopEffectAuthorityEvidenceStoreStatus status,
        DateTimeOffset evaluatedAtUtc)
    {
        var reason = status switch
        {
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Conflict => GovernedLoopEffectAuthorityReason.EvidenceConflict,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable => GovernedLoopEffectAuthorityReason.EvidenceUnavailable,
            _ => GovernedLoopEffectAuthorityReason.EvidenceAmbiguous,
        };
        var decision = CreateDecision(request, admitted, current, GovernedLoopEffectAuthorityDisposition.Pause, reason, evaluatedAtUtc);
        return GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid ? decision : null;
    }

    private async Task<GovernedLoopEffectAuthorityEvidenceStoreResult> AppendAsync(
        GovernedLoopEffectAuthorityDecision decision,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _evidenceStore.AppendAsync(decision, cancellationToken).ConfigureAwait(false)
                ?? new GovernedLoopEffectAuthorityEvidenceStoreResult(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopEffectAuthorityEvidenceStoreResult(GovernedLoopEffectAuthorityEvidenceStoreStatus.Unavailable, null);
        }
    }

    private static bool IsExactStoredDecision(
        GovernedLoopEffectAuthorityEvidenceStoreResult result,
        GovernedLoopEffectAuthorityDecision decision)
    {
        return result.Status is GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
            && string.Equals(result.ContentHash, decision.ContentHash, StringComparison.Ordinal);
    }

    private static GovernedLoopEffectAuthorityEvidenceStoreResult NormalizeEvidenceResult(
        GovernedLoopEffectAuthorityEvidenceStoreResult result,
        GovernedLoopEffectAuthorityDecision decision)
    {
        if (result.Status is GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended or GovernedLoopEffectAuthorityEvidenceStoreStatus.AlreadyPresent
            && !string.Equals(result.ContentHash, decision.ContentHash, StringComparison.Ordinal))
        {
            return new GovernedLoopEffectAuthorityEvidenceStoreResult(GovernedLoopEffectAuthorityEvidenceStoreStatus.Ambiguous, result.ContentHash);
        }

        return result;
    }

    private bool TryGetUtcNow(out DateTimeOffset value)
    {
        value = default;
        try
        {
            var candidate = _timeProvider.GetUtcNow();
            if (!IsTrustedUtc(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryReference(AuthorityGrant grant, out AuthorityGrantReference? reference)
    {
        reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        if (!AuthorityGrantContractValidator.Validate(grant).IsValid)
        {
            reference = null;
            return false;
        }

        return true;
    }

    private static AuthorityCeiling Intersect(AuthorityCeiling admitted, AuthorityCeiling current)
    {
        if (!AuthorityProfileValidator.ValidateCeiling(current).IsValid)
        {
            return AuthorityCeilingIntersection.EmptyCeiling();
        }

        return new AuthorityCeiling(
            admitted.Capabilities.Intersect(current.Capabilities).ToArray(),
            admitted.DataClasses.Intersect(current.DataClasses).ToArray(),
            Math.Min(admitted.MaxTargetCount, current.MaxTargetCount),
            (CapabilitySideEffectClass)Math.Min((int)admitted.MaxSideEffectClass, (int)current.MaxSideEffectClass),
            admitted.AllowsRecurrence && current.AllowsRecurrence,
            admitted.AllowsExternalPublication && current.AllowsExternalPublication,
            admitted.AllowsIrreversibleAction && current.AllowsIrreversibleAction);
    }

    private static bool IsEqualOrNarrow(AuthorityCeiling candidate, AuthorityCeiling current)
        => AuthorityCeilingSubset.IsEqual(candidate, current) || AuthorityCeilingSubset.IsStrictSubset(candidate, current);

    private static bool IsHash(string? value)
        => value?.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsTrustedUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsActiveAt(AuthorityGrantBoundary boundary, DateTimeOffset evaluatedAtUtc)
        => evaluatedAtUtc >= boundary.EffectiveAtUtc
            && (boundary.ExpiresAtUtc is null || evaluatedAtUtc < boundary.ExpiresAtUtc);

    private static GovernedLoopEffectAuthorityProof ReplaceGrantPosture(
        GovernedLoopEffectAuthorityProof proof,
        GovernedLoopEffectAuthorityGrantPosture posture)
        => new(
            proof.SchemaVersion,
            proof.Grant,
            proof.Binding,
            proof.GrantStatus,
            posture,
            proof.Boundary,
            proof.Ceiling,
            proof.CapabilityPins,
            proof.ObservedCapabilityPins,
            null);

    private static GovernedLoopEffectAuthorityProof ReplaceTargetCount(
        GovernedLoopEffectAuthorityProof proof,
        int maxTargetCount)
        => new(
            proof.SchemaVersion,
            proof.Grant,
            proof.Binding,
            proof.GrantStatus,
            proof.GrantPosture,
            proof.Boundary,
            new AuthorityCeiling(
                proof.Ceiling.Capabilities,
                proof.Ceiling.DataClasses,
                maxTargetCount,
                proof.Ceiling.MaxSideEffectClass,
                proof.Ceiling.AllowsRecurrence,
                proof.Ceiling.AllowsExternalPublication,
                proof.Ceiling.AllowsIrreversibleAction),
            proof.CapabilityPins,
            proof.ObservedCapabilityPins,
            proof.DependencyEvidenceHash);

    private static GovernedLoopEffectAuthorityExecutionResult<TResult> Result<TResult>(
        GovernedLoopEffectAuthorityExecutionStatus status,
        GovernedLoopEffectAuthorityDecision? decision,
        GovernedLoopEffectAuthorityEvidenceStoreStatus evidenceStatus,
        string detail,
        string? storedDecisionContentHash = null)
        => new(status, decision, evidenceStatus, false, default, detail, storedDecisionContentHash);
}
