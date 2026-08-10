using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Executes authenticated immutable authority-grant lifecycle operations under one shared authority fence.</summary>
public sealed class AuthorityGrantLifecycleService : IAuthorityGrantLifecycleService
{
    private const int MaximumCommitAttempts = 2;
    private readonly IAuthorityGrantStore _store;
    private readonly IAuthorityGrantActorAuthorizer _authorizer;
    private readonly AuthorityGrantDependencyEvaluator _dependencies;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a complete authority-grant lifecycle service over server-owned ports.</summary>
    public AuthorityGrantLifecycleService(
        IAuthorityGrantStore store,
        IAuthorityGrantActorAuthorizer authorizer,
        IAuthorityGrantProfileSource profileSource,
        IAuthorityGrantRoleSource roleSource,
        IGovernedLoopPublishedRevisionSource publishedLoopSource,
        IGovernedLoopGrantBindingSource loopBindingSource,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _dependencies = new AuthorityGrantDependencyEvaluator(profileSource, roleSource, publishedLoopSource, loopBindingSource);
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantMutationResult> MutateAsync(AuthorityGrantMutationRequest? request, CancellationToken cancellationToken = default)
    {
        AuthorityGrantMutationResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completed = await MutateUnderFenceAsync(request, transactionToken).ConfigureAwait(false);
                    return completed;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completed is null)
        {
            throw;
        }
        catch (Exception)
        {
            if (HasDurableProof(completed))
            {
                return completed!;
            }

            return Result(
                completed is null ? AuthorityGrantMutationStatus.Unavailable : AuthorityGrantMutationStatus.Ambiguous,
                SafeOperationId(request),
                completed?.RequestHash ?? string.Empty);
        }
    }

    private async Task<AuthorityGrantMutationResult> MutateUnderFenceAsync(AuthorityGrantMutationRequest? request, CancellationToken cancellationToken)
    {
        var errors = AuthorityGrantMutationRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return Result(AuthorityGrantMutationStatus.Invalid, SafeOperationId(request), string.Empty, validationErrors: errors);
        }

        var exact = request!;
        var initialRead = await ObserveAsync(exact, cancellationToken).ConfigureAwait(false);
        var existing = ResolveExisting(initialRead, exact);
        if (existing is not null)
        {
            return existing;
        }

        var readFailure = MapReadFailure(initialRead, exact);
        if (readFailure is not null)
        {
            return readFailure;
        }

        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            var read = attempt == 0 ? initialRead : await ObserveAsync(exact, cancellationToken).ConfigureAwait(false);
            existing = ResolveExisting(read, exact);
            if (existing is not null)
            {
                return existing;
            }

            readFailure = MapReadFailure(read, exact);
            if (readFailure is not null)
            {
                return readFailure;
            }

            if (read.Snapshot?.Operations.Count >= AuthorityGrantContractLimits.MaxOperationsPerStore
                || read.Snapshot?.Revisions.Count >= AuthorityGrantContractLimits.MaxRevisionsPerGrant)
            {
                return Result(AuthorityGrantMutationStatus.LimitExceeded, exact.OperationId, exact.RequestHash);
            }

            var recordedAtUtc = UtcNow();
            if (recordedAtUtc == default || read.Snapshot?.CurrentGrant is { } current && recordedAtUtc < current.RecordedAtUtc)
            {
                return Result(AuthorityGrantMutationStatus.Unavailable, exact.OperationId, exact.RequestHash);
            }

            var authorization = await AuthorizeAsync(exact, recordedAtUtc, cancellationToken).ConfigureAwait(false);
            if (authorization.Status != AuthorityGrantActorAuthorizationStatus.Authorized)
            {
                return Result(
                    authorization.Status == AuthorityGrantActorAuthorizationStatus.Denied ? AuthorityGrantMutationStatus.Denied : AuthorityGrantMutationStatus.Unavailable,
                    exact.OperationId,
                    exact.RequestHash);
            }

            var plan = Plan(exact, read.Snapshot, recordedAtUtc);
            var dependencyEvidenceHash = string.Empty;
            if (plan.Successor is not null && exact.Kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace)
            {
                var dependency = await _dependencies.EvaluateAsync(plan.Successor.Binding, plan.Successor.RequestedCeiling, recordedAtUtc, cancellationToken).ConfigureAwait(false);
                if (dependency.FailureCode == AuthorityGrantOperationFailureCode.CeilingExceeded)
                {
                    plan = Receipt(AuthorityGrantMutationStatus.CeilingExceeded, AuthorityGrantOperationOutcome.Conflict, dependency.FailureCode);
                    dependencyEvidenceHash = dependency.EvidenceHash;
                }
                else if (dependency.FailureCode != AuthorityGrantOperationFailureCode.None)
                {
                    return Result(AuthorityGrantMutationStatus.DependencyUnavailable, exact.OperationId, exact.RequestHash, read.Snapshot?.CurrentGrant);
                }
                else
                {
                    dependencyEvidenceHash = dependency.EvidenceHash;
                }
            }

            var mutation = BuildMutation(exact, read.StoreGeneration, authorization.EvidenceHash, dependencyEvidenceHash, plan, recordedAtUtc);
            if (mutation is null)
            {
                return Result(AuthorityGrantMutationStatus.Ambiguous, exact.OperationId, exact.RequestHash, read.Snapshot?.CurrentGrant);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AuthorityGrantStoreCommitResult? commit;
            try
            {
                commit = await _store.CommitAsync(mutation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await RecoverAfterIntentAsync(exact).ConfigureAwait(false);
            }

            var mapped = await MapCommitAsync(commit, mutation, exact).ConfigureAwait(false);
            if (mapped.Retry)
            {
                continue;
            }

            return mapped.Result!;
        }

        return Result(AuthorityGrantMutationStatus.Conflict, exact.OperationId, exact.RequestHash);
    }

    private async Task<(AuthorityGrantActorAuthorizationStatus Status, string EvidenceHash)> AuthorizeAsync(
        AuthorityGrantMutationRequest request,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        AuthorityGrantActorAuthorization decision;
        try
        {
            decision = await _authorizer.AuthorizeAsync(new AuthorityGrantActorAuthorizationRequest(request, request.RequestHash, evaluatedAtUtc), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (AuthorityGrantActorAuthorizationStatus.Unavailable, string.Empty);
        }

        if (decision is null
            || !Enum.IsDefined(decision.Status)
            || decision.Status == AuthorityGrantActorAuthorizationStatus.Unknown
            || !string.Equals(decision.OperationId, request.OperationId, StringComparison.Ordinal)
            || !string.Equals(decision.RequestHash, request.RequestHash, StringComparison.Ordinal)
            || !Equals(decision.ActorId, request.ActorId)
            || decision.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || decision.EvaluatedAtUtc != evaluatedAtUtc
            || !AuthorityGrantEvidenceHash.IsSha256(decision.AuthorityEvidenceHash))
        {
            return (AuthorityGrantActorAuthorizationStatus.Unavailable, string.Empty);
        }

        return (decision.Status, decision.AuthorityEvidenceHash);
    }

    private async Task<AuthorityGrantStoreReadResult> ObserveAsync(AuthorityGrantMutationRequest request, CancellationToken cancellationToken)
    {
        AuthorityGrantStoreReadResult read;
        try
        {
            read = await _store.ReadForMutationAsync(request.GrantId, request.OperationId, request.RequestHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UnavailableRead();
        }

        if (read is null || read.StoreGeneration is < 0 or long.MaxValue || !Enum.IsDefined(read.Status) || read.Status == AuthorityGrantStoreReadStatus.Unknown)
        {
            return AmbiguousRead();
        }

        AuthorityGrantStoreSnapshot? snapshot = null;
        if (read.Snapshot is not null && !AuthorityGrantStoreSnapshotGuard.TryCapture(read.Snapshot, request.GrantId, read.StoreGeneration, out snapshot))
        {
            return AmbiguousRead();
        }

        if (!IsValidExisting(read.ExistingOperation, request.OperationId))
        {
            return AmbiguousRead();
        }

        if (snapshot is not null && read.ExistingOperation is { } existing)
        {
            if (existing.GrantId.Equals(request.GrantId))
            {
                if (!Equals(snapshot.Operations[^1], existing.Evidence))
                {
                    return AmbiguousRead();
                }
            }
            else if (AuthorityGrantStoreSnapshotGuard.Contains(snapshot, existing.Evidence) || read.StoreGeneration <= snapshot.Operations.Count)
            {
                return AmbiguousRead();
            }
        }

        return read.Status switch
        {
            AuthorityGrantStoreReadStatus.Ready when snapshot is not null => read with { Snapshot = snapshot },
            AuthorityGrantStoreReadStatus.NotFound when snapshot is null => read,
            AuthorityGrantStoreReadStatus.OperationConflict when read.ExistingOperation is null => read with { Snapshot = snapshot },
            AuthorityGrantStoreReadStatus.Unavailable when read.StoreGeneration == 0 && snapshot is null && read.ExistingOperation is null => UnavailableRead(),
            AuthorityGrantStoreReadStatus.Ambiguous => AmbiguousRead(),
            _ => AmbiguousRead(),
        };
    }

    private static AuthorityGrantMutationResult? ResolveExisting(AuthorityGrantStoreReadResult read, AuthorityGrantMutationRequest request)
    {
        if (read.ExistingOperation is not { } stored)
        {
            return null;
        }

        var evidence = stored.Evidence;
        if (!string.Equals(evidence.RequestHash, request.RequestHash, StringComparison.Ordinal) || !stored.GrantId.Equals(request.GrantId))
        {
            return Result(AuthorityGrantMutationStatus.Conflict, request.OperationId, request.RequestHash);
        }

        if (evidence.Kind != request.Kind
            || !evidence.GrantId.Equals(request.GrantId)
            || evidence.ExpectedRevision != request.ExpectedRevision
            || !Equals(evidence.ActorId, request.ActorId)
            || !Equals(evidence.Reason, request.Reason))
        {
            return Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash);
        }

        AuthorityGrant? resultingGrant = null;
        if (evidence.ResultingGrant is { } reference)
        {
            if (read.Snapshot is null || (resultingGrant = AuthorityGrantStoreSnapshotGuard.Find(read.Snapshot, reference)) is null)
            {
                return Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash);
            }

            if (!IsExactCommittedReplay(request, evidence, read.Snapshot, resultingGrant))
            {
                return Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash);
            }
        }
        else if (!IsExactReceiptReplay(request, evidence, read.Snapshot))
        {
            return Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash);
        }

        return Result(AuthorityGrantMutationStatus.Replayed, request.OperationId, request.RequestHash, resultingGrant ?? read.Snapshot?.CurrentGrant, evidence);
    }

    private static AuthorityGrantMutationResult? MapReadFailure(AuthorityGrantStoreReadResult read, AuthorityGrantMutationRequest request)
        => read.Status switch
        {
            AuthorityGrantStoreReadStatus.OperationConflict => Result(AuthorityGrantMutationStatus.Conflict, request.OperationId, request.RequestHash),
            AuthorityGrantStoreReadStatus.Unavailable => Result(AuthorityGrantMutationStatus.Unavailable, request.OperationId, request.RequestHash),
            AuthorityGrantStoreReadStatus.Ambiguous => Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash),
            _ => null,
        };

    private static bool IsExactCommittedReplay(
        AuthorityGrantMutationRequest request,
        AuthorityGrantOperationEvidence evidence,
        AuthorityGrantStoreSnapshot snapshot,
        AuthorityGrant grant)
    {
        if (evidence.Outcome != AuthorityGrantOperationOutcome.Committed
            || !Equals(evidence.ActorId, grant.ChangedByActorId)
            || !Equals(evidence.Reason, grant.Reason)
            || evidence.RecordedAtUtc != grant.RecordedAtUtc)
        {
            return false;
        }

        AuthorityGrant? predecessor = null;
        if (request.Kind != AuthorityGrantOperationKind.Create)
        {
            predecessor = snapshot.Revisions.SingleOrDefault(candidate => candidate.Revision.Value == request.ExpectedRevision);
            if (predecessor is null || predecessor.Status != request.ExpectedStatus)
            {
                return false;
            }
        }

        var statusMatches = request.Kind switch
        {
            AuthorityGrantOperationKind.Create => grant.Status == AuthorityGrantLifecycleStatus.Active,
            AuthorityGrantOperationKind.Narrow => grant.Status == predecessor!.Status,
            AuthorityGrantOperationKind.Suspend => predecessor!.Status == AuthorityGrantLifecycleStatus.Active && grant.Status == AuthorityGrantLifecycleStatus.Suspended,
            AuthorityGrantOperationKind.Replace => predecessor!.Status is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended && grant.Status == AuthorityGrantLifecycleStatus.Active,
            AuthorityGrantOperationKind.Revoke => predecessor!.Status is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended && grant.Status == AuthorityGrantLifecycleStatus.Revoked,
            AuthorityGrantOperationKind.Expire => predecessor!.Status is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended && grant.Status == AuthorityGrantLifecycleStatus.Expired,
            _ => false,
        };
        if (!statusMatches)
        {
            return false;
        }

        return request.Kind is not (AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace)
            || Equals(grant.Binding, request.CandidateBinding)
            && AuthorityCeilingSubset.IsEqual(grant.RequestedCeiling, request.CandidateCeiling)
            && Equals(grant.Boundary, request.CandidateBoundary);
    }

    private static bool IsExactReceiptReplay(
        AuthorityGrantMutationRequest request,
        AuthorityGrantOperationEvidence evidence,
        AuthorityGrantStoreSnapshot? snapshot)
    {
        var exactExpectedSnapshot = snapshot is not null
            && snapshot.CurrentGrant.Revision.Value == request.ExpectedRevision
            && snapshot.CurrentGrant.Status == request.ExpectedStatus;
        var historicalShapeMatches = (evidence.Outcome, evidence.FailureCode, evidence.Kind) switch
        {
            (AuthorityGrantOperationOutcome.NotFound, AuthorityGrantOperationFailureCode.LifecycleConflict, not AuthorityGrantOperationKind.Create)
                => snapshot is null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict, _)
                => snapshot is not null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict, AuthorityGrantOperationKind.Create)
                => snapshot is null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict, AuthorityGrantOperationKind.Replace or AuthorityGrantOperationKind.Expire)
                => exactExpectedSnapshot,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.CeilingExceeded, AuthorityGrantOperationKind.Create)
                => snapshot is null,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.CeilingExceeded, AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace)
                => exactExpectedSnapshot,
            (AuthorityGrantOperationOutcome.LimitExceeded, AuthorityGrantOperationFailureCode.LimitExceeded, _)
                => true,
            _ => false,
        };
        if (!historicalShapeMatches)
        {
            return false;
        }

        if (evidence.Outcome == AuthorityGrantOperationOutcome.LimitExceeded)
        {
            return true;
        }

        var plan = Plan(request, snapshot, evidence.RecordedAtUtc);
        if (evidence.FailureCode == AuthorityGrantOperationFailureCode.CeilingExceeded)
        {
            return plan.Successor is not null;
        }

        return plan.Successor is null
            && plan.Outcome == evidence.Outcome
            && plan.FailureCode == evidence.FailureCode;
    }

    private static (AuthorityGrantMutationStatus ApplicationStatus, AuthorityGrantOperationOutcome Outcome, AuthorityGrantOperationFailureCode FailureCode, AuthorityGrant? Successor) Plan(
        AuthorityGrantMutationRequest request,
        AuthorityGrantStoreSnapshot? snapshot,
        DateTimeOffset recordedAtUtc)
    {
        var current = snapshot?.CurrentGrant;
        if (request.Kind == AuthorityGrantOperationKind.Create)
        {
            if (current is not null)
            {
                return Receipt(AuthorityGrantMutationStatus.Conflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict);
            }

            if (request.CandidateBoundary!.ExpiresAtUtc is { } expiry && expiry <= recordedAtUtc)
            {
                return Receipt(AuthorityGrantMutationStatus.BoundaryConflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict);
            }

            return Successor(request, null, AuthorityGrantLifecycleStatus.Active, recordedAtUtc);
        }

        if (current is null)
        {
            return Receipt(AuthorityGrantMutationStatus.NotFound, AuthorityGrantOperationOutcome.NotFound, AuthorityGrantOperationFailureCode.LifecycleConflict);
        }

        if (request.ExpectedRevision != current.Revision.Value || request.ExpectedStatus != current.Status)
        {
            return Receipt(AuthorityGrantMutationStatus.Conflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict);
        }

        if (current.Status is AuthorityGrantLifecycleStatus.Revoked or AuthorityGrantLifecycleStatus.Expired)
        {
            return Receipt(AuthorityGrantMutationStatus.Conflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict);
        }

        return request.Kind switch
        {
            AuthorityGrantOperationKind.Narrow => Successor(request, current, current.Status, recordedAtUtc),
            AuthorityGrantOperationKind.Suspend => Successor(request, current, AuthorityGrantLifecycleStatus.Suspended, recordedAtUtc),
            AuthorityGrantOperationKind.Replace when request.CandidateBoundary!.ExpiresAtUtc is { } expiry && expiry <= recordedAtUtc
                => Receipt(AuthorityGrantMutationStatus.BoundaryConflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict),
            AuthorityGrantOperationKind.Replace => Successor(request, current, AuthorityGrantLifecycleStatus.Active, recordedAtUtc),
            AuthorityGrantOperationKind.Revoke => Successor(request, current, AuthorityGrantLifecycleStatus.Revoked, recordedAtUtc),
            AuthorityGrantOperationKind.Expire when current.Boundary.ExpiresAtUtc is null || current.Boundary.ExpiresAtUtc > recordedAtUtc
                => Receipt(AuthorityGrantMutationStatus.BoundaryConflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict),
            AuthorityGrantOperationKind.Expire => Successor(request, current, AuthorityGrantLifecycleStatus.Expired, recordedAtUtc),
            _ => Receipt(AuthorityGrantMutationStatus.Conflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict),
        };
    }

    private static (AuthorityGrantMutationStatus ApplicationStatus, AuthorityGrantOperationOutcome Outcome, AuthorityGrantOperationFailureCode FailureCode, AuthorityGrant? Successor) Successor(
        AuthorityGrantMutationRequest request,
        AuthorityGrant? current,
        AuthorityGrantLifecycleStatus status,
        DateTimeOffset recordedAtUtc)
    {
        var revisionValue = (current?.Revision.Value ?? 0) + 1;
        if (!AuthorityGrantRevision.TryParse(revisionValue.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _))
        {
            return Receipt(AuthorityGrantMutationStatus.LimitExceeded, AuthorityGrantOperationOutcome.LimitExceeded, AuthorityGrantOperationFailureCode.LimitExceeded);
        }

        var usesCandidate = request.Kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace;
        var grant = new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            request.GrantId,
            revision!,
            current?.Revision,
            current?.ContentHash,
            status,
            usesCandidate ? request.CandidateBinding! : current!.Binding,
            usesCandidate ? request.CandidateCeiling! : current!.RequestedCeiling,
            usesCandidate ? request.CandidateBoundary! : current!.Boundary,
            request.ActorId,
            request.Reason,
            recordedAtUtc,
            string.Empty);
        try
        {
            grant = AuthorityGrantHash.Apply(grant);
        }
        catch (ArgumentException)
        {
            return Receipt(AuthorityGrantMutationStatus.Conflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict);
        }

        var valid = current is null
            ? AuthorityGrantContractValidator.Validate(grant).IsValid
            : AuthorityGrantContractValidator.ValidateTransition(current, grant, request.Kind).IsValid;
        return valid
            ? (AuthorityGrantMutationStatus.Committed, AuthorityGrantOperationOutcome.Committed, AuthorityGrantOperationFailureCode.None, grant)
            : Receipt(AuthorityGrantMutationStatus.Conflict, AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict);
    }

    private static (AuthorityGrantMutationStatus ApplicationStatus, AuthorityGrantOperationOutcome Outcome, AuthorityGrantOperationFailureCode FailureCode, AuthorityGrant? Successor) Receipt(
        AuthorityGrantMutationStatus status,
        AuthorityGrantOperationOutcome outcome,
        AuthorityGrantOperationFailureCode failureCode)
        => (status, outcome, failureCode, null);

    private static AuthorityGrantStoreMutation? BuildMutation(
        AuthorityGrantMutationRequest request,
        long storeGeneration,
        string authorityEvidenceHash,
        string dependencyEvidenceHash,
        (AuthorityGrantMutationStatus ApplicationStatus, AuthorityGrantOperationOutcome Outcome, AuthorityGrantOperationFailureCode FailureCode, AuthorityGrant? Successor) plan,
        DateTimeOffset recordedAtUtc)
    {
        var resultingReference = plan.Successor is null
            ? null
            : new AuthorityGrantReference(plan.Successor.GrantId, plan.Successor.Revision, plan.Successor.ContentHash);
        var evidence = new AuthorityGrantOperationEvidence(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            request.OperationId,
            request.RequestHash,
            request.Kind,
            plan.Outcome,
            plan.FailureCode,
            request.GrantId,
            request.ExpectedRevision,
            resultingReference,
            request.ActorId,
            request.Reason,
            authorityEvidenceHash,
            string.IsNullOrEmpty(dependencyEvidenceHash) ? null : dependencyEvidenceHash,
            recordedAtUtc);
        return AuthorityGrantContractValidator.Validate(evidence).IsValid
            ? new AuthorityGrantStoreMutation(storeGeneration, plan.Successor, evidence)
            : null;
    }

    private async Task<(bool Retry, AuthorityGrantMutationResult? Result)> MapCommitAsync(
        AuthorityGrantStoreCommitResult? commit,
        AuthorityGrantStoreMutation mutation,
        AuthorityGrantMutationRequest request)
    {
        if (commit is null || commit.StoreGeneration is < 0 or long.MaxValue || !Enum.IsDefined(commit.Status) || commit.Status == AuthorityGrantStoreCommitStatus.Unknown)
        {
            return (false, await RecoverAfterIntentAsync(request).ConfigureAwait(false));
        }

        if (commit.Status is AuthorityGrantStoreCommitStatus.Committed or AuthorityGrantStoreCommitStatus.Replayed)
        {
            if (!TryExactProof(commit, mutation, request, out var grant))
            {
                return (false, await RecoverAfterIntentAsync(request).ConfigureAwait(false));
            }

            var status = commit.Status == AuthorityGrantStoreCommitStatus.Replayed
                ? AuthorityGrantMutationStatus.Replayed
                : mutation.Operation.Outcome == AuthorityGrantOperationOutcome.Committed
                    ? AuthorityGrantMutationStatus.Committed
                    : MapReceipt(mutation.Operation);
            return (false, Result(status, request.OperationId, request.RequestHash, grant ?? commit.Snapshot?.CurrentGrant, mutation.Operation));
        }

        if (commit.Status == AuthorityGrantStoreCommitStatus.StoreConflict
            && commit.StoreGeneration > mutation.ExpectedStoreGeneration
            && commit.StoredOperation is null)
        {
            return (true, null);
        }

        if (commit.Status == AuthorityGrantStoreCommitStatus.OperationConflict
            && commit.StoredOperation is { } conflicting
            && string.Equals(conflicting.Evidence.OperationId, request.OperationId, StringComparison.Ordinal)
            && !string.Equals(conflicting.Evidence.RequestHash, request.RequestHash, StringComparison.Ordinal))
        {
            return (false, Result(AuthorityGrantMutationStatus.Conflict, request.OperationId, request.RequestHash));
        }

        if (commit.Status == AuthorityGrantStoreCommitStatus.OperationConflict
            && commit.StoredOperation is null
            && commit.StoreGeneration >= mutation.ExpectedStoreGeneration)
        {
            return (false, Result(AuthorityGrantMutationStatus.Conflict, request.OperationId, request.RequestHash));
        }

        if (commit.Status == AuthorityGrantStoreCommitStatus.LimitExceeded && commit.StoredOperation is null)
        {
            return (false, Result(AuthorityGrantMutationStatus.LimitExceeded, request.OperationId, request.RequestHash));
        }

        if (commit.Status == AuthorityGrantStoreCommitStatus.Unavailable && commit.StoredOperation is null)
        {
            return (false, Result(AuthorityGrantMutationStatus.Unavailable, request.OperationId, request.RequestHash));
        }

        return (false, await RecoverAfterIntentAsync(request).ConfigureAwait(false));
    }

    private async Task<AuthorityGrantMutationResult> RecoverAfterIntentAsync(AuthorityGrantMutationRequest request)
    {
        AuthorityGrantStoreReadResult recovered;
        try
        {
            recovered = await ObserveAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash);
        }

        return ResolveExisting(recovered, request)
            ?? MapReadFailure(recovered, request)
            ?? Result(AuthorityGrantMutationStatus.Ambiguous, request.OperationId, request.RequestHash, recovered.Snapshot?.CurrentGrant);
    }

    private static bool TryExactProof(
        AuthorityGrantStoreCommitResult commit,
        AuthorityGrantStoreMutation mutation,
        AuthorityGrantMutationRequest request,
        out AuthorityGrant? grant)
    {
        grant = null;
        if (mutation.ExpectedStoreGeneration == long.MaxValue
            || commit.StoredOperation is null
            || !Equals(commit.StoredOperation.Evidence, mutation.Operation)
            || !commit.StoredOperation.GrantId.Equals(mutation.Operation.GrantId))
        {
            return false;
        }

        var firstPossibleCommittedGeneration = mutation.ExpectedStoreGeneration + 1;
        if (commit.Status == AuthorityGrantStoreCommitStatus.Committed
                ? commit.StoreGeneration != firstPossibleCommittedGeneration
                : commit.StoreGeneration < firstPossibleCommittedGeneration)
        {
            return false;
        }

        if (mutation.GrantToAppend is null)
        {
            if (commit.Snapshot is null)
            {
                return IsExactReceiptReplay(request, mutation.Operation, null);
            }

            return AuthorityGrantStoreSnapshotGuard.TryCapture(commit.Snapshot, mutation.Operation.GrantId, commit.StoreGeneration, out var receiptSnapshot)
                && Equals(receiptSnapshot!.Operations[^1], mutation.Operation)
                && IsExactReceiptReplay(request, mutation.Operation, receiptSnapshot);
        }

        if (commit.Snapshot is null
            || !AuthorityGrantStoreSnapshotGuard.TryCapture(commit.Snapshot, mutation.Operation.GrantId, commit.StoreGeneration, out var snapshot)
            || !Equals(snapshot!.Operations[^1], mutation.Operation))
        {
            return false;
        }

        grant = AuthorityGrantStoreSnapshotGuard.Find(snapshot!, mutation.Operation.ResultingGrant!);
        return grant is not null
            && Equals(grant, mutation.GrantToAppend)
            && Equals(snapshot.CurrentGrant, grant);
    }

    private static AuthorityGrantMutationStatus MapReceipt(AuthorityGrantOperationEvidence evidence) => evidence.FailureCode switch
    {
        AuthorityGrantOperationFailureCode.LifecycleConflict when evidence.Outcome == AuthorityGrantOperationOutcome.NotFound => AuthorityGrantMutationStatus.NotFound,
        AuthorityGrantOperationFailureCode.LifecycleConflict => AuthorityGrantMutationStatus.Conflict,
        AuthorityGrantOperationFailureCode.BoundaryConflict => AuthorityGrantMutationStatus.BoundaryConflict,
        AuthorityGrantOperationFailureCode.CeilingExceeded => AuthorityGrantMutationStatus.CeilingExceeded,
        AuthorityGrantOperationFailureCode.LimitExceeded => AuthorityGrantMutationStatus.LimitExceeded,
        _ => AuthorityGrantMutationStatus.Ambiguous,
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

    private static AuthorityGrantStoreReadResult UnavailableRead()
        => new(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);

    private static AuthorityGrantStoreReadResult AmbiguousRead()
        => new(AuthorityGrantStoreReadStatus.Ambiguous, 0, null, null);

    private static bool IsValidExisting(AuthorityGrantStoredOperation? existing, string operationId)
        => existing is null
            || existing.GrantId is not null
            && existing.Evidence is not null
            && existing.GrantId.Equals(existing.Evidence.GrantId)
            && string.Equals(existing.Evidence.OperationId, operationId, StringComparison.Ordinal)
            && AuthorityGrantContractValidator.Validate(existing.Evidence).IsValid;

    private static bool HasDurableProof(AuthorityGrantMutationResult? result)
        => result is { Evidence: { } evidence }
            && AuthorityGrantContractValidator.Validate(evidence).IsValid
            && (evidence.ResultingGrant is null
                || result.Grant is { } grant
                && AuthorityGrantContractValidator.Validate(grant).IsValid
                && AuthorityGrantStoreSnapshotGuard.Matches(evidence.ResultingGrant, grant));

    private static string SafeOperationId(AuthorityGrantMutationRequest? request)
        => IsOperationToken(request?.OperationId) ? request!.OperationId : string.Empty;

    private static bool IsOperationToken(string? value)
        => value is { Length: > 0 and <= AuthorityGrantContractLimits.MaxOperationIdCharacters }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static AuthorityGrantMutationResult Result(
        AuthorityGrantMutationStatus status,
        string operationId,
        string requestHash,
        AuthorityGrant? grant = null,
        AuthorityGrantOperationEvidence? evidence = null,
        IReadOnlyList<AuthorityGrantMutationValidationError>? validationErrors = null)
        => new(status, operationId, requestHash, grant, evidence, Array.AsReadOnly((validationErrors ?? []).ToArray()));
}
