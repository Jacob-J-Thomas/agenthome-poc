using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Executes authenticated immutable revision lifecycle operations under one shared workspace authority fence.</summary>
public sealed class GovernedLoopRevisionLifecycleService : IGovernedLoopRevisionLifecycleService
{
    private const int MaximumCommitAttempts = 3;
    private readonly IGovernedLoopRevisionLifecycleStore _store;
    private readonly IGovernedLoopRevisionActorAuthorizer _authorizer;
    private readonly IGovernedLoopRevisionPublishValidator _publishValidator;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a lifecycle service over server-owned authority, validation, and atomic persistence ports.</summary>
    /// <param name="store">The atomic immutable revision lifecycle store.</param>
    /// <param name="authorizer">The current server-owned actor authorizer.</param>
    /// <param name="publishValidator">The current server-owned publication validator.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <param name="timeProvider">The trusted clock, or the system clock when omitted.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required server-owned port or authority transaction is <see langword="null"/>.</exception>
    public GovernedLoopRevisionLifecycleService(
        IGovernedLoopRevisionLifecycleStore store,
        IGovernedLoopRevisionActorAuthorizer authorizer,
        IGovernedLoopRevisionPublishValidator publishValidator,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _publishValidator = publishValidator ?? throw new ArgumentNullException(nameof(publishValidator));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopRevisionLifecycleMutationResult> MutateAsync(
        GovernedLoopRevisionLifecycleRequest? request,
        CancellationToken cancellationToken = default)
    {
        GovernedLoopRevisionLifecycleMutationResult? completedResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completedResult = await MutateUnderFenceAsync(request, transactionToken).ConfigureAwait(false);
                    return completedResult;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completedResult is null)
        {
            throw;
        }
        catch (Exception)
        {
            if (HasExactDurableProof(completedResult))
            {
                return completedResult!;
            }

            return Result(
                completedResult is null
                    ? GovernedLoopRevisionLifecycleMutationStatus.Unavailable
                    : GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                SafeOperationId(request),
                completedResult?.RequestHash ?? string.Empty);
        }
    }

    private async Task<GovernedLoopRevisionLifecycleMutationResult> MutateUnderFenceAsync(
        GovernedLoopRevisionLifecycleRequest? request,
        CancellationToken cancellationToken)
    {
        var validationErrors = GovernedLoopRevisionLifecycleRequestValidator.Validate(request);
        if (validationErrors.Count > 0)
        {
            return Result(
                GovernedLoopRevisionLifecycleMutationStatus.Invalid,
                SafeOperationId(request),
                string.Empty,
                validationErrors: validationErrors);
        }

        string requestHash;
        try
        {
            requestHash = GovernedLoopRevisionLifecycleRequestHash.Compute(request!);
        }
        catch (ArgumentException)
        {
            return Result(
                GovernedLoopRevisionLifecycleMutationStatus.Invalid,
                SafeOperationId(request),
                string.Empty,
                validationErrors: Array.AsReadOnly(new[]
                {
                    new GovernedLoopRevisionLifecycleValidationError(
                        GovernedLoopRevisionLifecycleValidationErrorCode.InvalidIdentifier,
                        "$"),
                }));
        }

        var exactRequest = request!;
        var initialRead = await ReadAsync(exactRequest, requestHash, cancellationToken).ConfigureAwait(false);
        var exactReplay = ResolveExactReplay(initialRead, exactRequest, requestHash);
        if (exactReplay is not null)
        {
            return exactReplay;
        }

        if (initialRead.Status == ReadStatus.Ambiguous)
        {
            return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactRequest.OperationId, requestHash);
        }

        if (initialRead.Status == ReadStatus.Unavailable)
        {
            return Result(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, exactRequest.OperationId, requestHash);
        }

        var authorization = await AuthorizeAsync(exactRequest, requestHash, cancellationToken).ConfigureAwait(false);
        if (authorization.Status != GovernedLoopRevisionActorAuthorizationStatus.Authorized)
        {
            return Result(
                authorization.Status == GovernedLoopRevisionActorAuthorizationStatus.Denied
                    ? GovernedLoopRevisionLifecycleMutationStatus.Unauthorized
                    : GovernedLoopRevisionLifecycleMutationStatus.Unavailable,
                exactRequest.OperationId,
                requestHash);
        }

        var readResult = ResolveReadOutcome(initialRead, exactRequest, requestHash);
        if (readResult is not null)
        {
            return readResult;
        }

        var initialPlan = Plan(exactRequest, initialRead.Snapshot, PreflightTimestamp(initialRead.Snapshot));
        if (initialPlan.Status == PlanStatus.InvalidStoreState)
        {
            return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactRequest.OperationId, requestHash);
        }

        for (var attempt = 0; attempt < MaximumCommitAttempts; attempt++)
        {
            var currentRead = await ReadAsync(exactRequest, requestHash, cancellationToken).ConfigureAwait(false);
            readResult = ResolveReadOutcome(currentRead, exactRequest, requestHash);
            if (readResult is not null)
            {
                return readResult;
            }

            var preflightPlan = Plan(exactRequest, currentRead.Snapshot, PreflightTimestamp(currentRead.Snapshot));
            if (preflightPlan.Status == PlanStatus.InvalidStoreState)
            {
                return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactRequest.OperationId, requestHash);
            }

            if (!preflightPlan.CanPersist)
            {
                return Result(
                    GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded,
                    exactRequest.OperationId,
                    requestHash,
                    head: currentRead.Snapshot?.Head);
            }

            authorization = await AuthorizeAsync(exactRequest, requestHash, cancellationToken).ConfigureAwait(false);
            if (authorization.Status != GovernedLoopRevisionActorAuthorizationStatus.Authorized)
            {
                return Result(
                    authorization.Status == GovernedLoopRevisionActorAuthorizationStatus.Denied
                        ? GovernedLoopRevisionLifecycleMutationStatus.Unauthorized
                        : GovernedLoopRevisionLifecycleMutationStatus.Unavailable,
                    exactRequest.OperationId,
                    requestHash,
                    head: currentRead.Snapshot?.Head);
            }

            var recordedAtUtc = UtcNow();
            if (recordedAtUtc == default)
            {
                return Result(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, exactRequest.OperationId, requestHash);
            }

            if (currentRead.Snapshot?.Head is { } currentHead
                && recordedAtUtc < currentHead.UpdatedAtUtc)
            {
                return Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Unavailable,
                    exactRequest.OperationId,
                    requestHash,
                    head: currentHead);
            }

            var plan = Plan(exactRequest, currentRead.Snapshot, recordedAtUtc);
            if (plan.Status == PlanStatus.InvalidStoreState)
            {
                return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactRequest.OperationId, requestHash);
            }

            if (!plan.CanPersist)
            {
                return Result(
                    GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded,
                    exactRequest.OperationId,
                    requestHash,
                    head: currentRead.Snapshot?.Head);
            }

            var publicationValidation = await ValidatePublicationAsync(plan, requestHash, recordedAtUtc, cancellationToken).ConfigureAwait(false);
            if (publicationValidation.Status == PublicationCheckStatus.Rejected)
            {
                return Result(
                    GovernedLoopRevisionLifecycleMutationStatus.PublicationRejected,
                    exactRequest.OperationId,
                    requestHash,
                    head: currentRead.Snapshot?.Head);
            }

            if (publicationValidation.Status == PublicationCheckStatus.Unavailable)
            {
                return Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Unavailable,
                    exactRequest.OperationId,
                    requestHash,
                    head: currentRead.Snapshot?.Head);
            }

            var mutation = BuildMutation(
                exactRequest,
                requestHash,
                authorization.EvidenceHash,
                publicationValidation.EvidenceHash,
                currentRead.StoreGeneration,
                plan,
                recordedAtUtc);
            if (mutation is null)
            {
                return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactRequest.OperationId, requestHash);
            }

            GovernedLoopRevisionStoreCommitResult commit;
            try
            {
                commit = await _store.CommitAsync(mutation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, exactRequest.OperationId, requestHash);
            }

            var mapped = MapCommit(commit, mutation, requestHash);
            if (mapped.RetryStoreConflict)
            {
                continue;
            }

            return mapped.Result!;
        }

        return Result(GovernedLoopRevisionLifecycleMutationStatus.Conflict, exactRequest.OperationId, requestHash);
    }

    private async Task<AuthorizationCheck> AuthorizeAsync(
        GovernedLoopRevisionLifecycleRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var evaluatedAtUtc = UtcNow();
        if (evaluatedAtUtc == default)
        {
            return AuthorizationCheck.Unavailable;
        }

        GovernedLoopRevisionActorAuthorization decision;
        try
        {
            decision = await _authorizer.AuthorizeAsync(
                new GovernedLoopRevisionActorAuthorizationRequest(request, requestHash, evaluatedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AuthorizationCheck.Unavailable;
        }

        if (decision is null
            || !Enum.IsDefined(decision.Status)
            || decision.Status == GovernedLoopRevisionActorAuthorizationStatus.Unknown
            || !string.Equals(decision.OperationId, request.OperationId, StringComparison.Ordinal)
            || !string.Equals(decision.RequestHash, requestHash, StringComparison.Ordinal)
            || !Equals(decision.ActorId, request.ActorId)
            || !IsSha256(decision.AuthorityEvidenceHash))
        {
            return AuthorizationCheck.Unavailable;
        }

        return new AuthorizationCheck(decision.Status, decision.AuthorityEvidenceHash);
    }

    private async Task<ReadObservation> ReadAsync(
        GovernedLoopRevisionLifecycleRequest request,
        string requestHash,
        CancellationToken cancellationToken)
    {
        GovernedLoopRevisionStoreReadResult read;
        try
        {
            read = await _store.ReadForMutationAsync(
                request.GraphId,
                request.OperationId,
                requestHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ReadObservation.Unavailable;
        }

        if (read is null
            || read.StoreGeneration < 0
            || read.ExistingOperation is not null && read.StoreGeneration == 0
            || !TryValidateStoredOperation(read.ExistingOperation, request.OperationId))
        {
            return ReadObservation.Ambiguous;
        }

        switch (read.Status)
        {
            case GovernedLoopRevisionStoreReadStatus.Ready:
                if (!GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                    read.Snapshot,
                    request.GraphId,
                    read.StoreGeneration,
                    out var snapshot))
                {
                    return ReadObservation.Ambiguous;
                }

                if (read.ExistingOperation is { } existing
                    && (string.Equals(existing.GraphId, request.GraphId, StringComparison.Ordinal)
                        ? !snapshot!.Operations.Any(operation => Equals(operation, existing.Evidence))
                        : read.StoreGeneration <= snapshot!.Operations.Count))
                {
                    return ReadObservation.Ambiguous;
                }

                return new ReadObservation(ReadStatus.Ready, read.StoreGeneration, snapshot, read.ExistingOperation);
            case GovernedLoopRevisionStoreReadStatus.NotFound when read.Snapshot is null:
                if (read.ExistingOperation is { } missingExisting
                    && string.Equals(missingExisting.GraphId, request.GraphId, StringComparison.Ordinal)
                    && !IsAbsentGraphLifecycleNotFound(missingExisting.Evidence))
                {
                    return ReadObservation.Ambiguous;
                }

                return new ReadObservation(ReadStatus.NotFound, read.StoreGeneration, null, read.ExistingOperation);
            case GovernedLoopRevisionStoreReadStatus.Unavailable when read.StoreGeneration == 0 && read.Snapshot is null && read.ExistingOperation is null:
                return ReadObservation.Unavailable;
            case GovernedLoopRevisionStoreReadStatus.Ambiguous when read.StoreGeneration == 0 && read.Snapshot is null:
                return ReadObservation.Ambiguous;
            default:
                return ReadObservation.Ambiguous;
        }
    }

    private static GovernedLoopRevisionLifecycleMutationResult? ResolveReadOutcome(
        ReadObservation read,
        GovernedLoopRevisionLifecycleRequest request,
        string requestHash)
    {
        if (read.Status == ReadStatus.Unavailable)
        {
            return Result(GovernedLoopRevisionLifecycleMutationStatus.Unavailable, request.OperationId, requestHash);
        }

        if (read.Status == ReadStatus.Ambiguous)
        {
            return Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, request.OperationId, requestHash);
        }

        if (read.ExistingOperation is not { } existing)
        {
            return null;
        }

        if (string.Equals(existing.GraphId, request.GraphId, StringComparison.Ordinal)
            && string.Equals(existing.Evidence.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return ResolveExactReplay(read, request, requestHash)
                ?? Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, request.OperationId, requestHash);
        }

        return Result(
            GovernedLoopRevisionLifecycleMutationStatus.Conflict,
            request.OperationId,
            requestHash,
            null,
            read.Snapshot?.Head);
    }

    private static GovernedLoopRevisionLifecycleMutationResult? ResolveExactReplay(
        ReadObservation read,
        GovernedLoopRevisionLifecycleRequest request,
        string requestHash)
    {
        if (read.Status is not (ReadStatus.Ready or ReadStatus.NotFound)
            || !OperationMatchesRequest(read.ExistingOperation, request, requestHash))
        {
            return null;
        }

        var evidence = read.ExistingOperation!.Evidence;
        if (read.Status == ReadStatus.NotFound && !IsAbsentGraphLifecycleNotFound(evidence))
        {
            return null;
        }

        return Result(
            GovernedLoopRevisionLifecycleMutationStatus.Replayed,
            request.OperationId,
            requestHash,
            evidence,
            read.Snapshot?.Head);
    }

    private async Task<PublicationCheck> ValidatePublicationAsync(
        MutationPlan plan,
        string requestHash,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (plan.Outcome != GovernedLoopRevisionOperationOutcome.Committed
            || plan.PublicationArtifact is null)
        {
            return PublicationCheck.NotRequired;
        }

        GovernedLoopRevisionPublishValidation validation;
        try
        {
            validation = await _publishValidator.ValidateAsync(
                new GovernedLoopRevisionPublishValidationRequest(
                    plan.OperationId,
                    requestHash,
                    plan.Kind,
                    plan.PublicationArtifact,
                    evaluatedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return PublicationCheck.Unavailable;
        }

        if (validation is null
            || !Enum.IsDefined(validation.Status)
            || validation.Status == GovernedLoopRevisionPublishValidationStatus.Unknown
            || !string.Equals(validation.OperationId, plan.OperationId, StringComparison.Ordinal)
            || !string.Equals(validation.RequestHash, requestHash, StringComparison.Ordinal)
            || !GovernedLoopRevisionStoreSnapshotGuard.SameRevision(validation.Revision, plan.PublicationArtifact.Revision)
            || !IsSha256(validation.ValidationEvidenceHash))
        {
            return PublicationCheck.Unavailable;
        }

        return validation.Status switch
        {
            GovernedLoopRevisionPublishValidationStatus.Valid => new PublicationCheck(PublicationCheckStatus.Valid, validation.ValidationEvidenceHash),
            GovernedLoopRevisionPublishValidationStatus.Invalid => PublicationCheck.Rejected,
            _ => PublicationCheck.Unavailable,
        };
    }

    private static GovernedLoopRevisionStoreMutation? BuildMutation(
        GovernedLoopRevisionLifecycleRequest request,
        string requestHash,
        string authorityEvidenceHash,
        string? publicationValidationEvidenceHash,
        long expectedStoreGeneration,
        MutationPlan plan,
        DateTimeOffset recordedAtUtc)
    {
        var resultHead = plan.NextHead is null
            ? plan.PreviousHead
            : plan.NextHead with
            {
                PublishedRevision = plan.PublicationArtifact is null
                    ? plan.NextHead.PublishedRevision
                    : new GovernedLoopRevisionPublicationPin(
                        GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
                        plan.PublicationArtifact.Revision,
                        request.OperationId,
                        publicationValidationEvidenceHash!),
            };

        var artifact = plan.ArtifactToAppend is null
            ? null
            : plan.ArtifactToAppend with
            {
                CreatedAtUtc = recordedAtUtc,
            };

        if (resultHead is not null && plan.NextHead is not null)
        {
            resultHead = resultHead with { UpdatedAtUtc = recordedAtUtc };
        }

        var evidence = new GovernedLoopRevisionOperationEvidence(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.OperationId,
            request.ActorId.Value,
            requestHash,
            request.Kind,
            plan.Outcome,
            plan.FailureCode,
            plan.PreviousHead,
            resultHead,
            request.CandidateRevision,
            request.TargetRevision,
            request.RollbackSourcePublication,
            authorityEvidenceHash,
            plan.PublicationArtifact is null || plan.Outcome != GovernedLoopRevisionOperationOutcome.Committed
                ? null
                : publicationValidationEvidenceHash,
            recordedAtUtc);

        if (!GovernedLoopRevisionContractValidator.Validate(evidence).IsValid
            || artifact is not null && !GovernedLoopRevisionContractValidator.Validate(artifact).IsValid
            || plan.NextHead is not null && !GovernedLoopRevisionContractValidator.Validate(resultHead).IsValid
            || plan.PreviousHead is not null && plan.NextHead is not null
                && !GovernedLoopRevisionContractValidator.ValidateTransition(plan.PreviousHead, resultHead).IsValid)
        {
            return null;
        }

        return new GovernedLoopRevisionStoreMutation(
            request.GraphId,
            expectedStoreGeneration,
            evidence,
            artifact,
            plan.NextHead is null ? null : resultHead);
    }

    private static MutationPlan Plan(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionStoreSnapshot? snapshot,
        DateTimeOffset recordedAtUtc)
    {
        if (snapshot is null)
        {
            return request.Kind == GovernedLoopRevisionOperationKind.CreateDraft
                ? PlanInitialDraft(request, recordedAtUtc)
                : MutationPlan.Failure(request, null, GovernedLoopRevisionOperationOutcome.NotFound, GovernedLoopRevisionOperationFailureCode.LifecycleNotFound);
        }

        var head = snapshot.Head;
        if (snapshot.Operations.Count >= GovernedLoopRevisionContractLimits.MaxOperationsPerGraph)
        {
            return MutationPlan.UnpersistableLimit(request, head, GovernedLoopRevisionOperationFailureCode.EvidenceLimitExceeded);
        }

        if (snapshot.Operations.Count == GovernedLoopRevisionContractLimits.MaxOperationsPerGraph - 1)
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.LimitExceeded, GovernedLoopRevisionOperationFailureCode.EvidenceLimitExceeded);
        }

        if (head.Status == GovernedLoopRevisionLifecycleStatus.Archived)
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.LifecycleArchived);
        }

        if (!ExpectedHeadMatches(request, head))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict);
        }

        if (head.LifecycleVersion >= GovernedLoopRevisionContractLimits.MaxLifecycleVersion)
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.LimitExceeded, GovernedLoopRevisionOperationFailureCode.LifecycleVersionLimitExceeded);
        }

        if (request.CandidateRevision is not null
            && snapshot.Artifacts.Any(artifact => string.Equals(
                artifact.Revision.RevisionId,
                request.CandidateRevision.RevisionId,
                StringComparison.Ordinal)))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict);
        }

        if (request.CandidateRevision is not null
            && snapshot.Artifacts.Count >= GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph)
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.LimitExceeded, GovernedLoopRevisionOperationFailureCode.ArtifactLimitExceeded);
        }

        return request.Kind switch
        {
            GovernedLoopRevisionOperationKind.CreateDraft => MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict),
            GovernedLoopRevisionOperationKind.ReplaceDraft => PlanDraftReplacement(request, head, recordedAtUtc),
            GovernedLoopRevisionOperationKind.Publish => PlanPublication(request, snapshot, recordedAtUtc),
            GovernedLoopRevisionOperationKind.Disable => PlanDisablement(request, head, recordedAtUtc),
            GovernedLoopRevisionOperationKind.Archive => PlanArchival(request, head, recordedAtUtc),
            GovernedLoopRevisionOperationKind.Rollback => PlanRollback(request, snapshot, recordedAtUtc),
            _ => MutationPlan.Invalid,
        };
    }

    private static MutationPlan PlanInitialDraft(
        GovernedLoopRevisionLifecycleRequest request,
        DateTimeOffset recordedAtUtc)
    {
        var next = new GovernedLoopRevisionLifecycleHead(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.GraphId,
            1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            request.CandidateRevision,
            null,
            request.OperationId,
            recordedAtUtc);
        var artifact = new GovernedLoopRevisionArtifact(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.CandidateRevision!,
            null,
            null,
            request.OperationId,
            request.ActorId.Value,
            recordedAtUtc);
        return MutationPlan.Success(request, null, next, artifact, null);
    }

    private static MutationPlan PlanDraftReplacement(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead head,
        DateTimeOffset recordedAtUtc)
    {
        var expectedTarget = head.DraftRevision ?? head.PublishedRevision?.Revision;
        if (!GovernedLoopRevisionStoreSnapshotGuard.SameRevision(request.TargetRevision, expectedTarget))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.NotFound, GovernedLoopRevisionOperationFailureCode.RevisionNotFound);
        }

        var next = new GovernedLoopRevisionLifecycleHead(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.GraphId,
            head.LifecycleVersion + 1,
            head.Status,
            request.CandidateRevision,
            head.PublishedRevision,
            request.OperationId,
            recordedAtUtc);
        var artifact = new GovernedLoopRevisionArtifact(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.CandidateRevision!,
            request.TargetRevision,
            null,
            request.OperationId,
            request.ActorId.Value,
            recordedAtUtc);
        return MutationPlan.Success(request, head, next, artifact, null);
    }

    private static MutationPlan PlanPublication(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionStoreSnapshot snapshot,
        DateTimeOffset recordedAtUtc)
    {
        if (!GovernedLoopRevisionStoreSnapshotGuard.SameRevision(request.TargetRevision, snapshot.Head.DraftRevision))
        {
            return MutationPlan.Failure(request, snapshot.Head, GovernedLoopRevisionOperationOutcome.NotFound, GovernedLoopRevisionOperationFailureCode.RevisionNotFound);
        }

        var artifact = GovernedLoopRevisionStoreSnapshotGuard.FindArtifact(snapshot.Artifacts, request.TargetRevision!);
        if (artifact is null)
        {
            return MutationPlan.Failure(request, snapshot.Head, GovernedLoopRevisionOperationOutcome.NotFound, GovernedLoopRevisionOperationFailureCode.RevisionNotFound);
        }

        var next = new GovernedLoopRevisionLifecycleHead(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.GraphId,
            snapshot.Head.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            null,
            request.OperationId,
            recordedAtUtc);
        return MutationPlan.Success(request, snapshot.Head, next, null, artifact);
    }

    private static MutationPlan PlanDisablement(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead head,
        DateTimeOffset recordedAtUtc)
    {
        if (head.Status != GovernedLoopRevisionLifecycleStatus.Published
            || !GovernedLoopRevisionStoreSnapshotGuard.SameRevision(request.TargetRevision, head.PublishedRevision?.Revision))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict);
        }

        var next = head with
        {
            LifecycleVersion = head.LifecycleVersion + 1,
            Status = GovernedLoopRevisionLifecycleStatus.Disabled,
            LastOperationId = request.OperationId,
            UpdatedAtUtc = recordedAtUtc,
        };
        return MutationPlan.Success(request, head, next, null, null);
    }

    private static MutationPlan PlanArchival(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead head,
        DateTimeOffset recordedAtUtc)
    {
        if (head.Status is not (GovernedLoopRevisionLifecycleStatus.Published or GovernedLoopRevisionLifecycleStatus.Disabled)
            || !GovernedLoopRevisionStoreSnapshotGuard.SameRevision(request.TargetRevision, head.PublishedRevision?.Revision))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict);
        }

        var next = head with
        {
            LifecycleVersion = head.LifecycleVersion + 1,
            Status = GovernedLoopRevisionLifecycleStatus.Archived,
            DraftRevision = null,
            LastOperationId = request.OperationId,
            UpdatedAtUtc = recordedAtUtc,
        };
        return MutationPlan.Success(request, head, next, null, null);
    }

    private static MutationPlan PlanRollback(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionStoreSnapshot snapshot,
        DateTimeOffset recordedAtUtc)
    {
        var head = snapshot.Head;
        if (head.Status is not (GovernedLoopRevisionLifecycleStatus.Published or GovernedLoopRevisionLifecycleStatus.Disabled)
            || !GovernedLoopRevisionStoreSnapshotGuard.SameRevision(request.TargetRevision, head.DraftRevision ?? head.PublishedRevision?.Revision))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.Conflict, GovernedLoopRevisionOperationFailureCode.OptimisticStateConflict);
        }

        var source = request.RollbackSourcePublication!;
        var sourceArtifact = GovernedLoopRevisionStoreSnapshotGuard.FindArtifact(snapshot.Artifacts, source.Revision);
        if (sourceArtifact is null
            || !GovernedLoopRevisionStoreSnapshotGuard.HasPublicationProof(snapshot.Operations, source))
        {
            return MutationPlan.Failure(request, head, GovernedLoopRevisionOperationOutcome.NotFound, GovernedLoopRevisionOperationFailureCode.PublicationNotFound);
        }

        var artifact = new GovernedLoopRevisionArtifact(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.CandidateRevision!,
            request.TargetRevision,
            source,
            request.OperationId,
            request.ActorId.Value,
            recordedAtUtc);
        var next = new GovernedLoopRevisionLifecycleHead(
            GovernedLoopRevisionContractLimits.CurrentSchemaVersion,
            request.GraphId,
            head.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            null,
            request.OperationId,
            recordedAtUtc);
        return MutationPlan.Success(request, head, next, artifact, artifact);
    }

    private static CommitMapping MapCommit(
        GovernedLoopRevisionStoreCommitResult? commit,
        GovernedLoopRevisionStoreMutation mutation,
        string requestHash)
    {
        if (commit is null || commit.StoreGeneration < 0)
        {
            return CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
        }

        if (commit.Status == GovernedLoopRevisionStoreCommitStatus.StoreConflict)
        {
            var wellShaped = commit.Operation is null
                && commit.StoreGeneration > 0
                && commit.StoreGeneration > mutation.ExpectedStoreGeneration
                && (commit.Snapshot is null
                    || GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                        commit.Snapshot,
                        mutation.GraphId,
                        commit.StoreGeneration,
                        out _));
            return wellShaped
                ? CommitMapping.Retry
                : CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
        }

        if (commit.Status == GovernedLoopRevisionStoreCommitStatus.Unavailable)
        {
            var unavailableHead = TryCaptureCommitHead(commit.Snapshot, mutation.GraphId, commit.StoreGeneration);
            var wellShaped = commit.Operation is null
                && commit.StoreGeneration == mutation.ExpectedStoreGeneration
                && (mutation.Operation.PreviousHead is null
                    ? commit.Snapshot is null
                    : Equals(unavailableHead, mutation.Operation.PreviousHead));
            return wellShaped
                ? CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Unavailable,
                    mutation.Operation.OperationId,
                    requestHash,
                    head: unavailableHead))
                : CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
        }

        if (commit.Status == GovernedLoopRevisionStoreCommitStatus.Ambiguous)
        {
            var exactOperation = OperationMatchesRequest(commit.Operation, mutation.GraphId, mutation.Operation);
            GovernedLoopRevisionStoreSnapshot? ambiguousSnapshot = null;
            if (commit.Snapshot is not null
                && (!GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                    commit.Snapshot,
                    mutation.GraphId,
                    commit.StoreGeneration,
                    out ambiguousSnapshot)
                    || exactOperation
                        && !ambiguousSnapshot!.Operations.Any(operation => Equals(operation, commit.Operation!.Evidence))))
            {
                return CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                    mutation.Operation.OperationId,
                    requestHash));
            }

            return CommitMapping.Final(Result(
                GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                mutation.Operation.OperationId,
                requestHash,
                exactOperation ? commit.Operation!.Evidence : null,
                ambiguousSnapshot?.Head));
        }

        if (commit.Status == GovernedLoopRevisionStoreCommitStatus.OperationConflict)
        {
            GovernedLoopRevisionStoreSnapshot? conflictSnapshot = null;
            if (commit.Snapshot is not null
                && !GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                    commit.Snapshot,
                    mutation.GraphId,
                    commit.StoreGeneration,
                    out conflictSnapshot))
            {
                return CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                    mutation.Operation.OperationId,
                    requestHash));
            }

            if (commit.StoreGeneration <= mutation.ExpectedStoreGeneration
                || !ValidCommitOperation(commit.Operation)
                || !string.Equals(commit.Operation!.Evidence.OperationId, mutation.Operation.OperationId, StringComparison.Ordinal))
            {
                return CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                    mutation.Operation.OperationId,
                    requestHash));
            }

            var sameGraph = string.Equals(commit.Operation.GraphId, mutation.GraphId, StringComparison.Ordinal);
            var hasCausalProof = sameGraph
                ? conflictSnapshot is null
                    ? IsAbsentGraphLifecycleNotFound(commit.Operation.Evidence)
                    : conflictSnapshot.Operations.Any(operation => Equals(operation, commit.Operation.Evidence))
                : conflictSnapshot is null || commit.StoreGeneration > conflictSnapshot.Operations.Count;
            if (!hasCausalProof)
            {
                return CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                    mutation.Operation.OperationId,
                    requestHash));
            }

            if (OperationMatchesRequest(commit.Operation, mutation.GraphId, mutation.Operation))
            {
                return CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                    mutation.Operation.OperationId,
                    requestHash,
                    head: conflictSnapshot?.Head));
            }

            if (sameGraph && string.Equals(commit.Operation.Evidence.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return CommitMapping.Final(Result(
                    GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
                    mutation.Operation.OperationId,
                    requestHash));
            }

            return CommitMapping.Final(Result(
                GovernedLoopRevisionLifecycleMutationStatus.Conflict,
                mutation.Operation.OperationId,
                requestHash,
                null,
                conflictSnapshot?.Head));
        }

        if (commit.Status is not (GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed)
            || commit.Status == GovernedLoopRevisionStoreCommitStatus.Committed
                && (mutation.ExpectedStoreGeneration == long.MaxValue
                    || commit.StoreGeneration != mutation.ExpectedStoreGeneration + 1)
            || commit.Status == GovernedLoopRevisionStoreCommitStatus.Replayed
                && commit.StoreGeneration <= mutation.ExpectedStoreGeneration
            || !OperationMatchesRequest(commit.Operation, mutation.GraphId, mutation.Operation))
        {
            return CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
        }

        var storedOperation = commit.Operation!;
        GovernedLoopRevisionStoreSnapshot? snapshot = null;
        if (commit.Snapshot is not null
            && !GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                commit.Snapshot,
                mutation.GraphId,
                commit.StoreGeneration,
                out snapshot))
        {
            return CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
        }

        var head = snapshot?.Head;
        if (commit.Status == GovernedLoopRevisionStoreCommitStatus.Committed)
        {
            if (!Equals(storedOperation.Evidence, mutation.Operation)
                || mutation.HeadToWrite is not null && !Equals(head, mutation.HeadToWrite)
                || mutation.HeadToWrite is null && mutation.Operation.ResultHead is not null && !Equals(head, mutation.Operation.ResultHead)
                || mutation.Operation.ResultHead is null
                    && (commit.Snapshot is not null || !IsAbsentGraphLifecycleNotFound(storedOperation.Evidence)))
            {
                return CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
            }
        }
        else if (snapshot is null
            ? !IsAbsentGraphLifecycleNotFound(storedOperation.Evidence)
            : !snapshot.Operations.Any(operation => Equals(operation, storedOperation.Evidence)))
        {
            return CommitMapping.Final(Result(GovernedLoopRevisionLifecycleMutationStatus.Ambiguous, mutation.Operation.OperationId, requestHash));
        }

        return CommitMapping.Final(Result(
            commit.Status == GovernedLoopRevisionStoreCommitStatus.Replayed
                ? GovernedLoopRevisionLifecycleMutationStatus.Replayed
                : MapEvidenceStatus(storedOperation.Evidence),
            mutation.Operation.OperationId,
            requestHash,
            storedOperation.Evidence,
            head));
    }

    private static bool IsAbsentGraphLifecycleNotFound(GovernedLoopRevisionOperationEvidence evidence)
        => evidence.Outcome == GovernedLoopRevisionOperationOutcome.NotFound
            && evidence.FailureCode == GovernedLoopRevisionOperationFailureCode.LifecycleNotFound
            && evidence.PreviousHead is null
            && evidence.ResultHead is null;

    private static GovernedLoopRevisionLifecycleHead? TryCaptureCommitHead(
        GovernedLoopRevisionStoreSnapshot? candidate,
        string graphId,
        long storeGeneration)
        => GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(candidate, graphId, storeGeneration, out var snapshot)
            ? snapshot!.Head
            : null;

    private static bool ValidCommitOperation(GovernedLoopRevisionStoredOperation? operation)
        => operation is not null
            && CustomLoopArtifactIdentifier.IsValid(operation.GraphId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            && GovernedLoopRevisionContractValidator.Validate(operation.Evidence).IsValid
            && GovernedLoopRevisionStoreSnapshotGuard.EvidenceBelongsToGraph(operation.Evidence, operation.GraphId);

    private static bool OperationMatchesRequest(
        GovernedLoopRevisionStoredOperation? operation,
        GovernedLoopRevisionLifecycleRequest request,
        string requestHash)
        => ValidCommitOperation(operation)
            && string.Equals(operation!.GraphId, request.GraphId, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.OperationId, request.OperationId, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.RequestHash, requestHash, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.ActorId, request.ActorId.Value, StringComparison.Ordinal)
            && operation.Evidence.Kind == request.Kind
            && Equals(operation.Evidence.CandidateRevision, request.CandidateRevision)
            && Equals(operation.Evidence.TargetRevision, request.TargetRevision)
            && Equals(operation.Evidence.RollbackSourcePublication, request.RollbackSourcePublication);

    private static bool OperationMatchesRequest(
        GovernedLoopRevisionStoredOperation? operation,
        string graphId,
        GovernedLoopRevisionOperationEvidence expectedEvidence)
        => ValidCommitOperation(operation)
            && string.Equals(operation!.GraphId, graphId, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.OperationId, expectedEvidence.OperationId, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.RequestHash, expectedEvidence.RequestHash, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.ActorId, expectedEvidence.ActorId, StringComparison.Ordinal)
            && operation.Evidence.Kind == expectedEvidence.Kind
            && Equals(operation.Evidence.CandidateRevision, expectedEvidence.CandidateRevision)
            && Equals(operation.Evidence.TargetRevision, expectedEvidence.TargetRevision)
            && Equals(operation.Evidence.RollbackSourcePublication, expectedEvidence.RollbackSourcePublication);

    private static bool HasExactDurableProof(GovernedLoopRevisionLifecycleMutationResult? result)
        => result?.Evidence is { } evidence
            && GovernedLoopRevisionContractValidator.Validate(evidence).IsValid
            && string.Equals(evidence.OperationId, result.OperationId, StringComparison.Ordinal)
            && string.Equals(evidence.RequestHash, result.RequestHash, StringComparison.Ordinal)
            && ExactDurableComposition(result.Status, evidence.Outcome);

    private static bool ExactDurableComposition(
        GovernedLoopRevisionLifecycleMutationStatus status,
        GovernedLoopRevisionOperationOutcome outcome)
        => status switch
        {
            GovernedLoopRevisionLifecycleMutationStatus.Committed => outcome == GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionLifecycleMutationStatus.Replayed => outcome is GovernedLoopRevisionOperationOutcome.Committed
                or GovernedLoopRevisionOperationOutcome.Conflict
                or GovernedLoopRevisionOperationOutcome.NotFound
                or GovernedLoopRevisionOperationOutcome.LimitExceeded,
            GovernedLoopRevisionLifecycleMutationStatus.Conflict => outcome == GovernedLoopRevisionOperationOutcome.Conflict,
            GovernedLoopRevisionLifecycleMutationStatus.NotFound => outcome == GovernedLoopRevisionOperationOutcome.NotFound,
            GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded => outcome == GovernedLoopRevisionOperationOutcome.LimitExceeded,
            _ => false,
        };

    private static bool TryValidateStoredOperation(GovernedLoopRevisionStoredOperation? operation, string expectedOperationId)
        => operation is null
            || ValidCommitOperation(operation)
                && string.Equals(operation.Evidence.OperationId, expectedOperationId, StringComparison.Ordinal);

    private static bool ExpectedHeadMatches(
        GovernedLoopRevisionLifecycleRequest request,
        GovernedLoopRevisionLifecycleHead head)
        => request.ExpectedLifecycleVersion == head.LifecycleVersion
            && request.ExpectedLifecycleStatus == head.Status
            && GovernedLoopRevisionStoreSnapshotGuard.SameRevision(request.ExpectedDraftRevision, head.DraftRevision)
                == (request.ExpectedDraftRevision is not null || head.DraftRevision is not null)
            && Equals(request.ExpectedPublishedRevision, head.PublishedRevision);

    private static GovernedLoopRevisionLifecycleMutationStatus MapEvidenceStatus(GovernedLoopRevisionOperationEvidence evidence)
        => evidence.Outcome switch
        {
            GovernedLoopRevisionOperationOutcome.Committed => GovernedLoopRevisionLifecycleMutationStatus.Committed,
            GovernedLoopRevisionOperationOutcome.Conflict => GovernedLoopRevisionLifecycleMutationStatus.Conflict,
            GovernedLoopRevisionOperationOutcome.NotFound => GovernedLoopRevisionLifecycleMutationStatus.NotFound,
            GovernedLoopRevisionOperationOutcome.LimitExceeded => GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded,
            _ => GovernedLoopRevisionLifecycleMutationStatus.Ambiguous,
        };

    private static DateTimeOffset PreflightTimestamp(GovernedLoopRevisionStoreSnapshot? snapshot)
        => snapshot?.Head.UpdatedAtUtc ?? DateTimeOffset.UnixEpoch;

    private DateTimeOffset UtcNow()
    {
        try
        {
            var value = _timeProvider.GetUtcNow().ToUniversalTime();
            return value == default ? default : value;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: GovernedLoopRevisionContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string SafeOperationId(GovernedLoopRevisionLifecycleRequest? request)
        => CustomLoopArtifactIdentifier.IsValid(request?.OperationId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            ? request!.OperationId
            : string.Empty;

    private static GovernedLoopRevisionLifecycleMutationResult Result(
        GovernedLoopRevisionLifecycleMutationStatus status,
        string operationId,
        string requestHash,
        GovernedLoopRevisionOperationEvidence? evidence = null,
        GovernedLoopRevisionLifecycleHead? head = null,
        IReadOnlyList<GovernedLoopRevisionLifecycleValidationError>? validationErrors = null)
        => new(
            status,
            operationId,
            requestHash,
            evidence,
            head,
            validationErrors ?? Array.Empty<GovernedLoopRevisionLifecycleValidationError>());

}
