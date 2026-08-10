using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

/// <summary>Executes authenticated Human Input request lifecycle operations with exact replay, active grant binding, and atomic durable evidence.</summary>
public sealed class HumanInputRequestLifecycleService : IHumanInputRequestLifecycleService
{
    private const int MaximumCommitAttempts = 2;
    private readonly IHumanInputRequestLifecycleStore _store;
    private readonly IHumanInputRequestLifecycleActorAuthorizer _authorizer;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly string _workspaceId;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a lifecycle service over server-owned workspace, actor, grant, time, and atomic persistence boundaries.</summary>
    /// <param name="store">The atomic immutable request lifecycle store.</param>
    /// <param name="authorizer">The current server-owned actor authorizer.</param>
    /// <param name="grantResolver">The exact active authority-grant resolver.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <param name="workspaceId">The server-configured exact workspace identity.</param>
    /// <param name="timeProvider">The trusted cleanup-operation clock, or the system clock when omitted.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required port or authority fence is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the configured workspace identity is not canonical.</exception>
    public HumanInputRequestLifecycleService(
        IHumanInputRequestLifecycleStore store,
        IHumanInputRequestLifecycleActorAuthorizer authorizer,
        IAuthorityGrantResolver grantResolver,
        ICapabilityAuthorityTransaction authorityTransaction,
        string workspaceId,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _workspaceId = HumanInputIdentifier.Require(workspaceId, nameof(workspaceId));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<HumanInputRequestLifecycleMutationResult> MutateAsync(
        HumanInputRequestLifecycleCommand? command,
        CancellationToken cancellationToken = default)
    {
        HumanInputRequestLifecycleMutationResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completed = await MutateUnderFenceAsync(command, transactionToken).ConfigureAwait(false);
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
                completed is null ? HumanInputRequestLifecycleMutationStatus.Unavailable : HumanInputRequestLifecycleMutationStatus.Ambiguous,
                SafeOperationId(command),
                completed?.RequestHash ?? string.Empty);
        }
    }

    private async Task<HumanInputRequestLifecycleMutationResult> MutateUnderFenceAsync(
        HumanInputRequestLifecycleCommand? command,
        CancellationToken cancellationToken)
    {
        var validationErrors = HumanInputRequestLifecycleCommandValidator.Validate(command);
        if (validationErrors.Count > 0 || !TryCaptureCommand(command, out var exactCommand))
        {
            return Result(
                HumanInputRequestLifecycleMutationStatus.Invalid,
                SafeOperationId(command),
                string.Empty,
                validationErrors: validationErrors);
        }

        validationErrors = HumanInputRequestLifecycleCommandValidator.Validate(exactCommand);
        if (validationErrors.Count > 0)
        {
            return Result(
                HumanInputRequestLifecycleMutationStatus.Invalid,
                SafeOperationId(command),
                string.Empty,
                validationErrors: validationErrors);
        }

        var exact = exactCommand!;
        if (exact.ExpectedBinding is { } expectedBinding
                && !string.Equals(expectedBinding.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || exact.CandidateRequest is { } workspaceCandidate
                && !string.Equals(workspaceCandidate.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return Result(
                HumanInputRequestLifecycleMutationStatus.Conflict,
                exact.OperationId,
                exact.RequestHash);
        }

        var initialRead = await ObserveAsync(exact, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            var read = attempt == 0 ? initialRead : await ObserveAsync(exact, cancellationToken).ConfigureAwait(false);
            var existing = ResolveExisting(read, exact);
            if (existing is not null)
            {
                return existing;
            }

            var readFailure = MapReadFailure(read, exact);
            if (readFailure is not null)
            {
                return readFailure;
            }

            if (!WorkspaceMatches(read.PrimarySnapshot) || !WorkspaceMatches(read.RelatedSnapshot))
            {
                return Result(HumanInputRequestLifecycleMutationStatus.Ambiguous, exact.OperationId, exact.RequestHash);
            }

            var dependency = await EstablishDependencyTimeAsync(exact, cancellationToken).ConfigureAwait(false);
            if (dependency.Status != HumanInputRequestLifecycleMutationStatus.Committed)
            {
                return Result(
                    dependency.Status,
                    exact.OperationId,
                    exact.RequestHash);
            }

            var authorization = await AuthorizeAsync(exact, dependency.RecordedAtUtc, cancellationToken).ConfigureAwait(false);
            if (authorization.Status != HumanInputRequestLifecycleActorAuthorizationStatus.Authorized)
            {
                return Result(
                    authorization.Status == HumanInputRequestLifecycleActorAuthorizationStatus.Denied
                        ? HumanInputRequestLifecycleMutationStatus.Denied
                        : HumanInputRequestLifecycleMutationStatus.Unavailable,
                    exact.OperationId,
                    exact.RequestHash);
            }

            if (!ObservedBindingsMatchExpectedScope(exact, read.PrimarySnapshot, read.RelatedSnapshot))
            {
                return Result(HumanInputRequestLifecycleMutationStatus.Conflict, exact.OperationId, exact.RequestHash);
            }

            var candidateIdentitySubstitution = exact.CandidateRequest is { } candidate
                && CandidateIdentityHasDifferentHash(candidate, read.PrimarySnapshot, read.RelatedSnapshot);
            var plan = Plan(exact, read.PrimarySnapshot, read.RelatedSnapshot, dependency.RecordedAtUtc, candidateIdentitySubstitution);
            if (!GrantBindsObservedOperation(dependency.Grant, exact, read.PrimarySnapshot, read.RelatedSnapshot))
            {
                return Result(HumanInputRequestLifecycleMutationStatus.GrantUnavailable, exact.OperationId, exact.RequestHash);
            }

            if (!plan.CanPersist)
            {
                return Result(
                    plan.Status,
                    exact.OperationId,
                    exact.RequestHash,
                    primary: candidateIdentitySubstitution ? null : Project(read.PrimarySnapshot),
                    related: candidateIdentitySubstitution ? null : Project(read.RelatedSnapshot));
            }

            var mutation = BuildMutation(
                exact,
                read.StoreGeneration,
                plan,
                authorization.ActorId!,
                authorization.AuthorityEvidenceHash,
                dependency.DependencyEvidenceHash,
                dependency.RecordedAtUtc);
            if (mutation is null)
            {
                return Result(HumanInputRequestLifecycleMutationStatus.Ambiguous, exact.OperationId, exact.RequestHash);
            }

            cancellationToken.ThrowIfCancellationRequested();
            HumanInputRequestLifecycleStoreCommitResult? commit;
            try
            {
                commit = await _store.CommitAsync(mutation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await RecoverAfterIntentAsync(exact).ConfigureAwait(false);
            }

            var mapped = await MapCommitAsync(commit, mutation, plan, exact).ConfigureAwait(false);
            if (mapped.Retry)
            {
                continue;
            }

            return mapped.Result!;
        }

        return Result(HumanInputRequestLifecycleMutationStatus.Conflict, exact.OperationId, exact.RequestHash);
    }

    private async Task<(HumanInputRequestLifecycleMutationStatus Status, DateTimeOffset RecordedAtUtc, AuthorityGrant? Grant, string DependencyEvidenceHash)> EstablishDependencyTimeAsync(
        HumanInputRequestLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        if (!RequiresGrant(command.Kind))
        {
            var now = UtcNow();
            return now == default
                ? (HumanInputRequestLifecycleMutationStatus.Unavailable, default, null, string.Empty)
                : (HumanInputRequestLifecycleMutationStatus.Committed, now, null, string.Empty);
        }

        AuthorityGrantResolution resolution;
        try
        {
            resolution = await _grantResolver.ResolveAsync(command.GrantReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (HumanInputRequestLifecycleMutationStatus.GrantUnavailable, default, null, string.Empty);
        }

        if (!IsExactActiveGrantResolution(resolution, command.GrantReference))
        {
            return (HumanInputRequestLifecycleMutationStatus.GrantUnavailable, default, null, string.Empty);
        }

        return (
            HumanInputRequestLifecycleMutationStatus.Committed,
            resolution.EvaluatedAtUtc,
            resolution.Grant,
            resolution.DependencyEvidenceHash);
    }

    private async Task<HumanInputRequestLifecycleActorAuthorization> AuthorizeAsync(
        HumanInputRequestLifecycleCommand command,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        HumanInputRequestLifecycleActorAuthorization decision;
        try
        {
            decision = await _authorizer.AuthorizeAsync(
                new HumanInputRequestLifecycleActorAuthorizationRequest(command, command.RequestHash, _workspaceId, evaluatedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UnavailableAuthorization(command, evaluatedAtUtc);
        }

        if (decision is null
            || !Enum.IsDefined(decision.Status)
            || decision.Status == HumanInputRequestLifecycleActorAuthorizationStatus.Unknown
            || !string.Equals(decision.OperationId, command.OperationId, StringComparison.Ordinal)
            || !string.Equals(decision.RequestHash, command.RequestHash, StringComparison.Ordinal)
            || !string.Equals(decision.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || decision.EvaluatedAtUtc != evaluatedAtUtc
            || decision.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || decision.Status is HumanInputRequestLifecycleActorAuthorizationStatus.Authorized or HumanInputRequestLifecycleActorAuthorizationStatus.Denied
                && (decision.ActorId is null
                    || !AuthorityActorId.TryParse(decision.ActorId.Value, out _, out _)
                    || !IsSha256(decision.AuthorityEvidenceHash)))
        {
            return UnavailableAuthorization(command, evaluatedAtUtc);
        }

        return decision;
    }

    private async Task<HumanInputRequestLifecycleStoreReadResult> ObserveAsync(
        HumanInputRequestLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        HumanInputRequestLifecycleStoreReadResult read;
        var relatedRequestId = command.Kind == HumanInputRequestLifecycleOperationKind.Supersede
            ? command.CandidateRequest?.RequestId
            : null;
        try
        {
            read = await _store.ReadForMutationAsync(
                command.RequestId,
                command.OperationId,
                command.RequestHash,
                relatedRequestId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UnavailableRead();
        }

        if (read is null
            || read.StoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || !Enum.IsDefined(read.Status)
            || read.Status == HumanInputRequestLifecycleStoreReadStatus.Unknown
            || !IsValidStoredOperation(read.ExistingOperation, command.OperationId))
        {
            return AmbiguousRead();
        }

        HumanInputRequestLifecycleStoreSnapshot? primary = null;
        if (read.PrimarySnapshot is not null
            && !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(read.PrimarySnapshot, command.RequestId, out primary))
        {
            return AmbiguousRead();
        }

        HumanInputRequestLifecycleStoreSnapshot? related = null;
        if (read.RelatedSnapshot is not null)
        {
            if (relatedRequestId is null
                || !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(read.RelatedSnapshot, relatedRequestId, out related))
            {
                return AmbiguousRead();
            }
        }

        if (read.StoreGeneration < (primary?.Operations.Count ?? 0)
            || read.StoreGeneration < (related?.Operations.Count ?? 0))
        {
            return AmbiguousRead();
        }

        if (!await ValidateSnapshotGraphAsync(read.StoreGeneration, [primary, related], cancellationToken).ConfigureAwait(false))
        {
            return AmbiguousRead();
        }

        if (read.ExistingOperation is { } existing)
        {
            var exact = string.Equals(existing.RequestId, command.RequestId, StringComparison.Ordinal)
                && string.Equals(existing.Evidence.RequestHash, command.RequestHash, StringComparison.Ordinal);
            if (exact)
            {
                if (read.StoreGeneration == 0
                    || !HumanInputRequestLifecycleStoreSnapshotGuard.EvidenceMatchesSnapshots(existing.Evidence, primary, related))
                {
                    return AmbiguousRead();
                }

                if (existing.Evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede
                    && existing.Evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                    && !HumanInputRequestLifecycleStoreSnapshotGuard.ValidatePairedSupersede(primary, related, existing.Evidence))
                {
                    return AmbiguousRead();
                }
            }
            else if (read.Status != HumanInputRequestLifecycleStoreReadStatus.OperationConflict)
            {
                return AmbiguousRead();
            }
        }

        var sanitized = read.Status switch
        {
            HumanInputRequestLifecycleStoreReadStatus.Ready when primary is not null => read with { PrimarySnapshot = primary, RelatedSnapshot = related },
            HumanInputRequestLifecycleStoreReadStatus.NotFound when primary is null => read with { PrimarySnapshot = null, RelatedSnapshot = related },
            HumanInputRequestLifecycleStoreReadStatus.OperationConflict when related is null => read with { PrimarySnapshot = primary, RelatedSnapshot = null },
            HumanInputRequestLifecycleStoreReadStatus.Unavailable when read.StoreGeneration == 0 && primary is null && related is null && read.ExistingOperation is null => UnavailableRead(),
            HumanInputRequestLifecycleStoreReadStatus.Ambiguous => AmbiguousRead(),
            _ => AmbiguousRead(),
        };

        if (!WorkspaceMatches(sanitized.PrimarySnapshot) || !WorkspaceMatches(sanitized.RelatedSnapshot))
        {
            return AmbiguousRead();
        }

        return sanitized;
    }

    private async Task<bool> ValidateSnapshotGraphAsync(
        long expectedStoreGeneration,
        IEnumerable<HumanInputRequestLifecycleStoreSnapshot?> seeds,
        CancellationToken cancellationToken)
    {
        try
        {
            if (expectedStoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore)
            {
                return false;
            }

            var snapshots = new Dictionary<string, HumanInputRequestLifecycleStoreSnapshot>(StringComparer.Ordinal);
            var pending = new Queue<HumanInputRequestLifecycleStoreSnapshot>();
            foreach (var seed in seeds)
            {
                if (seed is null)
                {
                    continue;
                }

                if (expectedStoreGeneration < seed.Operations.Count
                    || !snapshots.TryAdd(seed.Head.RequestId, seed))
                {
                    return false;
                }

                pending.Enqueue(seed);
            }

            while (pending.TryDequeue(out var snapshot))
            {
                foreach (var requestId in HumanInputRequestLifecycleStoreSnapshotGuard.RequiredSupersedeRequestIds(snapshot))
                {
                    if (snapshots.ContainsKey(requestId))
                    {
                        continue;
                    }

                    if (snapshots.Count >= HumanInputRequestLifecycleContractLimits.MaxRequestsPerStore)
                    {
                        return false;
                    }

                    HumanInputRequestLifecycleStoreReadResult read;
                    try
                    {
                        read = await _store.ReadAsync(requestId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        return false;
                    }

                    if (read is not
                        {
                            Status: HumanInputRequestLifecycleStoreReadStatus.Ready,
                            PrimarySnapshot: { } source,
                            RelatedSnapshot: null,
                            ExistingOperation: null,
                        }
                        || read.StoreGeneration != expectedStoreGeneration
                        || !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(source, requestId, out var captured)
                        || captured is null
                        || read.StoreGeneration < captured.Operations.Count
                        || !WorkspaceMatches(captured))
                    {
                        return false;
                    }

                    snapshots.Add(requestId, captured);
                    pending.Enqueue(captured);
                }
            }

            return HumanInputRequestLifecycleStoreSnapshotGuard.ValidateCommittedSupersedeGraph(snapshots)
                && HumanInputRequestLifecycleStoreSnapshotGuard.ValidateOperationOccurrences(snapshots, expectedStoreGeneration)
                && HumanInputRequestLifecycleStoreSnapshotGuard.ValidateRequestVersionIdentities(snapshots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static HumanInputRequestLifecycleMutationResult? ResolveExisting(
        HumanInputRequestLifecycleStoreReadResult read,
        HumanInputRequestLifecycleCommand command)
    {
        if (read.ExistingOperation is not { } stored)
        {
            return read.Status == HumanInputRequestLifecycleStoreReadStatus.OperationConflict
                ? Result(HumanInputRequestLifecycleMutationStatus.Conflict, command.OperationId, command.RequestHash)
                : null;
        }

        if (read.Status == HumanInputRequestLifecycleStoreReadStatus.OperationConflict
            || !string.Equals(stored.RequestId, command.RequestId, StringComparison.Ordinal)
            || !string.Equals(stored.Evidence.RequestHash, command.RequestHash, StringComparison.Ordinal))
        {
            return Result(HumanInputRequestLifecycleMutationStatus.Conflict, command.OperationId, command.RequestHash);
        }

        if (!OperationMatchesCommand(stored.Evidence, command))
        {
            return Result(HumanInputRequestLifecycleMutationStatus.Ambiguous, command.OperationId, command.RequestHash);
        }

        return TryBuildProvedResult(
            HumanInputRequestLifecycleMutationStatus.Replayed,
            command,
            stored.Evidence,
            read.PrimarySnapshot,
            read.RelatedSnapshot,
            out var result)
            ? result
            : Result(HumanInputRequestLifecycleMutationStatus.Ambiguous, command.OperationId, command.RequestHash);
    }

    private static HumanInputRequestLifecycleMutationResult? MapReadFailure(
        HumanInputRequestLifecycleStoreReadResult read,
        HumanInputRequestLifecycleCommand command)
        => read.Status switch
        {
            HumanInputRequestLifecycleStoreReadStatus.OperationConflict => Result(HumanInputRequestLifecycleMutationStatus.Conflict, command.OperationId, command.RequestHash),
            HumanInputRequestLifecycleStoreReadStatus.Unavailable => Result(HumanInputRequestLifecycleMutationStatus.Unavailable, command.OperationId, command.RequestHash),
            HumanInputRequestLifecycleStoreReadStatus.Ambiguous => Result(HumanInputRequestLifecycleMutationStatus.Ambiguous, command.OperationId, command.RequestHash),
            _ => null,
        };

    private static HumanInputRequestLifecycleMutationPlan Plan(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related,
        DateTimeOffset recordedAtUtc,
        bool candidateIdentitySubstitution)
    {
        var previousHead = primary?.Head;
        var previousRequest = HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(primary, previousHead?.CurrentRequest);
        var candidate = command.CandidateRequest;
        var relatedId = command.Kind == HumanInputRequestLifecycleOperationKind.Supersede ? candidate!.RequestId : null;
        var relatedHead = related?.Head;

        if (candidateIdentitySubstitution)
        {
            return Unpersistable(
                command,
                previousHead,
                relatedHead,
                previousRequest,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        }

        if (primary?.Operations.Count >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest
            || related?.Operations.Count >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest)
        {
            return Unpersistable(
                command,
                previousHead,
                relatedHead,
                previousRequest,
                HumanInputRequestLifecycleMutationStatus.LimitExceeded,
                HumanInputRequestLifecycleOperationFailureCode.OperationEvidenceLimitExceeded);
        }

        if (primary is null)
        {
            return command.Kind == HumanInputRequestLifecycleOperationKind.Create
                ? PlanCreate(command, candidate!, recordedAtUtc)
                : Receipt(
                    command,
                    null,
                    null,
                    relatedId,
                    relatedHead,
                    relatedHead,
                    null,
                    candidate,
                    HumanInputRequestLifecycleMutationStatus.NotFound,
                    HumanInputRequestLifecycleOperationOutcome.NotFound,
                    HumanInputRequestLifecycleOperationFailureCode.LifecycleNotFound);
        }

        if (previousRequest is null)
        {
            return Unpersistable(command, previousHead, relatedHead, null, HumanInputRequestLifecycleMutationStatus.Ambiguous, HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
        }

        if (recordedAtUtc < previousHead!.UpdatedAtUtc || relatedHead is not null && recordedAtUtc < relatedHead.UpdatedAtUtc)
        {
            return Unpersistable(command, previousHead, relatedHead, previousRequest, HumanInputRequestLifecycleMutationStatus.Unavailable, HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
        }

        if (command.Kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                null,
                null,
                null,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationOutcome.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.LifecycleAlreadyExists);
        }

        if (!ExpectedMatches(command, previousHead, previousRequest))
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                relatedId,
                relatedHead,
                relatedHead,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationOutcome.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
        }

        if (previousHead.Status != HumanInputRequestLifecycleStatus.Pending)
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                relatedId,
                relatedHead,
                relatedHead,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationOutcome.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.LifecycleTerminal);
        }

        if (previousHead.LifecycleVersion >= HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion)
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                relatedId,
                relatedHead,
                relatedHead,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.LimitExceeded,
                HumanInputRequestLifecycleOperationOutcome.LimitExceeded,
                HumanInputRequestLifecycleOperationFailureCode.LifecycleVersionLimitExceeded);
        }

        if (candidate is not null && CandidateReferenceExists(candidate, primary, related))
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                relatedId,
                relatedHead,
                relatedHead,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationOutcome.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        }

        if (command.Kind is HumanInputRequestLifecycleOperationKind.Reroute or HumanInputRequestLifecycleOperationKind.Amend
            && primary.RequestVersions.Count >= HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest)
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                null,
                null,
                null,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.LimitExceeded,
                HumanInputRequestLifecycleOperationOutcome.LimitExceeded,
                HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded);
        }

        if (command.Kind == HumanInputRequestLifecycleOperationKind.Remind
            && previousHead.ReminderCount >= HumanInputRequestLifecycleContractLimits.MaxReminderCount)
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                null,
                null,
                null,
                previousRequest,
                null,
                HumanInputRequestLifecycleMutationStatus.LimitExceeded,
                HumanInputRequestLifecycleOperationOutcome.LimitExceeded,
                HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded);
        }

        if (command.Kind == HumanInputRequestLifecycleOperationKind.Supersede && related is not null)
        {
            return Receipt(
                command,
                previousHead,
                previousHead,
                relatedId,
                relatedHead,
                relatedHead,
                previousRequest,
                candidate,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationOutcome.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        }

        return PlanTransition(command, previousHead, previousRequest, candidate, recordedAtUtc);
    }

    private static HumanInputRequestLifecycleMutationPlan PlanCreate(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequest candidate,
        DateTimeOffset recordedAtUtc)
    {
        if (recordedAtUtc < candidate.Timing.RequestedAtUtc
            || recordedAtUtc > candidate.Timing.ExpiresAtUtc)
        {
            return Receipt(
                command,
                null,
                null,
                null,
                null,
                null,
                null,
                candidate,
                HumanInputRequestLifecycleMutationStatus.Conflict,
                HumanInputRequestLifecycleOperationOutcome.Conflict,
                HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
        }

        var reference = Reference(candidate);
        var result = new HumanInputRequestLifecycleHead(
            HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
            candidate.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            reference,
            0,
            null,
            null,
            command.OperationId,
            recordedAtUtc);
        var plan = new HumanInputRequestLifecycleMutationPlan(
            HumanInputRequestLifecycleMutationStatus.Committed,
            HumanInputRequestLifecycleOperationOutcome.Committed,
            HumanInputRequestLifecycleOperationFailureCode.None,
            null,
            result,
            null,
            null,
            null,
            null,
            candidate,
            candidate,
            true);
        return CommittedTransitionIsValid(command, plan) ? plan : InvalidCommittedPlan(command, plan);
    }

    private static HumanInputRequestLifecycleMutationPlan PlanTransition(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleHead previousHead,
        HumanInputRequest previousRequest,
        HumanInputRequest? candidate,
        DateTimeOffset recordedAtUtc)
    {
        HumanInputRequestLifecycleHead result;
        HumanInputRequestLifecycleHead? relatedResult = null;
        switch (command.Kind)
        {
            case HumanInputRequestLifecycleOperationKind.Remind:
                result = previousHead with
                {
                    LifecycleVersion = previousHead.LifecycleVersion + 1,
                    ReminderCount = previousHead.ReminderCount + 1,
                    LastOperationId = command.OperationId,
                    UpdatedAtUtc = recordedAtUtc,
                };
                break;
            case HumanInputRequestLifecycleOperationKind.Reroute:
            case HumanInputRequestLifecycleOperationKind.Amend:
                result = previousHead with
                {
                    LifecycleVersion = previousHead.LifecycleVersion + 1,
                    CurrentRequest = Reference(candidate!),
                    LastOperationId = command.OperationId,
                    UpdatedAtUtc = recordedAtUtc,
                };
                break;
            case HumanInputRequestLifecycleOperationKind.Reject:
            case HumanInputRequestLifecycleOperationKind.Cancel:
            case HumanInputRequestLifecycleOperationKind.Expire:
                result = previousHead with
                {
                    LifecycleVersion = previousHead.LifecycleVersion + 1,
                    Status = command.Kind switch
                    {
                        HumanInputRequestLifecycleOperationKind.Reject => HumanInputRequestLifecycleStatus.Rejected,
                        HumanInputRequestLifecycleOperationKind.Cancel => HumanInputRequestLifecycleStatus.Cancelled,
                        _ => HumanInputRequestLifecycleStatus.Expired,
                    },
                    LastOperationId = command.OperationId,
                    UpdatedAtUtc = recordedAtUtc,
                };
                break;
            case HumanInputRequestLifecycleOperationKind.Supersede:
                result = previousHead with
                {
                    LifecycleVersion = previousHead.LifecycleVersion + 1,
                    Status = HumanInputRequestLifecycleStatus.Superseded,
                    SupersededByRequestId = candidate!.RequestId,
                    LastOperationId = command.OperationId,
                    UpdatedAtUtc = recordedAtUtc,
                };
                relatedResult = new HumanInputRequestLifecycleHead(
                    HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
                    candidate.RequestId,
                    1,
                    HumanInputRequestLifecycleStatus.Pending,
                    Reference(candidate),
                    0,
                    previousHead.RequestId,
                    null,
                    command.OperationId,
                    recordedAtUtc);
                break;
            default:
                return Unpersistable(command, previousHead, null, previousRequest, HumanInputRequestLifecycleMutationStatus.Ambiguous, HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
        }

        var plan = new HumanInputRequestLifecycleMutationPlan(
            HumanInputRequestLifecycleMutationStatus.Committed,
            HumanInputRequestLifecycleOperationOutcome.Committed,
            HumanInputRequestLifecycleOperationFailureCode.None,
            previousHead,
            result,
            command.Kind == HumanInputRequestLifecycleOperationKind.Supersede ? candidate!.RequestId : null,
            null,
            relatedResult,
            previousRequest,
            candidate,
            candidate,
            true);
        return CommittedTransitionIsValid(command, plan) ? plan : InvalidCommittedPlan(command, plan);
    }

    private static HumanInputRequestLifecycleMutationPlan InvalidCommittedPlan(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleMutationPlan proposed)
    {
        var evidence = EvidenceForValidation(command, proposed, AuthorityActorIdForValidation(), recordedAtUtc: proposed.ResultHead!.UpdatedAtUtc);
        var validation = HumanInputRequestLifecycleValidator.ValidateCommittedTransition(evidence, proposed.PreviousRequest, proposed.CandidateRequest);
        if (validation.Errors.Any(error => error.Code == HumanInputRequestLifecycleValidationErrorCode.TimingBoundaryConflict))
        {
            return ReceiptFromProposed(command, proposed, HumanInputRequestLifecycleOperationFailureCode.TimingBoundaryConflict);
        }

        if (command.Kind is HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede)
        {
            return ReceiptFromProposed(command, proposed, HumanInputRequestLifecycleOperationFailureCode.CandidateRequestConflict);
        }

        return Unpersistable(command, proposed.PreviousHead, proposed.RelatedPreviousHead, proposed.PreviousRequest, HumanInputRequestLifecycleMutationStatus.Ambiguous, HumanInputRequestLifecycleOperationFailureCode.OptimisticStateConflict);
    }

    private static HumanInputRequestLifecycleMutationPlan ReceiptFromProposed(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleMutationPlan proposed,
        HumanInputRequestLifecycleOperationFailureCode failureCode)
        => Receipt(
            command,
            proposed.PreviousHead,
            proposed.PreviousHead,
            proposed.RelatedRequestId,
            proposed.RelatedPreviousHead,
            proposed.RelatedPreviousHead,
            proposed.PreviousRequest,
            proposed.CandidateRequest,
            HumanInputRequestLifecycleMutationStatus.Conflict,
            HumanInputRequestLifecycleOperationOutcome.Conflict,
            failureCode);

    private static HumanInputRequestLifecycleMutationPlan Receipt(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleHead? previous,
        HumanInputRequestLifecycleHead? result,
        string? relatedRequestId,
        HumanInputRequestLifecycleHead? relatedPrevious,
        HumanInputRequestLifecycleHead? relatedResult,
        HumanInputRequest? previousRequest,
        HumanInputRequest? candidate,
        HumanInputRequestLifecycleMutationStatus status,
        HumanInputRequestLifecycleOperationOutcome outcome,
        HumanInputRequestLifecycleOperationFailureCode failureCode)
        => new(
            status,
            outcome,
            failureCode,
            previous,
            result,
            relatedRequestId,
            relatedPrevious,
            relatedResult,
            previousRequest,
            candidate,
            null,
            true);

    private static HumanInputRequestLifecycleMutationPlan Unpersistable(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleHead? previous,
        HumanInputRequestLifecycleHead? related,
        HumanInputRequest? previousRequest,
        HumanInputRequestLifecycleMutationStatus status,
        HumanInputRequestLifecycleOperationFailureCode failureCode)
        => new(
            status,
            status == HumanInputRequestLifecycleMutationStatus.LimitExceeded
                ? HumanInputRequestLifecycleOperationOutcome.LimitExceeded
                : HumanInputRequestLifecycleOperationOutcome.Conflict,
            failureCode,
            previous,
            previous,
            command.Kind == HumanInputRequestLifecycleOperationKind.Supersede ? command.CandidateRequest?.RequestId : null,
            related,
            related,
            previousRequest,
            command.CandidateRequest,
            null,
            false);

    private static HumanInputRequestLifecycleStoreMutation? BuildMutation(
        HumanInputRequestLifecycleCommand command,
        long expectedStoreGeneration,
        HumanInputRequestLifecycleMutationPlan plan,
        AuthorityActorId actorId,
        string authorityEvidenceHash,
        string dependencyEvidenceHash,
        DateTimeOffset recordedAtUtc)
    {
        var evidence = new HumanInputRequestLifecycleOperationEvidence(
            HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
            command.OperationId,
            command.RequestHash,
            command.Kind,
            plan.Outcome,
            plan.FailureCode,
            command.RequestId,
            command.ExpectedLifecycleVersion,
            command.ExpectedLifecycleStatus,
            command.ExpectedRequest,
            command.ExpectedBinding,
            plan.PreviousHead,
            plan.ResultHead,
            plan.RelatedRequestId,
            plan.RelatedPreviousHead,
            plan.RelatedResultHead,
            plan.CandidateRequest is null ? null : Reference(plan.CandidateRequest),
            actorId,
            command.Reason,
            command.GrantReference,
            authorityEvidenceHash,
            RequiresGrant(command.Kind) ? dependencyEvidenceHash : null,
            recordedAtUtc);
        if (!HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid
            || plan.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
                && !HumanInputRequestLifecycleValidator.ValidateCommittedTransition(evidence, plan.PreviousRequest, plan.CandidateRequest).IsValid)
        {
            return null;
        }

        return new HumanInputRequestLifecycleStoreMutation(
            expectedStoreGeneration,
            evidence,
            plan.RequestToAppend,
            plan.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed ? plan.ResultHead : null,
            plan.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed ? plan.RelatedResultHead : null);
    }

    private async Task<(bool Retry, HumanInputRequestLifecycleMutationResult? Result)> MapCommitAsync(
        HumanInputRequestLifecycleStoreCommitResult? commit,
        HumanInputRequestLifecycleStoreMutation mutation,
        HumanInputRequestLifecycleMutationPlan plan,
        HumanInputRequestLifecycleCommand command)
    {
        if (commit is null
            || commit.StoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || !Enum.IsDefined(commit.Status)
            || commit.Status == HumanInputRequestLifecycleStoreCommitStatus.Unknown)
        {
            return (false, await RecoverAfterIntentAsync(command).ConfigureAwait(false));
        }

        if (commit.Status is HumanInputRequestLifecycleStoreCommitStatus.Committed or HumanInputRequestLifecycleStoreCommitStatus.Replayed)
        {
            var proof = await ValidateCommitProofAsync(commit, mutation).ConfigureAwait(false);
            if (!proof.IsValid
                || !TryBuildProvedResult(
                    commit.Status == HumanInputRequestLifecycleStoreCommitStatus.Replayed
                        ? HumanInputRequestLifecycleMutationStatus.Replayed
                        : plan.Status,
                    command,
                    mutation.Operation,
                    proof.Primary,
                    proof.Related,
                    out var result))
            {
                return (false, await RecoverAfterIntentAsync(command).ConfigureAwait(false));
            }

            return (false, result);
        }

        if (commit.Status == HumanInputRequestLifecycleStoreCommitStatus.StoreConflict
            && commit.StoreGeneration > mutation.ExpectedStoreGeneration
            && commit.StoredOperation is null)
        {
            return (true, null);
        }

        if (commit.Status == HumanInputRequestLifecycleStoreCommitStatus.OperationConflict)
        {
            return (false, Result(HumanInputRequestLifecycleMutationStatus.Conflict, command.OperationId, command.RequestHash));
        }

        if (commit.Status == HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded && commit.StoredOperation is null)
        {
            return (false, Result(HumanInputRequestLifecycleMutationStatus.LimitExceeded, command.OperationId, command.RequestHash));
        }

        if (commit.Status == HumanInputRequestLifecycleStoreCommitStatus.Unavailable && commit.StoredOperation is null)
        {
            return (false, Result(HumanInputRequestLifecycleMutationStatus.Unavailable, command.OperationId, command.RequestHash));
        }

        return (false, await RecoverAfterIntentAsync(command).ConfigureAwait(false));
    }

    private async Task<HumanInputRequestLifecycleMutationResult> RecoverAfterIntentAsync(
        HumanInputRequestLifecycleCommand command)
    {
        HumanInputRequestLifecycleStoreReadResult recovered;
        try
        {
            recovered = await ObserveAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Result(HumanInputRequestLifecycleMutationStatus.Ambiguous, command.OperationId, command.RequestHash);
        }

        return ResolveExisting(recovered, command)
            ?? MapReadFailure(recovered, command)
            ?? Result(
                HumanInputRequestLifecycleMutationStatus.Ambiguous,
                command.OperationId,
                command.RequestHash,
                primary: ProjectWithinScope(recovered.PrimarySnapshot, ProjectionBinding(command)),
                related: ProjectWithinScope(recovered.RelatedSnapshot, ProjectionBinding(command)));
    }

    private async Task<(bool IsValid, HumanInputRequestLifecycleStoreSnapshot? Primary, HumanInputRequestLifecycleStoreSnapshot? Related)> ValidateCommitProofAsync(
        HumanInputRequestLifecycleStoreCommitResult commit,
        HumanInputRequestLifecycleStoreMutation mutation)
    {
        if (mutation.ExpectedStoreGeneration is < 0 or >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || commit.StoredOperation is null
            || !string.Equals(commit.StoredOperation.RequestId, mutation.Operation.TargetRequestId, StringComparison.Ordinal)
            || !Equals(commit.StoredOperation.Evidence, mutation.Operation))
        {
            return (false, null, null);
        }

        var firstGeneration = mutation.ExpectedStoreGeneration + 1;
        if (commit.Status == HumanInputRequestLifecycleStoreCommitStatus.Committed
                ? commit.StoreGeneration != firstGeneration
                : commit.StoreGeneration < firstGeneration)
        {
            return (false, null, null);
        }

        HumanInputRequestLifecycleStoreSnapshot? primary = null;
        if (commit.PrimarySnapshot is not null
            && !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(commit.PrimarySnapshot, mutation.Operation.TargetRequestId, out primary))
        {
            return (false, null, null);
        }

        HumanInputRequestLifecycleStoreSnapshot? related = null;
        if (commit.RelatedSnapshot is not null)
        {
            if (mutation.Operation.RelatedRequestId is not { } relatedRequestId
                || !HumanInputRequestLifecycleStoreSnapshotGuard.TryCapture(commit.RelatedSnapshot, relatedRequestId, out related))
            {
                return (false, null, null);
            }
        }

        if (!HumanInputRequestLifecycleStoreSnapshotGuard.EvidenceMatchesSnapshots(mutation.Operation, primary, related)
            || !await ValidateSnapshotGraphAsync(commit.StoreGeneration, [primary, related], CancellationToken.None).ConfigureAwait(false))
        {
            return (false, null, null);
        }

        if (commit.Status == HumanInputRequestLifecycleStoreCommitStatus.Committed
            && (!Equals(primary?.Head, mutation.Operation.ResultHead)
                || !Equals(related?.Head, mutation.Operation.RelatedResultHead)))
        {
            return (false, null, null);
        }

        if (mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Supersede
            && mutation.Operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed)
        {
            return (related is not null
                && HumanInputRequestLifecycleStoreSnapshotGuard.ValidatePairedSupersede(primary, related, mutation.Operation), primary, related);
        }

        return (mutation.Operation.Kind == HumanInputRequestLifecycleOperationKind.Supersede
            || related is null, primary, related);
    }

    private static bool TryBuildProvedResult(
        HumanInputRequestLifecycleMutationStatus status,
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleOperationEvidence evidence,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related,
        out HumanInputRequestLifecycleMutationResult? result)
    {
        result = null;
        if (!HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid
            || !HumanInputRequestLifecycleStoreSnapshotGuard.EvidenceMatchesSnapshots(evidence, primary, related))
        {
            return false;
        }

        HumanInputDeliveryOpportunity? opportunity = null;
        var projectionBinding = ProjectionBinding(command, evidence);
        if (evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed)
        {
            var transitionIsValid = evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede
                ? HumanInputRequestLifecycleStoreSnapshotGuard.ValidatePairedSupersede(primary, related, evidence)
                : HumanInputRequestLifecycleValidator.ValidateCommittedTransition(
                    evidence,
                    HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(primary, evidence.PreviousHead?.CurrentRequest),
                    HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(primary, evidence.CandidateRequest)).IsValid;
            if (!transitionIsValid)
            {
                return false;
            }

            if (RequiresGrant(evidence.Kind))
            {
                var deliveryHead = evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede
                    ? evidence.RelatedResultHead
                    : evidence.ResultHead;
                var currentDeliveryHead = evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede
                    ? related?.Head
                    : primary?.Head;
                if (deliveryHead is null)
                {
                    return false;
                }

                var currentDeliverySnapshot = evidence.Kind == HumanInputRequestLifecycleOperationKind.Supersede ? related : primary;
                if (currentDeliveryHead is { Status: HumanInputRequestLifecycleStatus.Pending }
                    && Equals(currentDeliveryHead, deliveryHead)
                    && ProjectWithinScope(currentDeliverySnapshot, projectionBinding) is not null)
                {
                    opportunity = new HumanInputDeliveryOpportunity(
                        HumanInputDeliveryOpportunity.CurrentSchemaVersion,
                        evidence.OperationId,
                        deliveryHead.CurrentRequest,
                        deliveryHead.LifecycleVersion,
                        evidence.RecordedAtUtc);
                }
            }
        }

        result = Result(
            status,
            command.OperationId,
            command.RequestHash,
            Proof(evidence),
            ProjectWithinScope(primary, projectionBinding),
            ProjectWithinScope(related, projectionBinding),
            opportunity);
        return true;
    }

    private static bool OperationMatchesCommand(
        HumanInputRequestLifecycleOperationEvidence evidence,
        HumanInputRequestLifecycleCommand command)
    {
        if (!string.Equals(evidence.OperationId, command.OperationId, StringComparison.Ordinal)
            || !string.Equals(evidence.RequestHash, command.RequestHash, StringComparison.Ordinal)
            || evidence.Kind != command.Kind
            || !string.Equals(evidence.TargetRequestId, command.RequestId, StringComparison.Ordinal)
            || evidence.ExpectedLifecycleVersion != command.ExpectedLifecycleVersion
            || evidence.ExpectedLifecycleStatus != command.ExpectedLifecycleStatus
            || !Equals(evidence.ExpectedRequest, command.ExpectedRequest)
            || !Equals(evidence.ExpectedBinding, command.ExpectedBinding)
            || !Equals(evidence.Reason, command.Reason)
            || !Equals(evidence.GrantReference, command.GrantReference))
        {
            return false;
        }

        if (command.Kind != HumanInputRequestLifecycleOperationKind.Create
            && evidence.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
            && (evidence.PreviousHead is not { } previous
            || previous.LifecycleVersion != command.ExpectedLifecycleVersion
            || previous.Status != command.ExpectedLifecycleStatus
            || !Equals(previous.CurrentRequest, command.ExpectedRequest)))
        {
            return false;
        }

        var candidateReference = command.CandidateRequest is null ? null : Reference(command.CandidateRequest);
        return Equals(evidence.CandidateRequest, candidateReference)
            && (command.Kind == HumanInputRequestLifecycleOperationKind.Supersede
                ? string.Equals(evidence.RelatedRequestId, command.CandidateRequest!.RequestId, StringComparison.Ordinal)
                : evidence.RelatedRequestId is null);
    }

    private static bool CommittedTransitionIsValid(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleMutationPlan plan)
    {
        var evidence = EvidenceForValidation(command, plan, AuthorityActorIdForValidation(), plan.ResultHead!.UpdatedAtUtc);
        return HumanInputRequestLifecycleValidator.ValidateCommittedTransition(evidence, plan.PreviousRequest, plan.CandidateRequest).IsValid;
    }

    private static HumanInputRequestLifecycleOperationEvidence EvidenceForValidation(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleMutationPlan plan,
        AuthorityActorId actorId,
        DateTimeOffset recordedAtUtc)
        => new(
            HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
            command.OperationId,
            command.RequestHash,
            command.Kind,
            plan.Outcome,
            plan.FailureCode,
            command.RequestId,
            command.ExpectedLifecycleVersion,
            command.ExpectedLifecycleStatus,
            command.ExpectedRequest,
            command.ExpectedBinding,
            plan.PreviousHead,
            plan.ResultHead,
            plan.RelatedRequestId,
            plan.RelatedPreviousHead,
            plan.RelatedResultHead,
            plan.CandidateRequest is null ? null : Reference(plan.CandidateRequest),
            actorId,
            command.Reason,
            command.GrantReference,
            new string('a', HumanInputRequestLifecycleContractLimits.Sha256HexCharacters),
            RequiresGrant(command.Kind) ? new string('b', HumanInputRequestLifecycleContractLimits.Sha256HexCharacters) : null,
            recordedAtUtc);

    private static bool ExpectedMatches(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleHead head,
        HumanInputRequest request)
        => head.LifecycleVersion == command.ExpectedLifecycleVersion
            && head.Status == command.ExpectedLifecycleStatus
            && Equals(head.CurrentRequest, command.ExpectedRequest)
            && Equals(request.Binding, command.ExpectedBinding);

    private static bool CandidateReferenceExists(
        HumanInputRequest candidate,
        HumanInputRequestLifecycleStoreSnapshot primary,
        HumanInputRequestLifecycleStoreSnapshot? related)
        => SnapshotContainsCandidateReference(primary, candidate)
            || related is not null && SnapshotContainsCandidateReference(related, candidate);

    private static bool CandidateIdentityHasDifferentHash(
        HumanInputRequest candidate,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related)
        => primary is not null && SnapshotContainsCandidateIdentityWithDifferentHash(primary, candidate)
            || related is not null && SnapshotContainsCandidateIdentityWithDifferentHash(related, candidate);

    private static bool SnapshotContainsCandidateReference(
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        HumanInputRequest candidate)
        => snapshot.RequestVersions.Any(request => SameCandidateIdentity(request.RequestId, request.RequestVersionId, candidate)
                && string.Equals(request.RequestHash, candidate.RequestHash, StringComparison.Ordinal))
            || snapshot.Operations.Any(operation => operation.CandidateRequest is { } reference
                && SameCandidateIdentity(reference.RequestId, reference.RequestVersionId, candidate)
                && string.Equals(reference.RequestHash, candidate.RequestHash, StringComparison.Ordinal));

    private static bool SnapshotContainsCandidateIdentityWithDifferentHash(
        HumanInputRequestLifecycleStoreSnapshot snapshot,
        HumanInputRequest candidate)
        => snapshot.RequestVersions.Any(request => SameCandidateIdentity(request.RequestId, request.RequestVersionId, candidate)
                && !string.Equals(request.RequestHash, candidate.RequestHash, StringComparison.Ordinal))
            || snapshot.Operations.Any(operation => operation.CandidateRequest is { } reference
                && SameCandidateIdentity(reference.RequestId, reference.RequestVersionId, candidate)
                && !string.Equals(reference.RequestHash, candidate.RequestHash, StringComparison.Ordinal));

    private static bool SameCandidateIdentity(
        string requestId,
        string requestVersionId,
        HumanInputRequest candidate)
        => string.Equals(requestId, candidate.RequestId, StringComparison.Ordinal)
            && string.Equals(requestVersionId, candidate.RequestVersionId, StringComparison.Ordinal);

    private bool GrantBindsObservedOperation(
        AuthorityGrant? grant,
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related)
    {
        if (!RequiresGrant(command.Kind))
        {
            return grant is null;
        }

        if (grant is null)
        {
            return false;
        }

        var hasBindingArtifact = false;
        if (command.ExpectedBinding is { } expectedBinding)
        {
            if (!BindingBindsGrant(expectedBinding, grant))
            {
                return false;
            }

            hasBindingArtifact = true;
        }

        if (command.CandidateRequest is { } candidate)
        {
            if (!RequestBindsGrant(candidate, grant))
            {
                return false;
            }

            hasBindingArtifact = true;
        }

        if (primary is not null)
        {
            var primaryRequest = HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(primary, primary.Head.CurrentRequest);
            if (primaryRequest is null || !RequestBindsGrant(primaryRequest, grant))
            {
                return false;
            }

            hasBindingArtifact = true;
        }

        if (related is not null)
        {
            var relatedRequest = HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(related, related.Head.CurrentRequest);
            if (relatedRequest is null || !RequestBindsGrant(relatedRequest, grant))
            {
                return false;
            }

            hasBindingArtifact = true;
        }

        return hasBindingArtifact;
    }

    private static bool ObservedBindingsMatchExpectedScope(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleStoreSnapshot? primary,
        HumanInputRequestLifecycleStoreSnapshot? related)
    {
        var expected = command.ExpectedBinding ?? command.CandidateRequest?.Binding;
        if (expected is null
            || command.CandidateRequest is { } candidate && !Equals(candidate.Binding, expected))
        {
            return false;
        }

        return SnapshotBindingMatches(primary, expected)
            && SnapshotBindingMatches(related, expected);
    }

    private static bool SnapshotBindingMatches(
        HumanInputRequestLifecycleStoreSnapshot? snapshot,
        HumanInputRequestBinding expected)
    {
        if (snapshot is null)
        {
            return true;
        }

        var request = HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(snapshot, snapshot.Head.CurrentRequest);
        return request is not null && Equals(request.Binding, expected);
    }

    private bool RequestBindsGrant(HumanInputRequest request, AuthorityGrant grant)
        => BindingBindsGrant(request.Binding, grant);

    private bool BindingBindsGrant(HumanInputRequestBinding binding, AuthorityGrant grant)
        => string.Equals(binding.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && string.Equals(binding.LoopGraphId, grant.Binding.Loop.Revision.GraphId, StringComparison.Ordinal)
            && string.Equals(binding.LoopRevisionId, grant.Binding.Loop.Revision.RevisionId, StringComparison.Ordinal);

    private bool WorkspaceMatches(HumanInputRequestLifecycleStoreSnapshot? snapshot)
        => snapshot is null || snapshot.RequestVersions.All(
            request => string.Equals(request.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal));

    private static bool IsExactActiveGrantResolution(
        AuthorityGrantResolution? resolution,
        AuthorityGrantReference? expected)
    {
        if (resolution is not
            {
                Status: AuthorityGrantResolutionStatus.Active,
                RequestedReference: { } requested,
                Grant: { } grant,
                EffectiveCeiling: { } ceiling,
            }
            || expected is null
            || !Equals(requested, expected)
            || !grant.GrantId.Equals(expected.GrantId)
            || !grant.Revision.Equals(expected.Revision)
            || !string.Equals(grant.ContentHash, expected.ContentHash, StringComparison.Ordinal)
            || !AuthorityGrantContractValidator.Validate(grant).IsValid
            || !AuthorityCeilingSubset.IsEqual(ceiling, grant.RequestedCeiling)
            || !IsSha256(resolution.DependencyEvidenceHash)
            || resolution.EvaluatedAtUtc == default
            || resolution.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || resolution.EvaluatedAtUtc < grant.RecordedAtUtc
            || grant.Status != AuthorityGrantLifecycleStatus.Active
            || resolution.EvaluatedAtUtc < grant.Boundary.EffectiveAtUtc
            || grant.Boundary.ExpiresAtUtc is { } expiry && resolution.EvaluatedAtUtc >= expiry)
        {
            return false;
        }

        return true;
    }

    private static bool TryCaptureCommand(
        HumanInputRequestLifecycleCommand? source,
        out HumanInputRequestLifecycleCommand? command)
    {
        command = null;
        if (source is null)
        {
            return false;
        }

        HumanInputRequest? candidate = null;
        if (source.CandidateRequest is not null
            && (!HumanInputRequestSnapshot.TryCapture(source.CandidateRequest, out candidate, out _) || candidate is null))
        {
            return false;
        }

        command = source with { CandidateRequest = candidate };
        return HumanInputRequestLifecycleCommandHash.Matches(command);
    }

    private static HumanInputRequestReference Reference(HumanInputRequest request)
    {
        if (!HumanInputRequestReference.TryCreate(request, out var reference, out _) || reference is null)
        {
            throw new InvalidOperationException("A validated Human Input request must produce an exact reference.");
        }

        return reference;
    }

    private static AuthorityActorId AuthorityActorIdForValidation()
    {
        if (!AuthorityActorId.TryParse("human-input-validator", out var actorId, out _) || actorId is null)
        {
            throw new InvalidOperationException("The internal Human Input validation actor identity must remain canonical.");
        }

        return actorId;
    }

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

    private static HumanInputRequestLifecycleActorAuthorization UnavailableAuthorization(
        HumanInputRequestLifecycleCommand command,
        DateTimeOffset evaluatedAtUtc)
        => new(
            HumanInputRequestLifecycleActorAuthorizationStatus.Unavailable,
            command.OperationId,
            command.RequestHash,
            string.Empty,
            evaluatedAtUtc,
            null,
            string.Empty);

    private static HumanInputRequestLifecycleStoreReadResult UnavailableRead()
        => new(HumanInputRequestLifecycleStoreReadStatus.Unavailable, 0, null, null, null);

    private static HumanInputRequestLifecycleStoreReadResult AmbiguousRead()
        => new(HumanInputRequestLifecycleStoreReadStatus.Ambiguous, 0, null, null, null);

    private static bool IsValidStoredOperation(
        HumanInputRequestLifecycleStoredOperation? stored,
        string operationId)
        => stored is null
            || stored.Evidence is not null
            && string.Equals(stored.Evidence.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(stored.RequestId, stored.Evidence.TargetRequestId, StringComparison.Ordinal)
            && HumanInputRequestLifecycleValidator.ValidateEvidence(stored.Evidence).IsValid;

    private static HumanInputRequestLifecycleOperationProof Proof(
        HumanInputRequestLifecycleOperationEvidence evidence)
        => new(
            evidence.SchemaVersion,
            evidence.OperationId,
            evidence.RequestHash,
            evidence.Kind,
            evidence.Outcome,
            evidence.FailureCode,
            evidence.TargetRequestId,
            evidence.PreviousHead?.LifecycleVersion,
            evidence.ResultHead?.LifecycleVersion,
            evidence.RelatedRequestId,
            evidence.RelatedPreviousHead?.LifecycleVersion,
            evidence.RelatedResultHead?.LifecycleVersion,
            evidence.RecordedAtUtc);

    private static HumanInputRequestLifecycleProjection? Project(
        HumanInputRequestLifecycleStoreSnapshot? snapshot)
        => snapshot is null ? null : HumanInputRequestLifecycleStoreSnapshotGuard.Project(snapshot.Head);

    private static HumanInputRequestLifecycleProjection? ProjectWithinScope(
        HumanInputRequestLifecycleStoreSnapshot? snapshot,
        HumanInputRequestBinding? expectedBinding)
    {
        if (snapshot is null || expectedBinding is null)
        {
            return null;
        }

        var current = HumanInputRequestLifecycleStoreSnapshotGuard.FindRequest(snapshot, snapshot.Head.CurrentRequest);
        return current is not null && Equals(current.Binding, expectedBinding)
            ? HumanInputRequestLifecycleStoreSnapshotGuard.Project(snapshot.Head)
            : null;
    }

    private static HumanInputRequestBinding? ProjectionBinding(
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleOperationEvidence? evidence = null)
    {
        if (command.Kind == HumanInputRequestLifecycleOperationKind.Create)
        {
            if (command.CandidateRequest is not { } candidate
                || evidence is not null
                    && (evidence.Kind != HumanInputRequestLifecycleOperationKind.Create
                        || evidence.CandidateRequest is not { } candidateReference
                        || !candidateReference.Matches(candidate)))
            {
                return null;
            }

            return candidate.Binding;
        }

        if (command.ExpectedBinding is not { } expected
            || evidence is not null
                && (evidence.Kind != command.Kind || !Equals(evidence.ExpectedBinding, expected)))
        {
            return null;
        }

        return expected;
    }

    private static HumanInputRequestLifecycleMutationResult Result(
        HumanInputRequestLifecycleMutationStatus status,
        string operationId,
        string requestHash,
        HumanInputRequestLifecycleOperationProof? proof = null,
        HumanInputRequestLifecycleProjection? primary = null,
        HumanInputRequestLifecycleProjection? related = null,
        HumanInputDeliveryOpportunity? deliveryOpportunity = null,
        IEnumerable<HumanInputRequestLifecycleMutationValidationError>? validationErrors = null)
        => new(
            status,
            operationId,
            requestHash,
            proof,
            primary,
            related,
            deliveryOpportunity,
            validationErrors);

    private static bool RequiresGrant(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;

    private static bool HasDurableProof(HumanInputRequestLifecycleMutationResult? result)
        => result is { Proof: { } proof }
            && proof.SchemaVersion == HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion
            && HumanInputIdentifier.IsValid(proof.OperationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters)
            && IsSha256(proof.RequestHash)
            && proof.RecordedAtUtc != default
            && proof.RecordedAtUtc.Offset == TimeSpan.Zero;

    private static string SafeOperationId(HumanInputRequestLifecycleCommand? command)
        => HumanInputIdentifier.IsValid(command?.OperationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters)
            ? command!.OperationId
            : string.Empty;

    private static bool IsSha256(string? value)
        => value is { Length: HumanInputRequestLifecycleContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
