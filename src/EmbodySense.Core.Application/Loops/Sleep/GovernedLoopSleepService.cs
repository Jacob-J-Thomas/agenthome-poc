using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Publishes sleeping checkpoints and admits exactly-once wake continuations through explicit durable ports.</summary>
/// <remarks>
/// The service is deliberately one-shot and host-neutral. It never senses or authenticates events, owns no background
/// lifetime, and invokes continuation only after durable <see cref="GovernedLoopWakeDisposition.Prepared"/> evidence.
/// Prepared and ambiguous operations are reconciled by their exact stable operation identity before any safe retry.
/// </remarks>
public sealed class GovernedLoopSleepService
{
    private const string AmbiguousContinuationReference = "continuation-outcome-ambiguous";
    private const string CancelledReference = "run-cancelled";
    private const string ConflictReference = "optimistic-conflict";
    private const string ExpiredReference = "run-expired";
    private const string FailedContinuationReference = "continuation-not-committed";
    private const string MalformedContinuationReference = "malformed-continuation-result";
    private const string StaleReference = "stale-frontier-binding";
    private readonly IGovernedLoopSleepStore _store;
    private readonly IGovernedLoopSleepCurrentPosturePort _currentPosture;
    private readonly IGovernedLoopWakeContinuationPort _continuation;
    private readonly IGovernedLoopAuthenticatedWakeVerificationPort _authenticatedWakeVerification;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one adapter-independent sleep and wake policy service.</summary>
    /// <param name="store">The crash-safe checkpoint and wake evidence store.</param>
    /// <param name="currentPosture">The authoritative current lifecycle, frontier, effect, publication, and unattended-authority reader.</param>
    /// <param name="continuation">The exact idempotent continuation and reconciliation boundary.</param>
    /// <param name="authenticatedWakeVerification">The trusted already-authenticated event verification boundary.</param>
    /// <param name="timeProvider">The optional trusted UTC clock.</param>
    public GovernedLoopSleepService(
        IGovernedLoopSleepStore store,
        IGovernedLoopSleepCurrentPosturePort currentPosture,
        IGovernedLoopWakeContinuationPort continuation,
        IGovernedLoopAuthenticatedWakeVerificationPort authenticatedWakeVerification,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currentPosture = currentPosture ?? throw new ArgumentNullException(nameof(currentPosture));
        _continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        _authenticatedWakeVerification = authenticatedWakeVerification ?? throw new ArgumentNullException(nameof(authenticatedWakeVerification));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Durably publishes one exact sleeping checkpoint before its current frontier owner is released.</summary>
    /// <param name="request">The exact waiting activation and admitted wake condition.</param>
    /// <param name="cancellationToken">The token used before and during the atomic publish-and-release boundary.</param>
    /// <returns>A bounded publication result. Ambiguous storage is reconciled by deterministic checkpoint identity.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before the publish-and-release outcome becomes ambiguous.</exception>
    public async Task<GovernedLoopSleepPublicationResult> PublishAsync(
        GovernedLoopSleepPublicationRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetUtcNow(out var readStartedAtUtc)
            || !TryCreateCheckpoint(request, readStartedAtUtc, out var provisional))
        {
            return Publication(GovernedLoopSleepPublicationStatus.Invalid);
        }

        GovernedLoopSleepCurrentPostureReadResult? read;
        try
        {
            read = await _currentPosture.ReadAsync(provisional.Binding.Execution, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Publication(GovernedLoopSleepPublicationStatus.Unavailable);
        }

        if (!TryGetUtcNow(out var readCompletedAtUtc))
        {
            return Publication(GovernedLoopSleepPublicationStatus.Unavailable);
        }

        var readStatus = ValidatePostureRead(read, provisional.Binding.Execution, readStartedAtUtc, readCompletedAtUtc, out var posture);
        if (readStatus != GovernedLoopSleepPublicationStatus.Published)
        {
            return Publication(readStatus);
        }

        if (!TryCreateCheckpoint(request, readCompletedAtUtc, out var checkpoint))
        {
            return Publication(GovernedLoopSleepPublicationStatus.Invalid);
        }

        var postureDecision = GovernedLoopSleepPosturePolicy.EvaluatePublication(posture!, checkpoint, readCompletedAtUtc);
        if (postureDecision != GovernedLoopSleepPostureDecision.Eligible)
        {
            return Publication(MapPublication(postureDecision));
        }

        GovernedLoopSleepCheckpointMutationResult? mutation;
        try
        {
            mutation = await _store.PublishAndReleaseAsync(checkpoint, posture!.PostureHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await ReconcilePublicationAsync(checkpoint).ConfigureAwait(false);
        }

        if (!IsCheckpointMutationShapeValid(mutation, checkpoint))
        {
            return Publication(GovernedLoopSleepPublicationStatus.Invalid);
        }

        return mutation!.Status switch
        {
            GovernedLoopSleepCheckpointMutationStatus.Committed => Publication(GovernedLoopSleepPublicationStatus.Published, mutation.Checkpoint),
            GovernedLoopSleepCheckpointMutationStatus.Replayed => Publication(GovernedLoopSleepPublicationStatus.Replayed, mutation.Checkpoint),
            GovernedLoopSleepCheckpointMutationStatus.Conflict => Publication(GovernedLoopSleepPublicationStatus.Conflict),
            GovernedLoopSleepCheckpointMutationStatus.Unavailable => Publication(GovernedLoopSleepPublicationStatus.Unavailable),
            GovernedLoopSleepCheckpointMutationStatus.Ambiguous => await ReconcilePublicationAsync(checkpoint).ConfigureAwait(false),
            _ => Publication(GovernedLoopSleepPublicationStatus.Invalid)
        };
    }

    /// <summary>Admits one timestamp or already-authenticated event wake and commits at most one exact continuation.</summary>
    /// <param name="request">The exact checkpoint and event-authentication evidence, when applicable.</param>
    /// <param name="cancellationToken">The token used until durable continuation intent exists.</param>
    /// <returns>The bounded durable wake posture.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before durable prepared wake intent exists.</exception>
    public async Task<GovernedLoopWakeResult> WakeAsync(
        GovernedLoopWakeRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsWakeRequestIdentifierShapeValid(request))
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        var checkpointRead = await ReadCheckpointAsync(request!.CheckpointId, cancellationToken).ConfigureAwait(false);
        if (checkpointRead.Status != GovernedLoopSleepStoreReadStatus.Found)
        {
            return Wake(MapRead(checkpointRead.Status));
        }

        var checkpoint = checkpointRead.Checkpoint!;
        if (!string.Equals(checkpoint.ContentHash, request.CheckpointHash, StringComparison.Ordinal))
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        if (checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent)
        {
            var verificationStatus = await VerifyAuthenticatedWakeAsync(
                checkpoint,
                request.AuthenticationEvidenceHash,
                cancellationToken).ConfigureAwait(false);
            if (verificationStatus is not null)
            {
                return Wake(verificationStatus.Value);
            }
        }

        if (!TryCreateWakeIdentity(checkpoint, request.AuthenticationEvidenceHash, out var identity))
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        var existingRead = await ReadWakeAsync(identity.WakeId, cancellationToken).ConfigureAwait(false);
        if (existingRead.Status == GovernedLoopSleepStoreReadStatus.Found)
        {
            if (!IsWakeEvidenceValid(checkpoint, existingRead.Evidence, identity.WakeId))
            {
                return Wake(GovernedLoopWakeResultStatus.Invalid);
            }

            return existingRead.Evidence!.Disposition is GovernedLoopWakeDisposition.Prepared or GovernedLoopWakeDisposition.AmbiguousAttempt
                ? await ReconcileExistingAsync(checkpoint, existingRead.Evidence, cancellationToken).ConfigureAwait(false)
                : FromEvidence(existingRead.Evidence, duplicateCommitted: true);
        }

        if (existingRead.Status != GovernedLoopSleepStoreReadStatus.NotFound)
        {
            return Wake(MapRead(existingRead.Status));
        }

        if (!TryGetUtcNow(out var readStartedAtUtc))
        {
            return Wake(GovernedLoopWakeResultStatus.Unavailable);
        }

        var postureRead = await ReadPostureAsync(checkpoint.Binding.Execution, cancellationToken).ConfigureAwait(false);
        if (!TryGetUtcNow(out var evaluatedAtUtc))
        {
            return Wake(GovernedLoopWakeResultStatus.Unavailable);
        }

        var postureStatus = ValidateWakePostureRead(postureRead, checkpoint.Binding.Execution, readStartedAtUtc, evaluatedAtUtc, out var posture);
        if (postureStatus is not null)
        {
            return Wake(postureStatus.Value);
        }

        if (evaluatedAtUtc < checkpoint.PublishedAtUtc)
        {
            return Wake(GovernedLoopWakeResultStatus.NotEligible);
        }

        var decision = GovernedLoopSleepPosturePolicy.EvaluateWake(posture!, checkpoint, evaluatedAtUtc);
        if (decision != GovernedLoopSleepPostureDecision.Eligible)
        {
            if (decision is GovernedLoopSleepPostureDecision.Paused
                or GovernedLoopSleepPostureDecision.ReviewBlocked
                or GovernedLoopSleepPostureDecision.AmbiguousAttempt)
            {
                return Wake(MapWake(decision));
            }

            var disposition = MapDisposition(decision);
            var evidence = CreateInitialEvidence(identity, disposition, null, EvidenceReference(decision), evaluatedAtUtc);
            return await PersistInitialAsync(checkpoint, evidence, posture!.PostureHash, invokeContinuation: false, cancellationToken).ConfigureAwait(false);
        }

        if (checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp
            && checkpoint.WakeDeadlineUtc is { } deadlineUtc
            && evaluatedAtUtc < deadlineUtc)
        {
            return Wake(GovernedLoopWakeResultStatus.NotEligible);
        }

        var operationId = GovernedLoopWakeOperationHash.Create(identity.WakeId);
        var prepared = CreateInitialEvidence(identity, GovernedLoopWakeDisposition.Prepared, operationId, null, evaluatedAtUtc);
        return await PersistInitialAsync(checkpoint, prepared, posture!.PostureHash, invokeContinuation: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles one exact prepared or ambiguous continuation after restart without guessing its outcome.</summary>
    /// <param name="request">The exact checkpoint and wake identities discovered from durable evidence.</param>
    /// <param name="cancellationToken">The token used before any safe continuation retry begins.</param>
    /// <returns>The reconciled bounded wake posture.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before a new safe continuation retry begins.</exception>
    public async Task<GovernedLoopWakeResult> ReconcileAsync(
        GovernedLoopWakeReconciliationRequest? request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null || !GovernedLoopSleepPosturePolicy.IsHash(request.CheckpointId) || !GovernedLoopSleepPosturePolicy.IsHash(request.WakeId))
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        var checkpointRead = await ReadCheckpointAsync(request.CheckpointId, cancellationToken).ConfigureAwait(false);
        if (checkpointRead.Status != GovernedLoopSleepStoreReadStatus.Found)
        {
            return Wake(MapRead(checkpointRead.Status));
        }

        var wakeRead = await ReadWakeAsync(request.WakeId, cancellationToken).ConfigureAwait(false);
        if (wakeRead.Status != GovernedLoopSleepStoreReadStatus.Found)
        {
            return Wake(MapRead(wakeRead.Status));
        }

        if (!IsWakeEvidenceValid(checkpointRead.Checkpoint!, wakeRead.Evidence, request.WakeId))
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        return wakeRead.Evidence!.Disposition is GovernedLoopWakeDisposition.Prepared or GovernedLoopWakeDisposition.AmbiguousAttempt
            ? await ReconcileExistingAsync(checkpointRead.Checkpoint!, wakeRead.Evidence, cancellationToken).ConfigureAwait(false)
            : FromEvidence(wakeRead.Evidence, duplicateCommitted: false);
    }

    private async Task<GovernedLoopWakeResult> ReconcileExistingAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence current,
        CancellationToken cancellationToken)
    {
        var operationId = current.ContinuationOperationId;
        if (operationId is null)
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        GovernedLoopWakeContinuationResult? reconciliation;
        try
        {
            reconciliation = await _continuation.ReconcileAsync(
                new GovernedLoopWakeContinuationRequest(checkpoint, current.Identity, operationId, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            reconciliation = new GovernedLoopWakeContinuationResult(GovernedLoopWakeContinuationStatus.Ambiguous, EvidenceReference: AmbiguousContinuationReference);
        }

        if (!IsContinuationResultShapeValid(reconciliation))
        {
            reconciliation = new GovernedLoopWakeContinuationResult(GovernedLoopWakeContinuationStatus.Ambiguous, EvidenceReference: MalformedContinuationReference);
        }

        if (reconciliation!.Status == GovernedLoopWakeContinuationStatus.Committed)
        {
            return await AdvanceAfterContinuationAsync(checkpoint, current, reconciliation, continuationInvoked: false).ConfigureAwait(false);
        }

        if (reconciliation.Status is GovernedLoopWakeContinuationStatus.Ambiguous or GovernedLoopWakeContinuationStatus.Unavailable)
        {
            return current.Disposition == GovernedLoopWakeDisposition.AmbiguousAttempt
                ? FromEvidence(current, duplicateCommitted: false)
                : await AdvanceAfterContinuationAsync(checkpoint, current, reconciliation, continuationInvoked: false).ConfigureAwait(false);
        }

        if (reconciliation.Status == GovernedLoopWakeContinuationStatus.Conflict)
        {
            var persisted = await AdvanceAfterContinuationAsync(checkpoint, current, reconciliation, continuationInvoked: false).ConfigureAwait(false);
            return persisted.Status == GovernedLoopWakeResultStatus.Failed
                ? persisted with { Status = GovernedLoopWakeResultStatus.Conflict }
                : persisted;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await RetryAfterConclusiveNonCommitAsync(checkpoint, current, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeResult> RetryAfterConclusiveNonCommitAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence current,
        CancellationToken cancellationToken)
    {
        if (!TryGetUtcNow(out var readStartedAtUtc))
        {
            return Wake(GovernedLoopWakeResultStatus.Unavailable, current);
        }

        var read = await ReadPostureAsync(checkpoint.Binding.Execution, cancellationToken).ConfigureAwait(false);
        if (!TryGetUtcNow(out var evaluatedAtUtc))
        {
            return Wake(GovernedLoopWakeResultStatus.Unavailable, current);
        }

        var postureStatus = ValidateWakePostureRead(read, checkpoint.Binding.Execution, readStartedAtUtc, evaluatedAtUtc, out var posture);
        if (postureStatus is not null)
        {
            return Wake(postureStatus.Value, current);
        }

        if (evaluatedAtUtc < checkpoint.PublishedAtUtc
            || evaluatedAtUtc < current.RecordedAtUtc)
        {
            return Wake(GovernedLoopWakeResultStatus.NotEligible, current);
        }

        var decision = GovernedLoopSleepPosturePolicy.EvaluateWake(posture!, checkpoint, evaluatedAtUtc);
        if (decision != GovernedLoopSleepPostureDecision.Eligible)
        {
            if (decision is GovernedLoopSleepPostureDecision.Paused
                or GovernedLoopSleepPostureDecision.ReviewBlocked
                or GovernedLoopSleepPostureDecision.AmbiguousAttempt)
            {
                return Wake(MapWake(decision), current);
            }

            if (!TryCreateSuccessorEvidence(current, GovernedLoopWakeDisposition.Failed, null, EvidenceReference(decision), evaluatedAtUtc, out var failed))
            {
                return Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current);
            }

            var persisted = await PersistSuccessorAsync(checkpoint, current, failed, continuationInvoked: false).ConfigureAwait(false);
            return persisted.Status == GovernedLoopWakeResultStatus.Failed
                ? persisted with { Status = MapWake(decision) }
                : persisted;
        }

        if (checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp
            && checkpoint.WakeDeadlineUtc is { } deadlineUtc
            && evaluatedAtUtc < deadlineUtc)
        {
            return Wake(GovernedLoopWakeResultStatus.NotEligible, current);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await InvokeContinuationAsync(checkpoint, current, posture!.PostureHash).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeResult> PersistInitialAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence proposed,
        string expectedPostureHash,
        bool invokeContinuation,
        CancellationToken cancellationToken)
    {
        GovernedLoopWakeEvidenceMutationResult? mutation;
        try
        {
            mutation = await _store.CreateWakeAsync(checkpoint, proposed, expectedPostureHash, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await ReconcileInitialMutationAsync(checkpoint, proposed, expectedPostureHash, invokeContinuation, cancellationToken).ConfigureAwait(false);
        }

        if (!IsWakeMutationShapeValid(checkpoint, mutation, proposed))
        {
            return Wake(GovernedLoopWakeResultStatus.Invalid);
        }

        return mutation!.Status switch
        {
            GovernedLoopWakeEvidenceMutationStatus.Committed => await ContinueOrReturnAsync(checkpoint, mutation.Evidence!, expectedPostureHash, invokeContinuation, replayed: false).ConfigureAwait(false),
            GovernedLoopWakeEvidenceMutationStatus.Replayed => await ContinueOrReturnAsync(checkpoint, mutation.Evidence!, expectedPostureHash, invokeContinuation, replayed: true).ConfigureAwait(false),
            GovernedLoopWakeEvidenceMutationStatus.CheckpointClaimed => Wake(GovernedLoopWakeResultStatus.Late, mutation.Evidence),
            GovernedLoopWakeEvidenceMutationStatus.Conflict => Wake(GovernedLoopWakeResultStatus.Conflict),
            GovernedLoopWakeEvidenceMutationStatus.Unavailable => Wake(GovernedLoopWakeResultStatus.Unavailable),
            GovernedLoopWakeEvidenceMutationStatus.Ambiguous => await ReconcileInitialMutationAsync(checkpoint, proposed, expectedPostureHash, invokeContinuation, cancellationToken).ConfigureAwait(false),
            _ => Wake(GovernedLoopWakeResultStatus.Invalid)
        };
    }

    private async Task<GovernedLoopWakeResult> ReconcileInitialMutationAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence proposed,
        string expectedPostureHash,
        bool invokeContinuation,
        CancellationToken cancellationToken)
    {
        var read = await ReadWakeAsync(proposed.Identity.WakeId, CancellationToken.None).ConfigureAwait(false);
        if (read.Status == GovernedLoopSleepStoreReadStatus.Found)
        {
            if (!IsWakeEvidenceValid(checkpoint, read.Evidence, proposed.Identity.WakeId))
            {
                return Wake(GovernedLoopWakeResultStatus.Invalid);
            }

            return await ContinueOrReturnAsync(checkpoint, read.Evidence!, expectedPostureHash, invokeContinuation, replayed: true).ConfigureAwait(false);
        }

        return read.Status switch
        {
            GovernedLoopSleepStoreReadStatus.NotFound when cancellationToken.IsCancellationRequested => throw new OperationCanceledException(cancellationToken),
            GovernedLoopSleepStoreReadStatus.NotFound => Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt),
            GovernedLoopSleepStoreReadStatus.Conflict => Wake(GovernedLoopWakeResultStatus.Conflict),
            GovernedLoopSleepStoreReadStatus.Unavailable => Wake(GovernedLoopWakeResultStatus.Unavailable),
            _ => Wake(GovernedLoopWakeResultStatus.Invalid)
        };
    }

    private async Task<GovernedLoopWakeResult> ContinueOrReturnAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence evidence,
        string expectedPostureHash,
        bool invokeContinuation,
        bool replayed)
    {
        if (evidence.Disposition is GovernedLoopWakeDisposition.Prepared or GovernedLoopWakeDisposition.AmbiguousAttempt)
        {
            if (!invokeContinuation || replayed)
            {
                return await ReconcileExistingAsync(checkpoint, evidence, CancellationToken.None).ConfigureAwait(false);
            }

            return await InvokeContinuationAsync(checkpoint, evidence, expectedPostureHash).ConfigureAwait(false);
        }

        return FromEvidence(evidence, duplicateCommitted: replayed);
    }

    private async Task<GovernedLoopWakeResult> InvokeContinuationAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence prepared,
        string expectedPostureHash)
    {
        if (!TryGetUtcNow(out var continuationAtUtc))
        {
            return Wake(GovernedLoopWakeResultStatus.Unavailable, prepared);
        }

        if (continuationAtUtc < checkpoint.PublishedAtUtc
            || continuationAtUtc < prepared.RecordedAtUtc)
        {
            return Wake(GovernedLoopWakeResultStatus.NotEligible, prepared);
        }

        GovernedLoopWakeContinuationResult? continuation;
        try
        {
            continuation = await _continuation.ContinueAsync(
                new GovernedLoopWakeContinuationRequest(checkpoint, prepared.Identity, prepared.ContinuationOperationId!, expectedPostureHash),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            continuation = new GovernedLoopWakeContinuationResult(GovernedLoopWakeContinuationStatus.Ambiguous, EvidenceReference: AmbiguousContinuationReference);
        }

        if (!IsContinuationResultShapeValid(continuation))
        {
            continuation = new GovernedLoopWakeContinuationResult(GovernedLoopWakeContinuationStatus.Ambiguous, EvidenceReference: MalformedContinuationReference);
        }

        return await AdvanceAfterContinuationAsync(checkpoint, prepared, continuation!, continuationInvoked: true).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeResult> AdvanceAfterContinuationAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeContinuationResult continuation,
        bool continuationInvoked)
    {
        if (!TryGetUtcNow(out var recordedAtUtc)
            || current.EvidenceVersion >= GovernedLoopSleepContractLimits.MaxVersion)
        {
            return Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current, continuationInvoked);
        }

        var disposition = continuation.Status switch
        {
            GovernedLoopWakeContinuationStatus.Committed => GovernedLoopWakeDisposition.Committed,
            GovernedLoopWakeContinuationStatus.Ambiguous or GovernedLoopWakeContinuationStatus.Unavailable => GovernedLoopWakeDisposition.AmbiguousAttempt,
            _ => GovernedLoopWakeDisposition.Failed
        };
        var reference = disposition switch
        {
            GovernedLoopWakeDisposition.AmbiguousAttempt => continuation.EvidenceReference ?? AmbiguousContinuationReference,
            GovernedLoopWakeDisposition.Failed => continuation.EvidenceReference ?? FailedContinuationReference,
            _ => null
        };
        if (current.Disposition == GovernedLoopWakeDisposition.AmbiguousAttempt
            && disposition == GovernedLoopWakeDisposition.AmbiguousAttempt)
        {
            return FromEvidence(current, duplicateCommitted: false, continuationInvoked);
        }

        if (!TryCreateSuccessorEvidence(current, disposition, continuation.ContinuationEvidenceHash, reference, recordedAtUtc, out var next))
        {
            return Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current, continuationInvoked);
        }

        return await PersistSuccessorAsync(checkpoint, current, next, continuationInvoked).ConfigureAwait(false);
    }

    private async Task<GovernedLoopWakeResult> PersistSuccessorAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeEvidence next,
        bool continuationInvoked)
    {
        GovernedLoopWakeEvidenceMutationResult? mutation;
        try
        {
            mutation = await _store.AdvanceWakeAsync(current, next, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await ReconcileSuccessorMutationAsync(checkpoint, current, next, continuationInvoked).ConfigureAwait(false);
        }

        if (!IsWakeMutationShapeValid(checkpoint, mutation, next))
        {
            return Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current, continuationInvoked);
        }

        return mutation!.Status switch
        {
            GovernedLoopWakeEvidenceMutationStatus.Committed or GovernedLoopWakeEvidenceMutationStatus.Replayed => FromEvidence(mutation.Evidence!, duplicateCommitted: false, continuationInvoked),
            GovernedLoopWakeEvidenceMutationStatus.Conflict => await ReconcileSuccessorMutationAsync(checkpoint, current, next, continuationInvoked).ConfigureAwait(false),
            GovernedLoopWakeEvidenceMutationStatus.Unavailable or GovernedLoopWakeEvidenceMutationStatus.Ambiguous => await ReconcileSuccessorMutationAsync(checkpoint, current, next, continuationInvoked).ConfigureAwait(false),
            _ => Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current, continuationInvoked)
        };
    }

    private async Task<GovernedLoopWakeResult> ReconcileSuccessorMutationAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeEvidence proposed,
        bool continuationInvoked)
    {
        var read = await ReadWakeAsync(current.Identity.WakeId, CancellationToken.None).ConfigureAwait(false);
        if (read.Status == GovernedLoopSleepStoreReadStatus.Found)
        {
            if (!IsWakeEvidenceValid(checkpoint, read.Evidence, current.Identity.WakeId))
            {
                return Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current, continuationInvoked);
            }

            if (read.Evidence!.ContentHash == proposed.ContentHash || read.Evidence.EvidenceVersion > current.EvidenceVersion)
            {
                return FromEvidence(read.Evidence, duplicateCommitted: false, continuationInvoked);
            }
        }

        return Wake(GovernedLoopWakeResultStatus.AmbiguousAttempt, current, continuationInvoked);
    }

    private async Task<GovernedLoopSleepPublicationResult> ReconcilePublicationAsync(GovernedLoopSleepCheckpoint proposed)
    {
        var read = await ReadCheckpointAsync(proposed.CheckpointId, CancellationToken.None).ConfigureAwait(false);
        return read.Status switch
        {
            GovernedLoopSleepStoreReadStatus.Found when SameCheckpointIdentity(proposed, read.Checkpoint) => Publication(GovernedLoopSleepPublicationStatus.Replayed, read.Checkpoint),
            GovernedLoopSleepStoreReadStatus.Found => Publication(GovernedLoopSleepPublicationStatus.Conflict),
            GovernedLoopSleepStoreReadStatus.NotFound => Publication(GovernedLoopSleepPublicationStatus.Ambiguous),
            GovernedLoopSleepStoreReadStatus.Conflict => Publication(GovernedLoopSleepPublicationStatus.Conflict),
            GovernedLoopSleepStoreReadStatus.Unavailable => Publication(GovernedLoopSleepPublicationStatus.Ambiguous),
            _ => Publication(GovernedLoopSleepPublicationStatus.Invalid)
        };
    }

    private async Task<GovernedLoopSleepCheckpointReadResult> ReadCheckpointAsync(string checkpointId, CancellationToken cancellationToken)
    {
        GovernedLoopSleepCheckpointReadResult? read;
        try
        {
            read = await _store.ReadCheckpointAsync(checkpointId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.Unavailable);
        }

        if (read is null || !Enum.IsDefined(read.Status))
        {
            return new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.Conflict);
        }

        var found = read.Status == GovernedLoopSleepStoreReadStatus.Found;
        if (found != (read.Checkpoint is not null)
            || found && (!GovernedLoopSleepContractValidator.Validate(read.Checkpoint).IsValid
                || !string.Equals(read.Checkpoint!.CheckpointId, checkpointId, StringComparison.Ordinal)))
        {
            return new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.Conflict);
        }

        return read;
    }

    private async Task<GovernedLoopWakeEvidenceReadResult> ReadWakeAsync(string wakeId, CancellationToken cancellationToken)
    {
        GovernedLoopWakeEvidenceReadResult? read;
        try
        {
            read = await _store.ReadWakeAsync(wakeId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopWakeEvidenceReadResult(GovernedLoopSleepStoreReadStatus.Unavailable);
        }

        if (read is null || !Enum.IsDefined(read.Status))
        {
            return new GovernedLoopWakeEvidenceReadResult(GovernedLoopSleepStoreReadStatus.Conflict);
        }

        var found = read.Status == GovernedLoopSleepStoreReadStatus.Found;
        if (found != (read.Evidence is not null)
            || found && (!GovernedLoopSleepContractValidator.Validate(read.Evidence).IsValid
                || !string.Equals(read.Evidence!.Identity.WakeId, wakeId, StringComparison.Ordinal)))
        {
            return new GovernedLoopWakeEvidenceReadResult(GovernedLoopSleepStoreReadStatus.Conflict);
        }

        return read;
    }

    private async Task<GovernedLoopSleepCurrentPostureReadResult?> ReadPostureAsync(
        GovernedLoopExecutionBinding binding,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _currentPosture.ReadAsync(binding, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopWakeResultStatus?> VerifyAuthenticatedWakeAsync(
        GovernedLoopSleepCheckpoint checkpoint,
        string? authenticationEvidenceHash,
        CancellationToken cancellationToken)
    {
        if (!GovernedLoopSleepPosturePolicy.IsHash(authenticationEvidenceHash)
            || checkpoint.AuthenticatedEventReference is null)
        {
            return GovernedLoopWakeResultStatus.Invalid;
        }

        if (!TryGetUtcNow(out var verificationStartedAtUtc))
        {
            return GovernedLoopWakeResultStatus.Unavailable;
        }

        GovernedLoopAuthenticatedWakeVerificationResult? result;
        try
        {
            result = await _authenticatedWakeVerification.VerifyAsync(
                new GovernedLoopAuthenticatedWakeVerificationRequest(
                    checkpoint.CheckpointId,
                    checkpoint.ContentHash,
                    checkpoint.AuthenticatedEventReference,
                    authenticationEvidenceHash!,
                    checkpoint.PublishedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GovernedLoopWakeResultStatus.Unavailable;
        }

        if (!TryGetUtcNow(out var verificationCompletedAtUtc)
            || verificationCompletedAtUtc < verificationStartedAtUtc)
        {
            return GovernedLoopWakeResultStatus.Unavailable;
        }

        if (result is null || !Enum.IsDefined(result.Status))
        {
            return GovernedLoopWakeResultStatus.Invalid;
        }

        if (result.Status != GovernedLoopAuthenticatedWakeVerificationStatus.Verified)
        {
            return result.Verification is not null
                ? GovernedLoopWakeResultStatus.Invalid
                : result.Status switch
                {
                    GovernedLoopAuthenticatedWakeVerificationStatus.Rejected => GovernedLoopWakeResultStatus.Invalid,
                    GovernedLoopAuthenticatedWakeVerificationStatus.NotFound => GovernedLoopWakeResultStatus.NotFound,
                    GovernedLoopAuthenticatedWakeVerificationStatus.Conflict => GovernedLoopWakeResultStatus.Conflict,
                    GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable => GovernedLoopWakeResultStatus.Unavailable,
                    _ => GovernedLoopWakeResultStatus.Invalid
                };
        }

        var verification = result.Verification;
        if (verification is null
            || !string.Equals(verification.CheckpointId, checkpoint.CheckpointId, StringComparison.Ordinal)
            || !string.Equals(verification.CheckpointHash, checkpoint.ContentHash, StringComparison.Ordinal)
            || !string.Equals(verification.AuthenticatedEventReference, checkpoint.AuthenticatedEventReference, StringComparison.Ordinal)
            || !string.Equals(verification.AuthenticationEvidenceHash, authenticationEvidenceHash, StringComparison.Ordinal)
            || !GovernedLoopSleepPosturePolicy.IsUtc(verification.OccurredAtUtc)
            || !GovernedLoopSleepPosturePolicy.IsUtc(verification.AuthenticatedAtUtc)
            || verification.OccurredAtUtc < checkpoint.PublishedAtUtc
            || verification.AuthenticatedAtUtc < verification.OccurredAtUtc
            || verification.AuthenticatedAtUtc > verificationCompletedAtUtc)
        {
            return GovernedLoopWakeResultStatus.Invalid;
        }

        return verification.Eligible ? null : GovernedLoopWakeResultStatus.NotEligible;
    }

    private static GovernedLoopSleepPublicationStatus ValidatePostureRead(
        GovernedLoopSleepCurrentPostureReadResult? read,
        GovernedLoopExecutionBinding binding,
        DateTimeOffset readStartedAtUtc,
        DateTimeOffset readCompletedAtUtc,
        out GovernedLoopSleepCurrentPosture? posture)
    {
        posture = null;
        if (read is null || !Enum.IsDefined(read.Status))
        {
            return GovernedLoopSleepPublicationStatus.Invalid;
        }

        if (read.Status != GovernedLoopSleepCurrentPostureReadStatus.Found)
        {
            return read.Posture is not null
                ? GovernedLoopSleepPublicationStatus.Invalid
                : read.Status switch
                {
                    GovernedLoopSleepCurrentPostureReadStatus.NotFound => GovernedLoopSleepPublicationStatus.NotFound,
                    GovernedLoopSleepCurrentPostureReadStatus.Conflict => GovernedLoopSleepPublicationStatus.Conflict,
                    GovernedLoopSleepCurrentPostureReadStatus.Unavailable => GovernedLoopSleepPublicationStatus.Unavailable,
                    _ => GovernedLoopSleepPublicationStatus.Invalid
                };
        }

        if (!GovernedLoopSleepPosturePolicy.IsWellFormed(read.Posture, binding, readStartedAtUtc, readCompletedAtUtc))
        {
            return GovernedLoopSleepPublicationStatus.Invalid;
        }

        posture = read.Posture;
        return GovernedLoopSleepPublicationStatus.Published;
    }

    private static GovernedLoopWakeResultStatus? ValidateWakePostureRead(
        GovernedLoopSleepCurrentPostureReadResult? read,
        GovernedLoopExecutionBinding binding,
        DateTimeOffset readStartedAtUtc,
        DateTimeOffset readCompletedAtUtc,
        out GovernedLoopSleepCurrentPosture? posture)
    {
        posture = null;
        if (read is null || !Enum.IsDefined(read.Status))
        {
            return GovernedLoopWakeResultStatus.Invalid;
        }

        if (read.Status != GovernedLoopSleepCurrentPostureReadStatus.Found)
        {
            return read.Posture is not null
                ? GovernedLoopWakeResultStatus.Invalid
                : read.Status switch
                {
                    GovernedLoopSleepCurrentPostureReadStatus.NotFound => GovernedLoopWakeResultStatus.NotFound,
                    GovernedLoopSleepCurrentPostureReadStatus.Conflict => GovernedLoopWakeResultStatus.Conflict,
                    GovernedLoopSleepCurrentPostureReadStatus.Unavailable => GovernedLoopWakeResultStatus.Unavailable,
                    _ => GovernedLoopWakeResultStatus.Invalid
                };
        }

        if (!GovernedLoopSleepPosturePolicy.IsWellFormed(read.Posture, binding, readStartedAtUtc, readCompletedAtUtc))
        {
            return GovernedLoopWakeResultStatus.Invalid;
        }

        posture = read.Posture;
        return null;
    }

    private static bool TryCreateCheckpoint(
        GovernedLoopSleepPublicationRequest? request,
        DateTimeOffset publishedAtUtc,
        out GovernedLoopSleepCheckpoint checkpoint)
    {
        checkpoint = null!;
        if (request?.Binding is null || !GovernedLoopSleepPosturePolicy.IsUtc(publishedAtUtc))
        {
            return false;
        }

        try
        {
            checkpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
                GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
                string.Empty,
                request.Binding,
                request.WakeMode,
                request.WakeDeadlineUtc,
                request.AuthenticatedEventReference,
                publishedAtUtc,
                string.Empty));
            return GovernedLoopSleepContractValidator.Validate(checkpoint).IsValid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryCreateWakeIdentity(
        GovernedLoopSleepCheckpoint checkpoint,
        string? authenticationEvidenceHash,
        out GovernedLoopWakeIdentity identity)
    {
        identity = null!;
        if (checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp && authenticationEvidenceHash is not null
            || checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent && !GovernedLoopSleepPosturePolicy.IsHash(authenticationEvidenceHash))
        {
            return false;
        }

        try
        {
            identity = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
                GovernedLoopWakeIdentity.CurrentSchemaVersion,
                string.Empty,
                checkpoint.CheckpointId,
                checkpoint.ContentHash,
                checkpoint.WakeMode,
                checkpoint.WakeMode == GovernedLoopWakeMode.AuthenticatedEvent ? checkpoint.AuthenticatedEventReference : null,
                authenticationEvidenceHash,
                string.Empty));
            return GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, identity).IsValid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static GovernedLoopWakeEvidence CreateInitialEvidence(
        GovernedLoopWakeIdentity identity,
        GovernedLoopWakeDisposition disposition,
        string? continuationOperationId,
        string? dispositionEvidenceReference,
        DateTimeOffset recordedAtUtc)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            GovernedLoopWakeEvidence.CurrentSchemaVersion,
            1,
            identity,
            disposition,
            continuationOperationId,
            null,
            dispositionEvidenceReference,
            recordedAtUtc,
            string.Empty));

    private static bool TryCreateSuccessorEvidence(
        GovernedLoopWakeEvidence current,
        GovernedLoopWakeDisposition disposition,
        string? continuationEvidenceHash,
        string? dispositionEvidenceReference,
        DateTimeOffset recordedAtUtc,
        out GovernedLoopWakeEvidence next)
    {
        next = null!;
        try
        {
            next = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
                GovernedLoopWakeEvidence.CurrentSchemaVersion,
                current.EvidenceVersion + 1,
                current.Identity,
                disposition,
                current.ContinuationOperationId,
                continuationEvidenceHash,
                dispositionEvidenceReference,
                recordedAtUtc,
                string.Empty));
            return GovernedLoopSleepContractValidator.ValidateTransition(current, next).IsValid;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsCheckpointMutationShapeValid(
        GovernedLoopSleepCheckpointMutationResult? mutation,
        GovernedLoopSleepCheckpoint proposed)
    {
        if (mutation is null || !Enum.IsDefined(mutation.Status))
        {
            return false;
        }

        var requiresCheckpoint = mutation.Status is GovernedLoopSleepCheckpointMutationStatus.Committed
            or GovernedLoopSleepCheckpointMutationStatus.Replayed;
        return requiresCheckpoint == (mutation.Checkpoint is not null)
            && (!requiresCheckpoint || SameCheckpointIdentity(proposed, mutation.Checkpoint))
            && (mutation.Status != GovernedLoopSleepCheckpointMutationStatus.Committed
                || string.Equals(proposed.ContentHash, mutation.Checkpoint!.ContentHash, StringComparison.Ordinal));
    }

    private static bool IsWakeMutationShapeValid(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidenceMutationResult? mutation,
        GovernedLoopWakeEvidence proposed)
    {
        if (mutation is null || !Enum.IsDefined(mutation.Status))
        {
            return false;
        }

        var requiresEvidence = mutation.Status is GovernedLoopWakeEvidenceMutationStatus.Committed
            or GovernedLoopWakeEvidenceMutationStatus.Replayed
            or GovernedLoopWakeEvidenceMutationStatus.CheckpointClaimed;
        if (requiresEvidence != (mutation.Evidence is not null))
        {
            return false;
        }

        if (!requiresEvidence || !GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, mutation.Evidence).IsValid)
        {
            return !requiresEvidence;
        }

        if (mutation.Status == GovernedLoopWakeEvidenceMutationStatus.CheckpointClaimed)
        {
            return !string.Equals(mutation.Evidence!.Identity.WakeId, proposed.Identity.WakeId, StringComparison.Ordinal);
        }

        return string.Equals(mutation.Evidence!.Identity.WakeId, proposed.Identity.WakeId, StringComparison.Ordinal)
            && (mutation.Status != GovernedLoopWakeEvidenceMutationStatus.Committed
                || string.Equals(mutation.Evidence.ContentHash, proposed.ContentHash, StringComparison.Ordinal));
    }

    private static bool IsWakeEvidenceValid(
        GovernedLoopSleepCheckpoint checkpoint,
        GovernedLoopWakeEvidence? evidence,
        string wakeId)
        => evidence is not null
            && string.Equals(evidence.Identity.WakeId, wakeId, StringComparison.Ordinal)
            && GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, evidence).IsValid;

    private static bool IsContinuationResultShapeValid(GovernedLoopWakeContinuationResult? result)
    {
        if (result is null || !Enum.IsDefined(result.Status))
        {
            return false;
        }

        if (result.Status == GovernedLoopWakeContinuationStatus.Committed)
        {
            return GovernedLoopSleepPosturePolicy.IsHash(result.ContinuationEvidenceHash)
                && result.EvidenceReference is null;
        }

        return result.ContinuationEvidenceHash is null
            && CustomLoopArtifactIdentifier.IsValid(result.EvidenceReference, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters);
    }

    private static bool SameCheckpointIdentity(
        GovernedLoopSleepCheckpoint proposed,
        GovernedLoopSleepCheckpoint? actual)
        => actual is not null
            && GovernedLoopSleepContractValidator.Validate(actual).IsValid
            && string.Equals(proposed.CheckpointId, actual.CheckpointId, StringComparison.Ordinal)
            && Equals(proposed.Binding, actual.Binding)
            && proposed.WakeMode == actual.WakeMode
            && proposed.WakeDeadlineUtc == actual.WakeDeadlineUtc
            && actual.PublishedAtUtc <= proposed.PublishedAtUtc
            && string.Equals(proposed.AuthenticatedEventReference, actual.AuthenticatedEventReference, StringComparison.Ordinal);

    private bool TryGetUtcNow(out DateTimeOffset value)
    {
        value = default;
        try
        {
            var candidate = _timeProvider.GetUtcNow();
            if (!GovernedLoopSleepPosturePolicy.IsUtc(candidate))
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

    private static bool IsWakeRequestIdentifierShapeValid(GovernedLoopWakeRequest? request)
        => request is not null
            && GovernedLoopSleepPosturePolicy.IsHash(request.CheckpointId)
            && GovernedLoopSleepPosturePolicy.IsHash(request.CheckpointHash);

    private static string EvidenceReference(GovernedLoopSleepPostureDecision decision)
        => decision switch
        {
            GovernedLoopSleepPostureDecision.Stale => StaleReference,
            GovernedLoopSleepPostureDecision.Cancelled => CancelledReference,
            GovernedLoopSleepPostureDecision.Expired => ExpiredReference,
            _ => ConflictReference
        };

    private static GovernedLoopWakeDisposition MapDisposition(GovernedLoopSleepPostureDecision decision)
        => decision switch
        {
            GovernedLoopSleepPostureDecision.Stale => GovernedLoopWakeDisposition.Stale,
            GovernedLoopSleepPostureDecision.Cancelled => GovernedLoopWakeDisposition.Cancelled,
            GovernedLoopSleepPostureDecision.Expired => GovernedLoopWakeDisposition.Expired,
            _ => GovernedLoopWakeDisposition.Conflict
        };

    private static GovernedLoopSleepPublicationStatus MapPublication(GovernedLoopSleepPostureDecision decision)
        => decision switch
        {
            GovernedLoopSleepPostureDecision.Stale => GovernedLoopSleepPublicationStatus.Stale,
            GovernedLoopSleepPostureDecision.Cancelled => GovernedLoopSleepPublicationStatus.Cancelled,
            GovernedLoopSleepPostureDecision.Expired => GovernedLoopSleepPublicationStatus.Expired,
            GovernedLoopSleepPostureDecision.Paused => GovernedLoopSleepPublicationStatus.Paused,
            GovernedLoopSleepPostureDecision.ReviewBlocked => GovernedLoopSleepPublicationStatus.ReviewBlocked,
            GovernedLoopSleepPostureDecision.AmbiguousAttempt => GovernedLoopSleepPublicationStatus.AmbiguousAttempt,
            _ => GovernedLoopSleepPublicationStatus.Invalid
        };

    private static GovernedLoopWakeResultStatus MapWake(GovernedLoopSleepPostureDecision decision)
        => decision switch
        {
            GovernedLoopSleepPostureDecision.Stale => GovernedLoopWakeResultStatus.Stale,
            GovernedLoopSleepPostureDecision.Cancelled => GovernedLoopWakeResultStatus.Cancelled,
            GovernedLoopSleepPostureDecision.Expired => GovernedLoopWakeResultStatus.Expired,
            GovernedLoopSleepPostureDecision.Paused => GovernedLoopWakeResultStatus.Paused,
            GovernedLoopSleepPostureDecision.ReviewBlocked => GovernedLoopWakeResultStatus.ReviewBlocked,
            GovernedLoopSleepPostureDecision.AmbiguousAttempt => GovernedLoopWakeResultStatus.AmbiguousAttempt,
            _ => GovernedLoopWakeResultStatus.Invalid
        };

    private static GovernedLoopWakeResultStatus MapRead(GovernedLoopSleepStoreReadStatus status)
        => status switch
        {
            GovernedLoopSleepStoreReadStatus.NotFound => GovernedLoopWakeResultStatus.NotFound,
            GovernedLoopSleepStoreReadStatus.Conflict => GovernedLoopWakeResultStatus.Conflict,
            GovernedLoopSleepStoreReadStatus.Unavailable => GovernedLoopWakeResultStatus.Unavailable,
            _ => GovernedLoopWakeResultStatus.Invalid
        };

    private static GovernedLoopWakeResult FromEvidence(
        GovernedLoopWakeEvidence evidence,
        bool duplicateCommitted,
        bool continuationInvoked = false)
    {
        var status = evidence.Disposition switch
        {
            GovernedLoopWakeDisposition.Committed when duplicateCommitted => GovernedLoopWakeResultStatus.Duplicate,
            GovernedLoopWakeDisposition.Committed => GovernedLoopWakeResultStatus.Committed,
            GovernedLoopWakeDisposition.Duplicate => GovernedLoopWakeResultStatus.Duplicate,
            GovernedLoopWakeDisposition.Late => GovernedLoopWakeResultStatus.Late,
            GovernedLoopWakeDisposition.Stale => GovernedLoopWakeResultStatus.Stale,
            GovernedLoopWakeDisposition.Conflict => GovernedLoopWakeResultStatus.Conflict,
            GovernedLoopWakeDisposition.Cancelled => GovernedLoopWakeResultStatus.Cancelled,
            GovernedLoopWakeDisposition.Expired => GovernedLoopWakeResultStatus.Expired,
            GovernedLoopWakeDisposition.Paused => GovernedLoopWakeResultStatus.Paused,
            GovernedLoopWakeDisposition.ReviewBlocked => GovernedLoopWakeResultStatus.ReviewBlocked,
            GovernedLoopWakeDisposition.Prepared or GovernedLoopWakeDisposition.AmbiguousAttempt => GovernedLoopWakeResultStatus.AmbiguousAttempt,
            GovernedLoopWakeDisposition.Failed => GovernedLoopWakeResultStatus.Failed,
            _ => GovernedLoopWakeResultStatus.Invalid
        };
        return Wake(status, evidence, continuationInvoked);
    }

    private static GovernedLoopSleepPublicationResult Publication(
        GovernedLoopSleepPublicationStatus status,
        GovernedLoopSleepCheckpoint? checkpoint = null)
        => new(status, checkpoint);

    private static GovernedLoopWakeResult Wake(
        GovernedLoopWakeResultStatus status,
        GovernedLoopWakeEvidence? evidence = null,
        bool continuationInvoked = false)
        => new(status, evidence, continuationInvoked);
}
