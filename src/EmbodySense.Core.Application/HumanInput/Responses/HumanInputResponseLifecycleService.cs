using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Authenticates, validates, and durably records exact Human Input response operations under the shared authority fence.</summary>
public sealed class HumanInputResponseLifecycleService : IHumanInputResponseLifecycleService
{
    private const int MaximumCommitAttempts = 2;
    private readonly IHumanInputResponseLifecycleStore _store;
    private readonly IHumanInputResponseActorAuthenticator _authenticator;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly string _workspaceId;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a response service over server-owned authentication, time, authority-fence, and persistence boundaries.</summary>
    /// <param name="store">The atomic response lifecycle store.</param>
    /// <param name="authenticator">The current server-owned caller authenticator.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <param name="workspaceId">The exact server-configured workspace identity.</param>
    /// <param name="timeProvider">The trusted UTC clock, or the system clock when omitted.</param>
    public HumanInputResponseLifecycleService(
        IHumanInputResponseLifecycleStore store,
        IHumanInputResponseActorAuthenticator authenticator,
        ICapabilityAuthorityTransaction authorityTransaction,
        string workspaceId,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _workspaceId = HumanInputIdentifier.Require(workspaceId, nameof(workspaceId));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<HumanInputResponseLifecycleMutationResult> MutateAsync(
        HumanInputResponseLifecycleCommand? command,
        CancellationToken cancellationToken = default)
    {
        HumanInputResponseLifecycleMutationResult? completed = null;
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
            return HasDurableProof(completed)
                ? completed!
                : Result(
                    completed is null ? HumanInputResponseLifecycleMutationStatus.Unavailable : HumanInputResponseLifecycleMutationStatus.Ambiguous,
                    SafeOperationId(command),
                    SafeCommandHash(command));
        }
    }

    private async Task<HumanInputResponseLifecycleMutationResult> MutateUnderFenceAsync(
        HumanInputResponseLifecycleCommand? command,
        CancellationToken cancellationToken)
    {
        var validationErrors = HumanInputResponseLifecycleCommandValidator.Validate(command);
        if (validationErrors.Count > 0 || !TryCaptureCommand(command, out var captured))
        {
            return Result(
                HumanInputResponseLifecycleMutationStatus.Invalid,
                SafeOperationId(command),
                string.Empty,
                validationErrors: validationErrors);
        }

        validationErrors = HumanInputResponseLifecycleCommandValidator.Validate(captured);
        if (validationErrors.Count > 0)
        {
            return Result(
                HumanInputResponseLifecycleMutationStatus.Invalid,
                SafeOperationId(command),
                string.Empty,
                validationErrors: validationErrors);
        }

        var exact = captured!;
        if (!string.Equals(exact.ExpectedBinding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return Result(HumanInputResponseLifecycleMutationStatus.Conflict, exact.OperationId, exact.CommandHash);
        }

        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            var read = await ObserveAsync(exact, cancellationToken).ConfigureAwait(false);
            var existingDisposition = ExistingDisposition(read, exact);
            if (existingDisposition is HumanInputResponseLifecycleMutationStatus.Conflict or HumanInputResponseLifecycleMutationStatus.Ambiguous)
            {
                return Result(existingDisposition.Value, exact.OperationId, exact.CommandHash);
            }

            if (read.ExistingOperation is { } existing)
            {
                var replayed = await AuthenticateReplayAsync(exact, existing, read.Snapshot, cancellationToken).ConfigureAwait(false);
                return replayed;
            }

            var readFailure = MapReadFailure(read);
            if (readFailure is not null)
            {
                return Result(readFailure.Value, exact.OperationId, exact.CommandHash);
            }

            var evaluatedAtUtc = UtcNow();
            if (evaluatedAtUtc == default)
            {
                return Result(HumanInputResponseLifecycleMutationStatus.Unavailable, exact.OperationId, exact.CommandHash);
            }
            var authentication = await AuthenticateAsync(exact, evaluatedAtUtc, cancellationToken).ConfigureAwait(false);
            if (authentication.Status != HumanInputResponseActorAuthenticationStatus.Authenticated)
            {
                return Result(
                    authentication.Status == HumanInputResponseActorAuthenticationStatus.Denied
                        ? HumanInputResponseLifecycleMutationStatus.Denied
                        : HumanInputResponseLifecycleMutationStatus.Unavailable,
                    exact.OperationId,
                    exact.CommandHash);
            }

            var actor = authentication.ActorId!;
            var (plan, request, actorRoleId) = Plan(exact, read.Snapshot, actor, evaluatedAtUtc);
            if (!plan.CanPersist)
            {
                return Result(
                    plan.Status,
                    exact.OperationId,
                    exact.CommandHash,
                    projection: ProjectWithinScope(read.Snapshot, exact));
            }

            var attributedRoleId = plan.FailureCode is HumanInputResponseOperationFailureCode.RequestNotFound
                or HumanInputResponseOperationFailureCode.IneligibleRespondent
                or HumanInputResponseOperationFailureCode.IneligibleSelector
                ? null
                : actorRoleId;
            var evidence = BuildEvidence(
                exact,
                plan,
                request,
                actor,
                attributedRoleId,
                authentication.AuthenticationEvidenceHash,
                evaluatedAtUtc);
            if (evidence is null)
            {
                return Result(HumanInputResponseLifecycleMutationStatus.Ambiguous, exact.OperationId, exact.CommandHash);
            }

            var mutation = new HumanInputResponseLifecycleStoreMutation(
                read.StoreGeneration,
                evidence,
                plan.ResponseToAppend,
                plan.SelectionToAppend,
                plan.SelectionToAppend is null ? null : plan.ResultHead);
            cancellationToken.ThrowIfCancellationRequested();
            HumanInputResponseLifecycleStoreCommitResult? commit;
            try
            {
                commit = await _store.CommitAsync(mutation, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return await RecoverAfterIntentAsync(exact, actor).ConfigureAwait(false);
            }

            var mapped = await MapCommitAsync(commit, mutation, plan, exact, actor).ConfigureAwait(false);
            if (mapped.Retry)
            {
                continue;
            }
            return mapped.Result!;
        }

        return Result(HumanInputResponseLifecycleMutationStatus.Conflict, exact.OperationId, exact.CommandHash);
    }

    private async Task<HumanInputResponseLifecycleMutationResult> AuthenticateReplayAsync(
        HumanInputResponseLifecycleCommand command,
        HumanInputResponseLifecycleStoredOperation stored,
        HumanInputResponseLifecycleStoreSnapshot? snapshot,
        CancellationToken cancellationToken)
    {
        var evaluatedAtUtc = UtcNow();
        if (evaluatedAtUtc == default)
        {
            return Result(HumanInputResponseLifecycleMutationStatus.Unavailable, command.OperationId, command.CommandHash);
        }
        var authentication = await AuthenticateAsync(command, evaluatedAtUtc, cancellationToken).ConfigureAwait(false);
        if (authentication.Status != HumanInputResponseActorAuthenticationStatus.Authenticated)
        {
            return Result(
                authentication.Status == HumanInputResponseActorAuthenticationStatus.Denied
                    ? HumanInputResponseLifecycleMutationStatus.Denied
                    : HumanInputResponseLifecycleMutationStatus.Unavailable,
                command.OperationId,
                command.CommandHash);
        }
        if (!authentication.ActorId!.Equals(stored.Evidence.ActorId))
        {
            return Result(HumanInputResponseLifecycleMutationStatus.Denied, command.OperationId, command.CommandHash);
        }
        return TryBuildProvedResult(HumanInputResponseLifecycleMutationStatus.Replayed, command, stored.Evidence, snapshot, out var result)
            ? result!
            : Result(HumanInputResponseLifecycleMutationStatus.Ambiguous, command.OperationId, command.CommandHash);
    }

    private async Task<HumanInputResponseActorAuthentication> AuthenticateAsync(
        HumanInputResponseLifecycleCommand command,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        HumanInputResponseActorAuthentication? authentication;
        try
        {
            authentication = await _authenticator.AuthenticateAsync(
                new HumanInputResponseActorAuthenticationRequest(
                    command.OperationId,
                    command.Kind,
                    command.RequestId,
                    command.CommandHash,
                    _workspaceId,
                    evaluatedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UnavailableAuthentication(command, evaluatedAtUtc);
        }

        if (authentication is null
            || !Enum.IsDefined(authentication.Status)
            || authentication.Status == HumanInputResponseActorAuthenticationStatus.Unknown
            || !string.Equals(authentication.OperationId, command.OperationId, StringComparison.Ordinal)
            || !FixedEquals(authentication.CommandHash, command.CommandHash)
            || !string.Equals(authentication.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || authentication.EvaluatedAtUtc != evaluatedAtUtc
            || authentication.EvaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            return UnavailableAuthentication(command, evaluatedAtUtc);
        }

        if (authentication.Status == HumanInputResponseActorAuthenticationStatus.Authenticated)
        {
            if (authentication.ActorId is null
                || !AuthorityActorId.TryParse(authentication.ActorId.Value, out _, out _)
                || !IsSha256(authentication.AuthenticationEvidenceHash))
            {
                return UnavailableAuthentication(command, evaluatedAtUtc);
            }
        }
        else if (authentication.ActorId is not null || !string.IsNullOrEmpty(authentication.AuthenticationEvidenceHash))
        {
            return UnavailableAuthentication(command, evaluatedAtUtc);
        }
        return authentication;
    }

    private async Task<HumanInputResponseLifecycleStoreReadResult> ObserveAsync(
        HumanInputResponseLifecycleCommand command,
        CancellationToken cancellationToken)
    {
        HumanInputResponseLifecycleStoreReadResult? read;
        try
        {
            read = await _store.ReadForMutationAsync(
                command.RequestId,
                command.OperationId,
                command.CommandHash,
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
            || !Enum.IsDefined(read.Status)
            || read.Status == HumanInputResponseLifecycleStoreReadStatus.Unknown
            || read.StoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore)
        {
            return AmbiguousRead();
        }

        HumanInputResponseLifecycleStoreSnapshot? snapshot = null;
        if (read.Snapshot is not null
            && (!HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(read.Snapshot, command.RequestId, out snapshot)
                || snapshot is null
                || !WorkspaceMatches(snapshot)))
        {
            return AmbiguousRead();
        }
        if (snapshot is not null && read.StoreGeneration < snapshot.Operations.Count)
        {
            return AmbiguousRead();
        }

        HumanInputResponseLifecycleStoredOperation? existing = null;
        var exactExisting = false;
        if (read.ExistingOperation is not null)
        {
            if (!TryCaptureStoredOperation(read.ExistingOperation, command.OperationId, out existing) || existing is null)
            {
                return AmbiguousRead();
            }
            var exactIntent = string.Equals(existing.RequestId, command.RequestId, StringComparison.Ordinal)
                && FixedEquals(existing.Evidence.CommandHash, command.CommandHash);
            exactExisting = exactIntent;
            if (exactIntent)
            {
                if (read.Status == HumanInputResponseLifecycleStoreReadStatus.OperationConflict
                    || !OperationMatchesCommand(existing.Evidence, command)
                    || !StoredEvidenceFitsSnapshot(existing.Evidence, snapshot))
                {
                    return AmbiguousRead();
                }
            }
            else if (read.Status != HumanInputResponseLifecycleStoreReadStatus.OperationConflict)
            {
                return AmbiguousRead();
            }
        }
        if (!exactExisting
            && snapshot is not null
            && !Equals(snapshot.ResponseRequest, snapshot.Request.Head.CurrentRequest))
        {
            return AmbiguousRead();
        }

        return read.Status switch
        {
            HumanInputResponseLifecycleStoreReadStatus.Ready when snapshot is not null => read with { Snapshot = snapshot, ExistingOperation = existing },
            HumanInputResponseLifecycleStoreReadStatus.NotFound when snapshot is null => read with { Snapshot = null, ExistingOperation = existing },
            HumanInputResponseLifecycleStoreReadStatus.OperationConflict => read with { Snapshot = snapshot, ExistingOperation = existing },
            HumanInputResponseLifecycleStoreReadStatus.Unavailable when read.StoreGeneration == 0 && snapshot is null && existing is null => UnavailableRead(),
            HumanInputResponseLifecycleStoreReadStatus.Ambiguous => AmbiguousRead(),
            _ => AmbiguousRead(),
        };
    }

    private static HumanInputResponseLifecycleMutationStatus? ExistingDisposition(
        HumanInputResponseLifecycleStoreReadResult read,
        HumanInputResponseLifecycleCommand command)
    {
        if (read.ExistingOperation is not { } existing)
        {
            return read.Status == HumanInputResponseLifecycleStoreReadStatus.OperationConflict
                ? HumanInputResponseLifecycleMutationStatus.Conflict
                : null;
        }
        if (!string.Equals(existing.RequestId, command.RequestId, StringComparison.Ordinal)
            || !FixedEquals(existing.Evidence.CommandHash, command.CommandHash))
        {
            return read.Status == HumanInputResponseLifecycleStoreReadStatus.OperationConflict
                ? HumanInputResponseLifecycleMutationStatus.Conflict
                : HumanInputResponseLifecycleMutationStatus.Ambiguous;
        }
        return OperationMatchesCommand(existing.Evidence, command)
            ? null
            : HumanInputResponseLifecycleMutationStatus.Ambiguous;
    }

    private static HumanInputResponseLifecycleMutationStatus? MapReadFailure(HumanInputResponseLifecycleStoreReadResult read)
        => read.Status switch
        {
            HumanInputResponseLifecycleStoreReadStatus.OperationConflict => HumanInputResponseLifecycleMutationStatus.Conflict,
            HumanInputResponseLifecycleStoreReadStatus.Unavailable => HumanInputResponseLifecycleMutationStatus.Unavailable,
            HumanInputResponseLifecycleStoreReadStatus.Ambiguous => HumanInputResponseLifecycleMutationStatus.Ambiguous,
            _ => null,
        };

    private static (HumanInputResponseLifecycleMutationPlan Plan, HumanInputRequest? Request, string? ActorRoleId) Plan(
        HumanInputResponseLifecycleCommand command,
        HumanInputResponseLifecycleStoreSnapshot? snapshot,
        AuthorityActorId actorId,
        DateTimeOffset recordedAtUtc)
    {
        if (snapshot is null)
        {
            return (
                HumanInputResponsePolicyEvaluator.Failure(
                    null,
                    command.TargetResponses,
                    HumanInputResponseLifecycleMutationStatus.NotFound,
                    HumanInputResponseOperationOutcome.NotFound,
                    HumanInputResponseOperationFailureCode.RequestNotFound),
                null,
                null);
        }
        var request = FindResponseRequest(snapshot);
        if (request is null || !Equals(snapshot.ResponseRequest, snapshot.Request.Head.CurrentRequest))
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                snapshot.Request.Head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Ambiguous,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: false), null, null);
        }

        var head = snapshot.Request.Head;
        var expectedRequest = FindRequest(snapshot, command.ExpectedRequest);
        var actorRoleId = expectedRequest is null ? null : EligibleRole(expectedRequest, actorId);
        if (head.Status != HumanInputRequestLifecycleStatus.Pending)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Conflict,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.RequestTerminal), request, actorRoleId);
        }
        if (!Equals(head.CurrentRequest, command.ExpectedRequest)
            || !Equals(request.Binding, command.ExpectedBinding))
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Conflict,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.StaleResponse), request, actorRoleId);
        }
        if (head.LifecycleVersion != command.ExpectedLifecycleVersion)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Conflict,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict), request, actorRoleId);
        }
        var selectorIsEligible = command.Kind != HumanInputResponseOperationKind.Select
            || request.ResponsePolicy.Kind == HumanInputResponsePolicyKind.ManualSelection
                && request.ResponsePolicy.OrderedRoleIds is { } selectorRoles
                && actorRoleId is not null
                && selectorRoles.Contains(actorRoleId, StringComparer.Ordinal);
        if (actorRoleId is null || !selectorIsEligible)
        {
            var selector = command.Kind == HumanInputResponseOperationKind.Select;
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Ineligible,
                HumanInputResponseOperationOutcome.Rejected,
                selector
                    ? HumanInputResponseOperationFailureCode.IneligibleSelector
                    : HumanInputResponseOperationFailureCode.IneligibleRespondent), request, null);
        }
        if (recordedAtUtc < head.UpdatedAtUtc || recordedAtUtc < request.Timing.RequestedAtUtc)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Unavailable,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: false), request, actorRoleId);
        }
        if (snapshot.Operations.Count >= HumanInputResponseContractLimits.MaxOperationsPerRequest)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.LimitExceeded,
                HumanInputResponseOperationOutcome.LimitExceeded,
                HumanInputResponseOperationFailureCode.OperationEvidenceLimitExceeded,
                canPersist: false), request, actorRoleId);
        }
        if (command.Kind is HumanInputResponseOperationKind.Submit or HumanInputResponseOperationKind.Select
            && recordedAtUtc > request.Timing.ExpiresAtUtc)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Late,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.LateResponse), request, actorRoleId);
        }
        if (command.Kind == HumanInputResponseOperationKind.Submit
            && !SubmittedValueIsValid(request, command, recordedAtUtc))
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                [],
                HumanInputResponseLifecycleMutationStatus.Invalid,
                HumanInputResponseOperationOutcome.Rejected,
                HumanInputResponseOperationFailureCode.MalformedResponse), request, actorRoleId);
        }
        if (!HumanInputResponseLifecycleStoreSnapshotGuard.TryGetActiveResponses(snapshot, out var active)
            || active is null)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Ambiguous,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: false), request, actorRoleId);
        }

        var plan = HumanInputResponsePolicyEvaluator.Evaluate(
            request,
            head,
            command,
            actorId,
            actorRoleId,
            recordedAtUtc,
            snapshot.Responses,
            active);
        if (plan.ResponseToAppend is not null
            && !HumanInputResponseContractValidator.ValidateArtifact(request, plan.ResponseToAppend).IsValid
            || plan.SelectionToAppend is not null
                && !HumanInputResponseContractValidator.ValidateSelection(
                    request,
                    plan.SelectionToAppend,
                    active.Append(plan.ResponseToAppend).Where(response => response is not null).Select(response => response!).ToArray()).IsValid)
        {
            return (HumanInputResponsePolicyEvaluator.Failure(
                head,
                command.TargetResponses,
                HumanInputResponseLifecycleMutationStatus.Ambiguous,
                HumanInputResponseOperationOutcome.Conflict,
                HumanInputResponseOperationFailureCode.OptimisticStateConflict,
                canPersist: false), request, actorRoleId);
        }
        return (plan, request, actorRoleId);
    }

    private HumanInputResponseOperationEvidence? BuildEvidence(
        HumanInputResponseLifecycleCommand command,
        HumanInputResponseLifecycleMutationPlan plan,
        HumanInputRequest? observedRequest,
        AuthorityActorId actorId,
        string? actorRoleId,
        string authenticationEvidenceHash,
        DateTimeOffset recordedAtUtc)
    {
        HumanInputResponseReference? submitted = null;
        if (plan.ResponseToAppend is not null
            && (observedRequest is null
                || !HumanInputResponseReference.TryCreate(observedRequest, plan.ResponseToAppend, out submitted, out _)
                || submitted is null))
        {
            return null;
        }
        var selection = plan.SelectionToAppend is null
            ? null
            : HumanInputResponseSelectionReference.Create(plan.SelectionToAppend);
        var eligibilityHash = HumanInputResponseEligibilityEvidenceHash.Compute(
            _workspaceId,
            command.OperationId,
            command.CommandHash,
            command.ExpectedRequest,
            actorId,
            actorRoleId,
            authenticationEvidenceHash,
            recordedAtUtc);
        var candidate = new HumanInputResponseOperationEvidence(
            HumanInputResponseContractLimits.CurrentSchemaVersion,
            command.OperationId,
            command.CommandHash,
            command.Kind,
            plan.Outcome,
            plan.FailureCode,
            command.ExpectedRequest,
            command.ExpectedBinding,
            observedRequest?.Binding,
            command.ExpectedLifecycleVersion,
            command.ExpectedLifecycleStatus,
            plan.PreviousHead,
            plan.ResultHead,
            submitted,
            plan.TargetResponses,
            selection,
            actorId,
            actorRoleId,
            authenticationEvidenceHash,
            eligibilityHash,
            recordedAtUtc);
        return HumanInputResponseOperationEvidenceSnapshot.TryCapture(candidate, out var evidence, out _)
            ? evidence
            : null;
    }

    private async Task<(bool Retry, HumanInputResponseLifecycleMutationResult? Result)> MapCommitAsync(
        HumanInputResponseLifecycleStoreCommitResult? commit,
        HumanInputResponseLifecycleStoreMutation mutation,
        HumanInputResponseLifecycleMutationPlan plan,
        HumanInputResponseLifecycleCommand command,
        AuthorityActorId actorId)
    {
        if (!TryCaptureCommit(commit, mutation, out var exactCommit) || exactCommit is null)
        {
            return (false, await RecoverAfterIntentAsync(command, actorId).ConfigureAwait(false));
        }
        if (exactCommit.Status is HumanInputResponseLifecycleStoreCommitStatus.Committed
            or HumanInputResponseLifecycleStoreCommitStatus.Replayed)
        {
            if (exactCommit.StoredOperation is null
                || !exactCommit.StoredOperation.Evidence.ActorId.Equals(actorId)
                || !TryBuildProvedResult(
                    exactCommit.Status == HumanInputResponseLifecycleStoreCommitStatus.Replayed
                        ? HumanInputResponseLifecycleMutationStatus.Replayed
                        : plan.Status,
                    command,
                    exactCommit.StoredOperation.Evidence,
                    exactCommit.Snapshot,
                    out var result))
            {
                return (false, await RecoverAfterIntentAsync(command, actorId).ConfigureAwait(false));
            }
            return (false, result);
        }
        if (exactCommit.Status == HumanInputResponseLifecycleStoreCommitStatus.StoreConflict
            && exactCommit.StoreGeneration > mutation.ExpectedStoreGeneration
            && exactCommit.StoredOperation is null)
        {
            return (true, null);
        }
        if (exactCommit.Status == HumanInputResponseLifecycleStoreCommitStatus.OperationConflict)
        {
            return (false, Result(HumanInputResponseLifecycleMutationStatus.Conflict, command.OperationId, command.CommandHash));
        }
        if (exactCommit.Status == HumanInputResponseLifecycleStoreCommitStatus.LimitExceeded)
        {
            return (false, Result(HumanInputResponseLifecycleMutationStatus.LimitExceeded, command.OperationId, command.CommandHash));
        }
        if (exactCommit.Status == HumanInputResponseLifecycleStoreCommitStatus.Unavailable)
        {
            return (false, Result(HumanInputResponseLifecycleMutationStatus.Unavailable, command.OperationId, command.CommandHash));
        }
        return (false, await RecoverAfterIntentAsync(command, actorId).ConfigureAwait(false));
    }

    private async Task<HumanInputResponseLifecycleMutationResult> RecoverAfterIntentAsync(
        HumanInputResponseLifecycleCommand command,
        AuthorityActorId authenticatedActor)
    {
        HumanInputResponseLifecycleStoreReadResult recovered;
        try
        {
            recovered = await ObserveAsync(command, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Result(HumanInputResponseLifecycleMutationStatus.Ambiguous, command.OperationId, command.CommandHash);
        }
        if (recovered.ExistingOperation is { } existing
            && existing.Evidence.ActorId.Equals(authenticatedActor)
            && TryBuildProvedResult(
                HumanInputResponseLifecycleMutationStatus.Replayed,
                command,
                existing.Evidence,
                recovered.Snapshot,
                out var result))
        {
            return result!;
        }
        return Result(
            HumanInputResponseLifecycleMutationStatus.Ambiguous,
            command.OperationId,
            command.CommandHash,
            projection: ProjectWithinScope(recovered.Snapshot, command));
    }

    private bool TryCaptureCommit(
        HumanInputResponseLifecycleStoreCommitResult? commit,
        HumanInputResponseLifecycleStoreMutation mutation,
        out HumanInputResponseLifecycleStoreCommitResult? captured)
    {
        captured = null;
        try
        {
            if (commit is null
                || !Enum.IsDefined(commit.Status)
                || commit.Status == HumanInputResponseLifecycleStoreCommitStatus.Unknown
                || commit.StoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore)
            {
                return false;
            }

            HumanInputResponseLifecycleStoredOperation? stored = null;
            if (commit.StoredOperation is not null
                && (!TryCaptureStoredOperation(commit.StoredOperation, mutation.Operation.OperationId, out stored) || stored is null))
            {
                return false;
            }
            HumanInputResponseLifecycleStoreSnapshot? snapshot = null;
            if (commit.Snapshot is not null
                && (!HumanInputResponseLifecycleStoreSnapshotGuard.TryCapture(commit.Snapshot, mutation.Operation.Request.RequestId, out snapshot)
                    || snapshot is null
                    || !Equals(snapshot.ResponseRequest, snapshot.Request.Head.CurrentRequest)
                    || !WorkspaceMatches(snapshot)))
            {
                return false;
            }

            if (commit.Status is HumanInputResponseLifecycleStoreCommitStatus.Committed
                or HumanInputResponseLifecycleStoreCommitStatus.Replayed)
            {
                if (stored is null
                    || !Equals(stored.Evidence, mutation.Operation)
                    || commit.StoreGeneration < mutation.ExpectedStoreGeneration + 1
                    || commit.Status == HumanInputResponseLifecycleStoreCommitStatus.Committed
                        && commit.StoreGeneration != mutation.ExpectedStoreGeneration + 1
                    || !StoredEvidenceFitsSnapshot(stored.Evidence, snapshot)
                    || !MutationArtifactsMatchSnapshot(mutation, snapshot))
                {
                    return false;
                }
            }
            captured = commit with { StoredOperation = stored, Snapshot = snapshot };
            return true;
        }
        catch (Exception)
        {
            captured = null;
            return false;
        }
    }

    private static bool MutationArtifactsMatchSnapshot(
        HumanInputResponseLifecycleStoreMutation mutation,
        HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return mutation.Operation.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound
                && mutation.ResponseToAppend is null
                && mutation.SelectionToAppend is null
                && mutation.RequestHeadToWrite is null;
        }
        if (mutation.ResponseToAppend is not null
            && !snapshot.Responses.Any(response => Equals(response, mutation.ResponseToAppend)))
        {
            return false;
        }
        if (mutation.SelectionToAppend is not null)
        {
            return Equals(snapshot.Selection, mutation.SelectionToAppend)
                && Equals(snapshot.Request.Head, mutation.RequestHeadToWrite)
                && Equals(snapshot.Request.AnswerOperation, mutation.Operation);
        }
        return mutation.RequestHeadToWrite is null;
    }

    private static bool TryBuildProvedResult(
        HumanInputResponseLifecycleMutationStatus status,
        HumanInputResponseLifecycleCommand command,
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot,
        out HumanInputResponseLifecycleMutationResult? result)
    {
        result = null;
        if (!OperationMatchesCommand(evidence, command)
            || !HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out var captured, out _)
            || captured is null
            || !StoredEvidenceFitsSnapshot(captured, snapshot))
        {
            return false;
        }
        result = Result(
            status,
            command.OperationId,
            command.CommandHash,
            Proof(captured),
            ProjectWithinScope(snapshot, command));
        return true;
    }

    private static bool StoredEvidenceFitsSnapshot(
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleStoreSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return evidence.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound;
        }
        if (!string.Equals(snapshot.Request.Head.RequestId, evidence.Request.RequestId, StringComparison.Ordinal))
        {
            return false;
        }
        var expectedIsRetained = snapshot.Request.RequestVersions.Any(evidence.Request.Matches);
        if (expectedIsRetained)
        {
            return Equals(snapshot.ResponseRequest, evidence.Request)
                && HumanInputResponseLifecycleStoreSnapshotGuard.EvidenceMatchesSnapshot(evidence, snapshot);
        }
        return Equals(snapshot.ResponseRequest, snapshot.Request.Head.CurrentRequest)
            && HumanInputResponseLifecycleStoreSnapshotGuard.EvidenceMatchesAbsentExpectedLifecycle(evidence, snapshot.Request);
    }

    private static bool TryCaptureStoredOperation(
        HumanInputResponseLifecycleStoredOperation source,
        string expectedOperationId,
        out HumanInputResponseLifecycleStoredOperation? captured)
    {
        captured = null;
        try
        {
            if (!HumanInputIdentifier.IsValid(source.RequestId)
                || !HumanInputResponseOperationEvidenceSnapshot.TryCapture(source.Evidence, out var evidence, out _)
                || evidence is null
                || !string.Equals(evidence.OperationId, expectedOperationId, StringComparison.Ordinal)
                || !string.Equals(evidence.Request.RequestId, source.RequestId, StringComparison.Ordinal))
            {
                return false;
            }
            captured = new HumanInputResponseLifecycleStoredOperation(source.RequestId, evidence);
            return true;
        }
        catch (Exception)
        {
            captured = null;
            return false;
        }
    }

    private static bool OperationMatchesCommand(
        HumanInputResponseOperationEvidence evidence,
        HumanInputResponseLifecycleCommand command)
        => string.Equals(evidence.OperationId, command.OperationId, StringComparison.Ordinal)
            && FixedEquals(evidence.CommandHash, command.CommandHash)
            && evidence.Kind == command.Kind
            && Equals(evidence.Request, command.ExpectedRequest)
            && Equals(evidence.ExpectedBinding, command.ExpectedBinding)
            && evidence.ExpectedLifecycleVersion == command.ExpectedLifecycleVersion
            && evidence.ExpectedLifecycleStatus == command.ExpectedLifecycleStatus
            && evidence.TargetResponses.SequenceEqual(command.TargetResponses);

    private static bool SubmittedValueIsValid(
        HumanInputRequest request,
        HumanInputResponseLifecycleCommand command,
        DateTimeOffset recordedAtUtc)
    {
        var representative = request.EligibleRespondents[0];
        var response = new HumanInputResponse(
            request.RequestId,
            request.RequestVersionId,
            request.Binding,
            representative.RespondentId,
            representative.RespondentRoleId,
            recordedAtUtc,
            command.Value!,
            command.Explanation);
        return HumanInputValidator.ValidateResponse(request, response).Kind == HumanInputResponseOutcomeKind.Valid;
    }

    private static string? EligibleRole(HumanInputRequest request, AuthorityActorId actorId)
        => request.EligibleRespondents.SingleOrDefault(respondent => string.Equals(respondent.RespondentId, actorId.Value, StringComparison.Ordinal))?.RespondentRoleId;

    private static HumanInputRequest? FindResponseRequest(HumanInputResponseLifecycleStoreSnapshot snapshot)
        => FindRequest(snapshot, snapshot.ResponseRequest);

    private static HumanInputRequest? FindRequest(
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        HumanInputRequestReference reference)
    {
        try
        {
            return snapshot.Request.RequestVersions.SingleOrDefault(reference.Matches);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static HumanInputResponseLifecycleProjection? ProjectWithinScope(
        HumanInputResponseLifecycleStoreSnapshot? snapshot,
        HumanInputResponseLifecycleCommand command)
    {
        var request = snapshot is null ? null : FindResponseRequest(snapshot);
        return request is not null
            && Equals(snapshot!.ResponseRequest, command.ExpectedRequest)
            && Equals(request.Binding, command.ExpectedBinding)
            ? HumanInputResponseLifecycleStoreSnapshotGuard.Project(snapshot)
            : null;
    }

    private static HumanInputResponseLifecycleOperationProof Proof(HumanInputResponseOperationEvidence evidence)
        => new(
            HumanInputResponseContractLimits.CurrentSchemaVersion,
            evidence.OperationId,
            evidence.CommandHash,
            evidence.Kind,
            evidence.Outcome,
            evidence.FailureCode,
            evidence.Request.RequestId,
            evidence.Request.RequestVersionId,
            evidence.PreviousHead?.LifecycleVersion,
            evidence.ResultHead?.LifecycleVersion,
            evidence.Selection,
            evidence.RecordedAtUtc);

    private static HumanInputResponseLifecycleMutationResult Result(
        HumanInputResponseLifecycleMutationStatus status,
        string operationId,
        string commandHash,
        HumanInputResponseLifecycleOperationProof? operation = null,
        HumanInputResponseLifecycleProjection? projection = null,
        IReadOnlyList<HumanInputResponseLifecycleMutationValidationError>? validationErrors = null)
        => new(status, operationId, commandHash, operation, projection, validationErrors ?? []);

    private static bool TryCaptureCommand(
        HumanInputResponseLifecycleCommand? source,
        out HumanInputResponseLifecycleCommand? captured)
    {
        captured = null;
        try
        {
            if (source is null || source.TargetResponses.IsDefault)
            {
                return false;
            }
            var targets = source.TargetResponses.Select(target => target with { Request = target.Request with { } }).ToImmutableArray();
            ImmutableArray<HumanInputStructuredFieldValue>? structured = source.Value?.StructuredFields is not { } fields
                ? null
                : fields.Select(field => field is null ? null! : field with { }).ToImmutableArray();
            var value = source.Value is null
                ? null
                : source.Value with
                {
                    StructuredFields = structured,
                    Reference = source.Value.Reference is null ? null : source.Value.Reference with { }
                };
            captured = source with
            {
                ExpectedRequest = source.ExpectedRequest with { },
                ExpectedBinding = source.ExpectedBinding with { },
                Value = value,
                TargetResponses = targets
            };
            return true;
        }
        catch (Exception)
        {
            captured = null;
            return false;
        }
    }

    private DateTimeOffset UtcNow()
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            return now != default && now.Offset == TimeSpan.Zero ? now : default;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static HumanInputResponseActorAuthentication UnavailableAuthentication(
        HumanInputResponseLifecycleCommand command,
        DateTimeOffset evaluatedAtUtc)
        => new(
            HumanInputResponseActorAuthenticationStatus.Unavailable,
            command.OperationId,
            command.CommandHash,
            string.Empty,
            evaluatedAtUtc,
            null,
            string.Empty);

    private static HumanInputResponseLifecycleStoreReadResult UnavailableRead()
        => new(HumanInputResponseLifecycleStoreReadStatus.Unavailable, 0, null, null);

    private static HumanInputResponseLifecycleStoreReadResult AmbiguousRead()
        => new(HumanInputResponseLifecycleStoreReadStatus.Ambiguous, 0, null, null);

    private bool WorkspaceMatches(HumanInputResponseLifecycleStoreSnapshot snapshot)
    {
        var current = snapshot.Request.RequestVersions.SingleOrDefault(snapshot.Request.Head.CurrentRequest.Matches);
        var response = FindResponseRequest(snapshot);
        return current is not null
            && response is not null
            && snapshot.Request.RequestVersions.All(request => string.Equals(request.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal))
            && string.Equals(current.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            && string.Equals(response.Binding.WorkspaceId, _workspaceId, StringComparison.Ordinal);
    }

    private static bool HasDurableProof(HumanInputResponseLifecycleMutationResult? result)
        => result?.Operation is not null;

    private static string SafeOperationId(HumanInputResponseLifecycleCommand? command)
        => HumanInputIdentifier.IsValid(command?.OperationId) ? command!.OperationId : string.Empty;

    private static string SafeCommandHash(HumanInputResponseLifecycleCommand? command)
        => IsSha256(command?.CommandHash) ? command!.CommandHash : string.Empty;

    private static bool IsSha256(string? value)
        => value is { Length: HumanInputLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedEquals(string? left, string? right)
    {
        if (!IsSha256(left) || !IsSha256(right))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left!), Encoding.ASCII.GetBytes(right!));
    }
}
