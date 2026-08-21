using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Governance.Authority.Delegation;

/// <summary>Creates and revalidates exact delegated-authority evidence under one shared workspace authority fence.</summary>
/// <remarks>An envelope grants nothing by possession. A later owning boundary must revalidate it and enforce its own operation semantics. Exact create replay reservations are scoped to this service instance; durable replay across service recreation or hosts is tracked by <see href="https://github.com/Jacob-J-Thomas/agenthome-poc/issues/468">issue #468</see>.</remarks>
public sealed class AuthorityDelegationEnvelopeService : IAuthorityDelegationEnvelopeService
{
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IAuthorityDelegationOriginResolver _originResolver;
    private readonly IAuthorityDelegationTargetResolver _targetResolver;
    private readonly IAuthorityDelegationCompletionSource _completionSource;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;
    private readonly object _createOperationsSync = new();
    // Durable replay across service recreation or hosts is intentionally deferred to issue #468; this map is one service-instance fence.
    private readonly Dictionary<string, AuthorityDelegationCreateOperation> _createOperations = new(StringComparer.Ordinal);

    /// <summary>Creates one fail-closed delegated-authority evidence service.</summary>
    public AuthorityDelegationEnvelopeService(
        IAuthorityGrantResolver grantResolver,
        IAuthorityDelegationOriginResolver originResolver,
        IAuthorityDelegationTargetResolver targetResolver,
        IAuthorityDelegationCompletionSource completionSource,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _originResolver = originResolver ?? throw new ArgumentNullException(nameof(originResolver));
        _targetResolver = targetResolver ?? throw new ArgumentNullException(nameof(targetResolver));
        _completionSource = completionSource ?? throw new ArgumentNullException(nameof(completionSource));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AuthorityDelegationServiceResult> CreateAsync(AuthorityDelegationCreateRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotCreateRequest(request);
        if (snapshot is null || !IsValidCreateRequest(snapshot))
        {
            return Result(AuthorityDelegationServiceStatus.InvalidContract);
        }

        AuthorityDelegationCreateOperation operation;
        var ownsOperation = false;
        lock (_createOperationsSync)
        {
            if (_createOperations.TryGetValue(snapshot.EnvelopeId, out var existing))
            {
                if (!SameCreateRequest(existing.Request, snapshot))
                {
                    return Result(AuthorityDelegationServiceStatus.EnvelopeIdConflict);
                }

                operation = existing;
            }
            else
            {
                operation = new AuthorityDelegationCreateOperation(snapshot);
                _createOperations.Add(snapshot.EnvelopeId, operation);
                ownsOperation = true;
            }
        }

        if (!ownsOperation)
        {
            operation.AddWaiter();
        }

        var waiterReleased = 0;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (Interlocked.Exchange(ref waiterReleased, 1) == 0)
            {
                operation.ReleaseWaiter();
            }
        });

        // Register the public cancellation boundary before owner execution; see https://github.com/Jacob-J-Thomas/agenthome-poc/issues/482.
        if (ownsOperation)
        {
            _ = ExecuteCreateOperationAsync(operation);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completed = await operation.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return !ownsOperation && completed.Status == AuthorityDelegationServiceStatus.Created
                ? Result(AuthorityDelegationServiceStatus.Replayed, completed.Envelope)
                : completed;
        }
        finally
        {
            if (Interlocked.Exchange(ref waiterReleased, 1) == 0)
            {
                operation.ReleaseWaiter();
            }
        }
    }

    private async Task ExecuteCreateOperationAsync(AuthorityDelegationCreateOperation operation)
    {
        await Task.Yield();
        try
        {
            var result = await ExecuteUnderFenceAsync(
                transactionToken => CreateUnderFenceAsync(operation.Request, transactionToken),
                operation.ExecutionCancellationToken).ConfigureAwait(false);
            CompleteCreateOperation(operation, result);
        }
        catch (OperationCanceledException)
        {
            CompleteCreateOperation(operation, null);
        }
        catch (Exception)
        {
            CompleteCreateOperation(operation, null);
        }
    }

    /// <inheritdoc />
    public async Task<AuthorityDelegationServiceResult> RevalidateAsync(AuthorityDelegationUseRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = SnapshotUseRequest(request);
        var staticStatus = ValidateUseRequest(snapshot);
        if (staticStatus is { } failure)
        {
            return Result(failure);
        }

        return await ExecuteUnderFenceAsync(
            transactionToken => RevalidateUnderFenceAsync(snapshot!, transactionToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthorityDelegationServiceResult> ExecuteUnderFenceAsync(
        Func<CancellationToken, Task<AuthorityDelegationServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        AuthorityDelegationServiceResult? callbackResult = null;
        var callbackAttempts = 0;
        var callbackState = 0;
        try
        {
            var transactionResult = await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    Interlocked.Increment(ref callbackAttempts);
                    if (Interlocked.CompareExchange(ref callbackState, 1, 0) != 0)
                    {
                        return Result(AuthorityDelegationServiceStatus.Unavailable);
                    }

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transactionToken);
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        var result = await operation(linkedCancellation.Token).ConfigureAwait(false);
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        callbackResult = result;
                        return result;
                    }
                    finally
                    {
                        Volatile.Write(ref callbackState, 2);
                    }
                },
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref callbackState, 2);
            return Volatile.Read(ref callbackAttempts) == 1
                && callbackResult is not null
                && transactionResult is not null
                && ReferenceEquals(transactionResult, callbackResult)
                    ? transactionResult
                    : Result(AuthorityDelegationServiceStatus.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && callbackResult is null)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }
    }

    private async Task<AuthorityDelegationServiceResult> CreateUnderFenceAsync(AuthorityDelegationCreateRequest request, CancellationToken cancellationToken)
    {
        var windowStart = UtcNow();
        if (windowStart == default)
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var observedAtUtc = windowStart;

        var receipt = request.ParentAdmission;
        if (receipt.RecordedAtUtc > windowStart
            || receipt.Evidence.EvaluatedAtUtc > windowStart
            || receipt.Evidence.CapabilityAdmission.AdmittedAtUtc > windowStart)
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        if (!TryNormalizeBoundary(
            request.Boundary,
            receipt.Evidence.GrantBoundary,
            receipt.Evidence.EvaluatedAtUtc,
            windowStart,
            out _))
        {
            return Result(AuthorityDelegationServiceStatus.InvalidContract);
        }

        var grantResolution = await _grantResolver.ResolveAsync(receipt.Intent.AuthorityGrant, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var grantFailure = ValidateGrantResolution(grantResolution, receipt, windowStart, observedAtUtc);
        if (grantFailure is { } grantStatus)
        {
            return Result(grantStatus);
        }

        var parentPins = AdmissionPins(receipt);
        if (parentPins is null
            || !IsWithin(receipt.Evidence.EffectiveAuthority, grantResolution!.EffectiveCeiling))
        {
            return Result(AuthorityDelegationServiceStatus.OutsideParentAuthority);
        }

        var origin = await _originResolver.ResolveForCreationAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var originFailure = ValidateCreationOrigin(origin, request, receipt, parentPins);
        if (originFailure is { } originStatus)
        {
            return Result(originStatus);
        }

        if (!IsWithin(request.DelegatedCeiling, origin!.DeclaredAuthorityMaximum))
        {
            return Result(AuthorityDelegationServiceStatus.OutsideParentAuthority);
        }

        var target = await _targetResolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var targetFailure = ValidateTargetResolution(target, request.Target, receipt.Intent.WorkspaceId);
        if (targetFailure is { } targetStatus)
        {
            return Result(targetStatus);
        }

        var completion = await _completionSource.ResolveAsync(receipt.Intent.WorkspaceId, receipt.Evidence.Binding, request.Target, cancellationToken).ConfigureAwait(false);
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var completionFailure = MapCompletion(completion, request.Boundary.CompletionConstraint);
        if (completionFailure is { } completionStatus)
        {
            return Result(completionStatus);
        }

        var issueAtUtc = observedAtUtc;
        var finalGrantFailure = ValidateGrantAtInstant(grantResolution!.Grant!, issueAtUtc);
        if (finalGrantFailure is { } finalGrantStatus)
        {
            return Result(finalGrantStatus);
        }

        if (!TryNormalizeBoundary(
            request.Boundary,
            receipt.Evidence.GrantBoundary,
            receipt.Evidence.EvaluatedAtUtc,
            issueAtUtc,
            out var effectiveBoundary))
        {
            return Result(AuthorityDelegationServiceStatus.InvalidContract);
        }

        var parentEvidenceCandidate = new AuthorityDelegationParentEvidenceReference(
            receipt.Intent.WorkspaceId,
            receipt.Evidence.Binding,
            request.OriginNodeId,
            request.OriginNodeAttempt,
            receipt.ContentHash,
            receipt.Intent.ActorId,
            receipt.Intent.AuthorityGrant,
            grantResolution!.Grant!.Binding,
            origin!.EvidenceHash,
            grantResolution.DependencyEvidenceHash,
            issueAtUtc,
            string.Empty);
        var parentEvidence = AuthorityDelegationContractHash.Apply(parentEvidenceCandidate);
        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            receipt.Evidence.EffectiveAuthority,
            parentPins,
            target!.RoleCapabilityIds,
            target.LoopCapabilityIds,
            target.NodeCapabilityIds,
            request.DelegatedCeiling,
            request.DelegatedCapabilityPins,
            parentEvidence.ContentHash,
            target.TargetMaximumEvidenceHash);
        if (proof is null)
        {
            return Result(AuthorityDelegationServiceStatus.OutsideParentAuthority);
        }

        var revocationLink = AuthorityDelegationContractHash.Apply(new AuthorityDelegationRevocationLink(
            parentEvidence.GrantReference,
            parentEvidence.ParentAdmissionReceiptHash,
            parentEvidence.WorkspaceId,
            parentEvidence.ParentExecution.RunId,
            parentEvidence.ParentExecution.ExecutionGeneration,
            string.Empty));
        var envelopeCandidate = new AuthorityDelegationEnvelope(
            AuthorityDelegationEnvelope.CurrentSchemaVersion,
            request.EnvelopeId,
            parentEvidence,
            target.Target,
            request.DelegatedCeiling,
            request.DelegatedCapabilityPins,
            request.TargetClass,
            request.OperationClass,
            request.Purpose,
            effectiveBoundary!,
            revocationLink,
            proof,
            issueAtUtc,
            string.Empty);
        AuthorityDelegationEnvelope envelope;
        try
        {
            envelope = AuthorityDelegationContractHash.Apply(envelopeCandidate);
        }
        catch (ArgumentException)
        {
            return Result(AuthorityDelegationServiceStatus.InvalidContract);
        }

        return AuthorityDelegationContractValidator.Validate(envelope).IsValid
            ? Result(AuthorityDelegationServiceStatus.Created, envelope)
            : Result(AuthorityDelegationServiceStatus.InvalidContract);
    }

    private async Task<AuthorityDelegationServiceResult> RevalidateUnderFenceAsync(AuthorityDelegationUseRequest request, CancellationToken cancellationToken)
    {
        var envelope = request.Envelope;
        var windowStart = UtcNow();
        if (windowStart == default)
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var observedAtUtc = windowStart;

        if (windowStart < envelope.Boundary.EffectiveAtUtc)
        {
            return Result(AuthorityDelegationServiceStatus.EnvelopeNotEffective);
        }

        if (envelope.Boundary.ExpiresAtUtc is { } expiry && windowStart >= expiry)
        {
            return Result(AuthorityDelegationServiceStatus.EnvelopeExpired);
        }

        var completion = await _completionSource.ResolveAsync(request.WorkspaceId, request.ParentExecution, request.Target, cancellationToken).ConfigureAwait(false);
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var completionFailure = MapCompletion(completion, envelope.Boundary.CompletionConstraint);
        if (completionFailure is { } completionStatus)
        {
            return Result(completionStatus);
        }

        var grantResolution = await _grantResolver.ResolveAsync(envelope.ParentEvidence.GrantReference, cancellationToken).ConfigureAwait(false);
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var grantFailure = ValidateGrantResolution(grantResolution, envelope.ParentEvidence, windowStart, observedAtUtc);
        if (grantFailure is { } grantStatus)
        {
            return Result(grantStatus);
        }

        if (!BoundaryFitsParent(envelope.Boundary, grantResolution!.Grant!.Boundary))
        {
            return Result(AuthorityDelegationServiceStatus.ParentReplaced);
        }

        var origin = await _originResolver.ResolveForUseAsync(request, cancellationToken).ConfigureAwait(false);
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var originFailure = ValidateUseOrigin(origin, request);
        if (originFailure is { } originStatus)
        {
            return Result(originStatus);
        }

        if (!IsWithin(origin!.ParentEffectiveAuthority, grantResolution.EffectiveCeiling)
            || !IsWithin(envelope.DelegatedCeiling, origin.DeclaredAuthorityMaximum))
        {
            return Result(AuthorityDelegationServiceStatus.ParentReplaced);
        }

        var target = await _targetResolver.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (!TryAdvanceTrustedTime(ref observedAtUtc))
        {
            return Result(AuthorityDelegationServiceStatus.Unavailable);
        }

        var targetFailure = ValidateTargetResolution(target, envelope.Target, envelope.ParentEvidence.WorkspaceId);
        if (targetFailure is { } targetStatus)
        {
            return Result(targetStatus);
        }

        if (observedAtUtc < envelope.Boundary.EffectiveAtUtc)
        {
            return Result(AuthorityDelegationServiceStatus.EnvelopeNotEffective);
        }

        if (envelope.Boundary.ExpiresAtUtc is { } finalExpiry && observedAtUtc >= finalExpiry)
        {
            return Result(AuthorityDelegationServiceStatus.EnvelopeExpired);
        }

        var finalGrantFailure = ValidateGrantAtInstant(grantResolution!.Grant!, observedAtUtc);
        if (finalGrantFailure is { } finalGrantStatus)
        {
            return Result(finalGrantStatus);
        }

        var proof = AuthorityDelegationSubsetEvaluator.Evaluate(
            origin.ParentEffectiveAuthority,
            origin.ParentCapabilityPins,
            target!.RoleCapabilityIds,
            target.LoopCapabilityIds,
            target.NodeCapabilityIds,
            envelope.DelegatedCeiling,
            envelope.DelegatedCapabilityPins,
            envelope.ParentEvidence.ContentHash,
            target.TargetMaximumEvidenceHash);
        if (proof is null || !SameProof(proof, envelope.SubsetProof))
        {
            return Result(AuthorityDelegationServiceStatus.ParentReplaced);
        }

        return Result(AuthorityDelegationServiceStatus.Valid, envelope);
    }

    private static bool IsValidCreateRequest(AuthorityDelegationCreateRequest? request)
    {
        if (request is null
            || !GovernedLoopAdmissionValidator.Validate(request.ParentAdmission).IsValid
            || !IsToken(request.OriginNodeId, AuthorityDelegationContractLimits.MaxIdentifierCharacters)
            || request.OriginNodeAttempt is < 1 or > GovernedLoopExecutionLimits.MaxNodeAttempt
            || !IsToken(request.EnvelopeId, AuthorityDelegationContractLimits.MaxIdentifierCharacters)
            || !AuthorityDelegationContractValidator.Validate(request.Target).IsValid
            || !AuthorityDelegationContractValidator.Validate(request.Boundary).IsValid
            || !IsToken(request.TargetClass, AuthorityDelegationContractLimits.MaxClassTokenCharacters)
            || !IsToken(request.OperationClass, AuthorityDelegationContractLimits.MaxClassTokenCharacters)
            || request.Purpose is null
            || !AuthorityPurpose.TryParse(request.Purpose.Value, out var purpose, out _)
            || !purpose!.Equals(request.Purpose))
        {
            return false;
        }

        try
        {
            _ = AuthorityDelegationContractHash.ComputeAuthorityScopeHash(request.DelegatedCeiling, request.DelegatedCapabilityPins);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static AuthorityDelegationCreateRequest? SnapshotCreateRequest(AuthorityDelegationCreateRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        try
        {
            var ceiling = AuthorityDelegationApplicationCopy.Copy(request.DelegatedCeiling);
            var pins = AuthorityDelegationApplicationCopy.CopyPins(request.DelegatedCapabilityPins);
            var target = CopyTarget(request.Target);
            if (ceiling is null || pins is null || target is null)
            {
                return null;
            }

            return new AuthorityDelegationCreateRequest(
                request.ParentAdmission,
                request.OriginNodeId,
                request.OriginNodeAttempt,
                request.EnvelopeId,
                target,
                ceiling,
                pins,
                request.TargetClass,
                request.OperationClass,
                request.Purpose,
                new AuthorityDelegationBoundary(
                    request.Boundary.EffectiveAtUtc,
                    request.Boundary.ExpiresAtUtc,
                    request.Boundary.CompletionConstraint));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AuthorityDelegationUseRequest? SnapshotUseRequest(AuthorityDelegationUseRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        try
        {
            var envelope = new AuthorityDelegationEnvelope(
                request.Envelope.SchemaVersion,
                request.Envelope.EnvelopeId,
                request.Envelope.ParentEvidence,
                request.Envelope.Target,
                request.Envelope.DelegatedCeiling,
                request.Envelope.DelegatedCapabilityPins,
                request.Envelope.TargetClass,
                request.Envelope.OperationClass,
                request.Envelope.Purpose,
                request.Envelope.Boundary,
                request.Envelope.RevocationLink,
                request.Envelope.SubsetProof,
                request.Envelope.IssuedAtUtc,
                request.Envelope.ContentHash);
            var target = CopyTarget(request.Target);
            return target is null
                ? null
                : new AuthorityDelegationUseRequest(
                    envelope,
                    request.WorkspaceId,
                    request.ParentExecution,
                    request.OriginNodeId,
                    request.OriginNodeAttempt,
                    target,
                    request.TargetClass,
                    request.OperationClass,
                    request.Purpose);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AuthorityDelegationTargetBinding? CopyTarget(AuthorityDelegationTargetBinding? target)
        => target is null
            ? null
            : new AuthorityDelegationTargetBinding(
                target.Kind,
                target.Role,
                target.Loop,
                target.NodeId,
                target.BindingEvidenceHash);

    private void CompleteCreateOperation(AuthorityDelegationCreateOperation operation, AuthorityDelegationServiceResult? result)
    {
        var committed = operation.Complete(result?.Status == AuthorityDelegationServiceStatus.Created);
        if (committed
            && result is not null)
        {
            operation.Completion.TrySetResult(result);
            return;
        }

        lock (_createOperationsSync)
        {
            if (_createOperations.TryGetValue(operation.Request.EnvelopeId, out var current) && ReferenceEquals(current, operation))
            {
                _createOperations.Remove(operation.Request.EnvelopeId);
            }
        }

        operation.Completion.TrySetResult(result?.Status == AuthorityDelegationServiceStatus.Created
            ? Result(AuthorityDelegationServiceStatus.Unavailable)
            : result ?? Result(AuthorityDelegationServiceStatus.Unavailable));
    }

    private static bool SameCreateRequest(AuthorityDelegationCreateRequest left, AuthorityDelegationCreateRequest right)
    {
        try
        {
            return string.Equals(left.ParentAdmission.ContentHash, right.ParentAdmission.ContentHash, StringComparison.Ordinal)
                && string.Equals(left.OriginNodeId, right.OriginNodeId, StringComparison.Ordinal)
                && left.OriginNodeAttempt == right.OriginNodeAttempt
                && string.Equals(left.EnvelopeId, right.EnvelopeId, StringComparison.Ordinal)
                && left.Target == right.Target
                && SameCeiling(left.DelegatedCeiling, right.DelegatedCeiling)
                && left.DelegatedCapabilityPins.SequenceEqual(right.DelegatedCapabilityPins)
                && string.Equals(left.TargetClass, right.TargetClass, StringComparison.Ordinal)
                && string.Equals(left.OperationClass, right.OperationClass, StringComparison.Ordinal)
                && Equals(left.Purpose, right.Purpose)
                && left.Boundary == right.Boundary;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SameCeiling(AuthorityCeiling left, AuthorityCeiling right)
        => left.MaxTargetCount == right.MaxTargetCount
            && left.MaxSideEffectClass == right.MaxSideEffectClass
            && left.AllowsRecurrence == right.AllowsRecurrence
            && left.AllowsExternalPublication == right.AllowsExternalPublication
            && left.AllowsIrreversibleAction == right.AllowsIrreversibleAction
            && left.Capabilities.SequenceEqual(right.Capabilities)
            && left.DataClasses.SequenceEqual(right.DataClasses);

    private static AuthorityDelegationServiceStatus? ValidateUseRequest(AuthorityDelegationUseRequest? request)
    {
        if (request is null || !AuthorityDelegationContractValidator.Validate(request.Envelope).IsValid)
        {
            return AuthorityDelegationServiceStatus.InvalidContract;
        }

        var parent = request.Envelope.ParentEvidence;
        if (!string.Equals(request.WorkspaceId, parent.WorkspaceId, StringComparison.Ordinal)
            || request.ParentExecution != parent.ParentExecution
            || !string.Equals(request.OriginNodeId, parent.OriginNodeId, StringComparison.Ordinal)
            || request.OriginNodeAttempt != parent.OriginNodeAttempt)
        {
            return AuthorityDelegationServiceStatus.OriginMismatch;
        }

        if (request.Target != request.Envelope.Target
            || !string.Equals(request.TargetClass, request.Envelope.TargetClass, StringComparison.Ordinal)
            || !string.Equals(request.OperationClass, request.Envelope.OperationClass, StringComparison.Ordinal)
            || request.Purpose is null
            || !request.Purpose.Equals(request.Envelope.Purpose))
        {
            return AuthorityDelegationServiceStatus.TargetMismatch;
        }

        return null;
    }

    private static AuthorityDelegationServiceStatus? ValidateGrantResolution(
        AuthorityGrantResolution? resolution,
        GovernedLoopAdmissionReceipt receipt,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var mapped = MapGrant(resolution?.Status ?? AuthorityGrantResolutionStatus.Unknown);
        if (mapped is not null)
        {
            return mapped;
        }

        var grant = resolution!.Grant;
        if (grant is null
            || !AuthorityGrantContractValidator.Validate(grant).IsValid
            || grant.Status != AuthorityGrantLifecycleStatus.Active
            || !Equals(resolution.RequestedReference, receipt.Intent.AuthorityGrant)
            || !Equals(new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash), receipt.Intent.AuthorityGrant)
            || resolution.CurrentGrant is null
            || !AuthorityGrantContractValidator.Validate(resolution.CurrentGrant).IsValid
            || resolution.CurrentGrant.Status != AuthorityGrantLifecycleStatus.Active
            || !SameCanonicalGrantRevision(resolution.CurrentGrant, grant)
            || !Equals(new AuthorityGrantReference(resolution.CurrentGrant.GrantId, resolution.CurrentGrant.Revision, resolution.CurrentGrant.ContentHash), receipt.Intent.AuthorityGrant))
        {
            return AuthorityDelegationServiceStatus.Ambiguous;
        }

        if (grant.Binding.Profile != receipt.Evidence.GrantProfile
            || grant.Binding.Role != receipt.Intent.Role
            || grant.Binding.Loop != receipt.Intent.Publication
            || grant.Boundary != receipt.Evidence.GrantBoundary
            || !string.Equals(resolution.DependencyEvidenceHash, receipt.Evidence.GrantDependencyEvidenceHash, StringComparison.Ordinal)
            || !IsCanonicalHash(resolution.DependencyEvidenceHash)
            || resolution.EvaluatedAtUtc == default
            || resolution.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || resolution.EvaluatedAtUtc > windowEnd
            || resolution.EvaluatedAtUtc < windowStart
            || resolution.EvaluatedAtUtc < grant.RecordedAtUtc
            || !AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling)
            || !IsWithin(receipt.Evidence.EffectiveAuthority, resolution.EffectiveCeiling))
        {
            return AuthorityDelegationServiceStatus.ParentReplaced;
        }

        return null;
    }

    private static AuthorityDelegationServiceStatus? ValidateGrantResolution(
        AuthorityGrantResolution? resolution,
        AuthorityDelegationParentEvidenceReference parent,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var mapped = MapGrant(resolution?.Status ?? AuthorityGrantResolutionStatus.Unknown);
        if (mapped is not null)
        {
            return mapped;
        }

        var grant = resolution!.Grant;
        if (grant is null
            || !AuthorityGrantContractValidator.Validate(grant).IsValid
            || grant.Status != AuthorityGrantLifecycleStatus.Active
            || !Equals(resolution.RequestedReference, parent.GrantReference)
            || !Equals(new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash), parent.GrantReference)
            || resolution.CurrentGrant is null
            || !AuthorityGrantContractValidator.Validate(resolution.CurrentGrant).IsValid
            || resolution.CurrentGrant.Status != AuthorityGrantLifecycleStatus.Active
            || !SameCanonicalGrantRevision(resolution.CurrentGrant, grant)
            || !Equals(new AuthorityGrantReference(resolution.CurrentGrant.GrantId, resolution.CurrentGrant.Revision, resolution.CurrentGrant.ContentHash), parent.GrantReference)
            || grant.Binding != parent.GrantBinding
            || !string.Equals(resolution.DependencyEvidenceHash, parent.GrantDependencyEvidenceHash, StringComparison.Ordinal)
            || !IsCanonicalHash(resolution.DependencyEvidenceHash)
            || resolution.EvaluatedAtUtc == default
            || resolution.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || resolution.EvaluatedAtUtc > windowEnd
            || resolution.EvaluatedAtUtc < windowStart
            || resolution.EvaluatedAtUtc < grant.RecordedAtUtc
            || !AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling))
        {
            return AuthorityDelegationServiceStatus.ParentReplaced;
        }

        return null;
    }

    private static AuthorityDelegationServiceStatus? ValidateGrantAtInstant(AuthorityGrant grant, DateTimeOffset evaluatedAtUtc)
    {
        if (evaluatedAtUtc < grant.Boundary.EffectiveAtUtc)
        {
            return AuthorityDelegationServiceStatus.ParentNotEffective;
        }

        if (grant.Boundary.ExpiresAtUtc is { } expiry && evaluatedAtUtc >= expiry)
        {
            return AuthorityDelegationServiceStatus.ParentExpired;
        }

        return null;
    }

    private static AuthorityDelegationServiceStatus? ValidateCreationOrigin(
        AuthorityDelegationOriginResolution? origin,
        AuthorityDelegationCreateRequest request,
        GovernedLoopAdmissionReceipt receipt,
        IReadOnlyList<CapabilityAdmissionPin> parentPins)
    {
        var mapped = MapOrigin(origin?.Status ?? AuthorityDelegationOriginResolutionStatus.Unknown);
        if (mapped is not null)
        {
            return mapped;
        }

        if (origin!.ParentExecution != receipt.Evidence.Binding
            || !string.Equals(origin.WorkspaceId, receipt.Intent.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(origin.OriginNodeId, request.OriginNodeId, StringComparison.Ordinal)
            || origin.OriginNodeAttempt != request.OriginNodeAttempt
            || origin.Target != request.Target
            || !string.Equals(origin.TargetClass, request.TargetClass, StringComparison.Ordinal)
            || !string.Equals(origin.OperationClass, request.OperationClass, StringComparison.Ordinal)
            || !Equals(origin.Purpose, request.Purpose)
            || origin.CompletionConstraint != request.Boundary.CompletionConstraint
            || !IsCanonicalHash(origin.EvidenceHash)
            || !AuthorityCeilingSubset.IsEqual(origin.ParentEffectiveAuthority, receipt.Evidence.EffectiveAuthority)
            || !origin.ParentCapabilityPins.SequenceEqual(parentPins))
        {
            return AuthorityDelegationServiceStatus.OriginMismatch;
        }

        return ValidResolutionAuthority(origin.DeclaredAuthorityMaximum, origin.ParentEffectiveAuthority, origin.ParentCapabilityPins)
            ? null
            : AuthorityDelegationServiceStatus.Ambiguous;
    }

    private static AuthorityDelegationServiceStatus? ValidateUseOrigin(AuthorityDelegationOriginResolution? origin, AuthorityDelegationUseRequest request)
    {
        var mapped = MapOrigin(origin?.Status ?? AuthorityDelegationOriginResolutionStatus.Unknown);
        if (mapped is not null)
        {
            return mapped;
        }

        var envelope = request.Envelope;
        if (origin!.ParentExecution != envelope.ParentEvidence.ParentExecution
            || !string.Equals(origin.WorkspaceId, envelope.ParentEvidence.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(origin.OriginNodeId, envelope.ParentEvidence.OriginNodeId, StringComparison.Ordinal)
            || origin.OriginNodeAttempt != envelope.ParentEvidence.OriginNodeAttempt
            || origin.Target != envelope.Target
            || !string.Equals(origin.TargetClass, envelope.TargetClass, StringComparison.Ordinal)
            || !string.Equals(origin.OperationClass, envelope.OperationClass, StringComparison.Ordinal)
            || !Equals(origin.Purpose, envelope.Purpose)
            || origin.CompletionConstraint != envelope.Boundary.CompletionConstraint
            || !string.Equals(origin.EvidenceHash, envelope.ParentEvidence.OriginBindingEvidenceHash, StringComparison.Ordinal))
        {
            return AuthorityDelegationServiceStatus.OriginDrifted;
        }

        return ValidResolutionAuthority(origin.DeclaredAuthorityMaximum, origin.ParentEffectiveAuthority, origin.ParentCapabilityPins)
            ? null
            : AuthorityDelegationServiceStatus.Ambiguous;
    }

    private static AuthorityDelegationServiceStatus? ValidateTargetResolution(
        AuthorityDelegationTargetResolution? target,
        AuthorityDelegationTargetBinding expected,
        string workspaceId)
    {
        var mapped = MapTarget(target?.Status ?? AuthorityDelegationTargetResolutionStatus.Unknown);
        if (mapped is not null)
        {
            return mapped;
        }

        if (target!.Target != expected
            || !string.Equals(target.WorkspaceId, workspaceId, StringComparison.Ordinal)
            || !IsCanonicalHash(target.TargetMaximumEvidenceHash)
            || target.RoleCapabilityIds is null
            || target.LoopCapabilityIds is null
            || target.NodeCapabilityIds is null)
        {
            return AuthorityDelegationServiceStatus.TargetMismatch;
        }

        return null;
    }

    private static IReadOnlyList<CapabilityAdmissionPin>? AdmissionPins(GovernedLoopAdmissionReceipt receipt)
    {
        try
        {
            var capabilities = receipt.Evidence.EffectiveAuthority.Capabilities.ToHashSet();
            var pins = receipt.Evidence.CapabilityAdmission.Pins
                .Where(pin => capabilities.Contains(pin.DescriptorIdentity))
                .OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
                .ThenBy(pin => pin.DescriptorIdentity.Version.Value, StringComparer.Ordinal)
                .ThenBy(pin => pin.DescriptorIdentity.Hash.Value, StringComparer.Ordinal)
                .ToArray();
            return pins.Length == capabilities.Count && pins.Select(pin => pin.DescriptorIdentity).ToHashSet().SetEquals(capabilities)
                ? Array.AsReadOnly(pins)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ValidResolutionAuthority(
        AuthorityCeiling declaredMaximum,
        AuthorityCeiling parentEffectiveAuthority,
        IReadOnlyList<CapabilityAdmissionPin> parentPins)
    {
        try
        {
            _ = AuthorityDelegationContractHash.ComputeAuthorityScopeHash(parentEffectiveAuthority, parentPins);
            return AuthorityProfileValidator.ValidateCeiling(declaredMaximum).IsValid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryNormalizeBoundary(
        AuthorityDelegationBoundary boundary,
        AuthorityGrantBoundary parentBoundary,
        DateTimeOffset admittedAtUtc,
        DateTimeOffset now,
        out AuthorityDelegationBoundary? normalized)
    {
        var effectiveAtUtc = boundary.EffectiveAtUtc < now ? now : boundary.EffectiveAtUtc;
        effectiveAtUtc = effectiveAtUtc < admittedAtUtc ? admittedAtUtc : effectiveAtUtc;
        effectiveAtUtc = effectiveAtUtc < parentBoundary.EffectiveAtUtc ? parentBoundary.EffectiveAtUtc : effectiveAtUtc;
        normalized = new AuthorityDelegationBoundary(effectiveAtUtc, boundary.ExpiresAtUtc, boundary.CompletionConstraint);
        if (!AuthorityDelegationContractValidator.Validate(normalized).IsValid || !BoundaryFitsParent(normalized, parentBoundary))
        {
            normalized = null;
            return false;
        }

        return true;
    }

    private static bool BoundaryFitsParent(AuthorityDelegationBoundary boundary, AuthorityGrantBoundary parentBoundary)
    {
        if (boundary.EffectiveAtUtc < parentBoundary.EffectiveAtUtc
            || parentBoundary.ExpiresAtUtc is { } parentExpiry
            && (boundary.EffectiveAtUtc >= parentExpiry
                || boundary.ExpiresAtUtc is { } localExpiry && localExpiry > parentExpiry))
        {
            return false;
        }

        return true;
    }

    private static bool IsWithin(AuthorityCeiling? candidate, AuthorityCeiling? maximum)
        => AuthorityCeilingSubset.IsEqual(candidate, maximum) || AuthorityCeilingSubset.IsStrictSubset(candidate, maximum);

    private static bool SameCanonicalGrantRevision(AuthorityGrant left, AuthorityGrant right)
        => Equals(left.GrantId, right.GrantId)
            && Equals(left.Revision, right.Revision)
            && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static bool SameProof(AuthorityDelegationSubsetProof left, AuthorityDelegationSubsetProof right)
        => string.Equals(left.ParentEvidenceHash, right.ParentEvidenceHash, StringComparison.Ordinal)
            && string.Equals(left.ParentAuthorityScopeHash, right.ParentAuthorityScopeHash, StringComparison.Ordinal)
            && string.Equals(left.DelegatedAuthorityScopeHash, right.DelegatedAuthorityScopeHash, StringComparison.Ordinal)
            && string.Equals(left.TargetMaximumEvidenceHash, right.TargetMaximumEvidenceHash, StringComparison.Ordinal)
            && left.NarrowingDimensions.SequenceEqual(right.NarrowingDimensions)
            && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);

    private static AuthorityDelegationServiceStatus? MapGrant(AuthorityGrantResolutionStatus status) => status switch
    {
        AuthorityGrantResolutionStatus.Active => null,
        AuthorityGrantResolutionStatus.NotEffective => AuthorityDelegationServiceStatus.ParentNotEffective,
        AuthorityGrantResolutionStatus.Suspended => AuthorityDelegationServiceStatus.ParentSuspended,
        AuthorityGrantResolutionStatus.Revoked => AuthorityDelegationServiceStatus.ParentRevoked,
        AuthorityGrantResolutionStatus.Expired => AuthorityDelegationServiceStatus.ParentExpired,
        AuthorityGrantResolutionStatus.Stale or AuthorityGrantResolutionStatus.ProfileUnavailable or AuthorityGrantResolutionStatus.RoleUnavailable or AuthorityGrantResolutionStatus.LoopUnavailable or AuthorityGrantResolutionStatus.CeilingExceeded or AuthorityGrantResolutionStatus.NotFound or AuthorityGrantResolutionStatus.Invalid => AuthorityDelegationServiceStatus.ParentReplaced,
        AuthorityGrantResolutionStatus.Unavailable => AuthorityDelegationServiceStatus.Unavailable,
        _ => AuthorityDelegationServiceStatus.Ambiguous,
    };

    private static AuthorityDelegationServiceStatus? MapOrigin(AuthorityDelegationOriginResolutionStatus status) => status switch
    {
        AuthorityDelegationOriginResolutionStatus.Current => null,
        AuthorityDelegationOriginResolutionStatus.Unavailable => AuthorityDelegationServiceStatus.OriginUnavailable,
        AuthorityDelegationOriginResolutionStatus.Ambiguous or AuthorityDelegationOriginResolutionStatus.Unknown => AuthorityDelegationServiceStatus.Ambiguous,
        _ => AuthorityDelegationServiceStatus.OriginDrifted,
    };

    private static AuthorityDelegationServiceStatus? MapTarget(AuthorityDelegationTargetResolutionStatus status) => status switch
    {
        AuthorityDelegationTargetResolutionStatus.Active => null,
        AuthorityDelegationTargetResolutionStatus.Unavailable => AuthorityDelegationServiceStatus.TargetUnavailable,
        AuthorityDelegationTargetResolutionStatus.Ambiguous or AuthorityDelegationTargetResolutionStatus.Unknown => AuthorityDelegationServiceStatus.Ambiguous,
        _ => AuthorityDelegationServiceStatus.TargetMismatch,
    };

    private static AuthorityDelegationServiceStatus? MapCompletion(
        AuthorityDelegationCompletionResolution? resolution,
        AuthorityDelegationCompletionConstraintKind constraint)
        => resolution?.Status switch
        {
            AuthorityDelegationCompletionStatus.Active => null,
            AuthorityDelegationCompletionStatus.ParentCompleted => AuthorityDelegationServiceStatus.ParentCompleted,
            AuthorityDelegationCompletionStatus.TargetCompleted when constraint == AuthorityDelegationCompletionConstraintKind.TargetCompletion => AuthorityDelegationServiceStatus.EnvelopeCompleted,
            AuthorityDelegationCompletionStatus.TargetCompleted when constraint == AuthorityDelegationCompletionConstraintKind.None => null,
            AuthorityDelegationCompletionStatus.Unavailable => AuthorityDelegationServiceStatus.Unavailable,
            _ => AuthorityDelegationServiceStatus.Ambiguous,
        };

    private DateTimeOffset UtcNow()
    {
        try
        {
            var value = _timeProvider.GetUtcNow();
            return value == default || value.Offset != TimeSpan.Zero ? default : value;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private bool TryAdvanceTrustedTime(ref DateTimeOffset observedAtUtc)
    {
        var current = UtcNow();
        if (current == default || current < observedAtUtc)
        {
            return false;
        }

        observedAtUtc = current;
        return true;
    }

    private static bool IsToken(string? value, int maximum)
        => value is not null
            && value.Length > 0
            && value.Length <= maximum
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool IsCanonicalHash(string? value)
        => value?.Length == AuthorityDelegationContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AuthorityDelegationServiceResult Result(
        AuthorityDelegationServiceStatus status,
        AuthorityDelegationEnvelope? envelope = null)
    {
        var successful = status is AuthorityDelegationServiceStatus.Created or AuthorityDelegationServiceStatus.Replayed or AuthorityDelegationServiceStatus.Valid;
        return new AuthorityDelegationServiceResult(status, successful ? envelope : null, Reason(status));
    }

    private static string Reason(AuthorityDelegationServiceStatus status) => status switch
    {
        AuthorityDelegationServiceStatus.Created => "created",
        AuthorityDelegationServiceStatus.Replayed => "replayed",
        AuthorityDelegationServiceStatus.Valid => "valid",
        AuthorityDelegationServiceStatus.InvalidContract => "invalid-contract",
        AuthorityDelegationServiceStatus.OutsideParentAuthority => "outside-parent-authority",
        AuthorityDelegationServiceStatus.OriginMismatch => "origin-mismatch",
        AuthorityDelegationServiceStatus.OriginDrifted => "origin-drifted",
        AuthorityDelegationServiceStatus.OriginUnavailable => "origin-unavailable",
        AuthorityDelegationServiceStatus.TargetMismatch => "target-mismatch",
        AuthorityDelegationServiceStatus.TargetUnavailable => "target-unavailable",
        AuthorityDelegationServiceStatus.ParentNotEffective => "parent-not-effective",
        AuthorityDelegationServiceStatus.ParentSuspended => "parent-suspended",
        AuthorityDelegationServiceStatus.ParentRevoked => "parent-revoked",
        AuthorityDelegationServiceStatus.ParentExpired => "parent-expired",
        AuthorityDelegationServiceStatus.ParentReplaced => "parent-replaced",
        AuthorityDelegationServiceStatus.ParentCompleted => "parent-completed",
        AuthorityDelegationServiceStatus.EnvelopeNotEffective => "envelope-not-effective",
        AuthorityDelegationServiceStatus.EnvelopeExpired => "envelope-expired",
        AuthorityDelegationServiceStatus.EnvelopeCompleted => "envelope-completed",
        AuthorityDelegationServiceStatus.Unavailable => "unavailable",
        AuthorityDelegationServiceStatus.EnvelopeIdConflict => "envelope-id-conflict",
        _ => "ambiguous",
    };
}
