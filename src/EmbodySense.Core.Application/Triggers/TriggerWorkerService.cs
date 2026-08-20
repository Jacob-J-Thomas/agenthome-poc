using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Coordinates one bounded selection, exact authority revalidation, durable intent, and governed dispatch.</summary>
/// <remarks>
/// This service never runs in the background. Caller cancellation is honored before intent. Once intent is durable, dispatch
/// proceeds independently of the caller token while exact generation ownership is renewed at a bounded interval. Renewal is
/// stopped and awaited before terminal persistence. Any renewal failure, ownership loss, exception, cancellation, or lost
/// response is translated to <see cref="TriggerDispatchOutcome.NeedsReview"/> and receives one bounded exact-revision persistence
/// attempt; if that attempt is unavailable, the latest known durable posture is returned rather than fabricated as terminal.
/// </remarks>
public sealed class TriggerWorkerService
{
    private readonly ITriggerWorkerStatePort _state;
    private readonly ITriggerDispatchAuthorizer _authorizer;
    private readonly ITriggerWorkerDispatcher _dispatcher;
    private readonly ITriggerWorkerDispatchReadinessPort _readiness;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a one-shot worker over composition-owned state, authority, and dispatch ports.</summary>
    /// <param name="state">The crash-safe queue ownership and dispatch-state port.</param>
    /// <param name="authorizer">The trusted current-evidence authorizer.</param>
    /// <param name="dispatcher">The governed dispatcher used only after durable intent.</param>
    /// <param name="readiness">The trusted pre-intent readiness boundary.</param>
    /// <param name="timeProvider">The optional composition-owned UTC clock.</param>
    public TriggerWorkerService(
        ITriggerWorkerStatePort state,
        ITriggerDispatchAuthorizer authorizer,
        ITriggerWorkerDispatcher dispatcher,
        ITriggerWorkerDispatchReadinessPort readiness,
        TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Runs at most one eligible trigger entry through the durable dispatch boundary.</summary>
    /// <param name="request">The exact selection inputs.</param>
    /// <param name="cancellationToken">A token honored until durable intent is recorded.</param>
    /// <returns>The selection and final durable posture after the dispatch renewer has stopped.</returns>
    public async Task<TriggerWorkerRunResult> RunOnceAsync(TriggerWorkerRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Selection);
        var selected = await _state.SelectAsync(request.Selection, cancellationToken).ConfigureAwait(false);
        if (selected.Status != TriggerWorkerSelectionStatus.Acquired || selected.Entry?.WorkerLease is not { } lease || selected.Envelope is null)
        {
            return new TriggerWorkerRunResult(selected.Status, null, selected.Entry);
        }

        var readinessRequiresAttention = false;
        TriggerWorkerDispatchReadinessResult? readiness;
        try
        {
            readiness = await _readiness.CheckAsync(selected.Envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var released = await _state.ReleaseAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, UtcNow(), CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, released.Status, released.Entry);
        }
        catch
        {
            readiness = null;
            readinessRequiresAttention = true;
        }

        if (readiness?.Status == TriggerWorkerDispatchReadinessStatus.RetryAfterScheduleFinalization)
        {
            var released = await _state.ReleaseAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, UtcNow(), CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, released.Status, released.Entry);
        }

        readinessRequiresAttention |= readiness?.Status != TriggerWorkerDispatchReadinessStatus.Ready;

        TriggerDispatchAuthorization authorization;
        try
        {
            authorization = await _authorizer.AuthorizeAsync(selected.Envelope, UtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var released = await _state.ReleaseAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, UtcNow(), CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, released.Status, released.Entry);
        }
        catch (Exception exception)
        {
            authorization = new TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus.Unavailable, new string('0', 64), $"Current dispatch evidence was unavailable: {exception.GetType().Name}.");
        }

        try
        {
            ValidateAuthorization(authorization);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            authorization = new TriggerDispatchAuthorization(TriggerDispatchAuthorizationStatus.Unavailable, new string('0', 64), $"Current dispatch evidence was malformed: {exception.GetType().Name}.");
        }
        var operationId = TriggerWorkerRequestHash.ComputeOperationId(selected.Entry.DeliveryId, lease.Generation);
        var requestHash = TriggerWorkerRequestHash.Compute(selected.Envelope, lease, authorization.EvidenceHash);
        var now = UtcNow();
        if (authorization.Status != TriggerDispatchAuthorizationStatus.Authorized)
        {
            var detail = Bound(authorization.Detail);
            var rejection = new TriggerDispatchEvidence(operationId, requestHash, authorization.EvidenceHash, now, TriggerDispatchOutcome.Rejected, now, detail);
            var rejected = await _state.RejectBeforeDispatchAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, rejection, CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, rejected.Status, rejected.Entry);
        }

        var intent = new TriggerDispatchEvidence(operationId, requestHash, authorization.EvidenceHash, now, TriggerDispatchOutcome.IntentRecorded, null, "Durable dispatch intent recorded after exact current-evidence revalidation.");
        TriggerWorkerMutationResult begun;
        try
        {
            begun = await _state.BeginDispatchAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var released = await _state.ReleaseAsync(selected.Entry.DeliveryId, lease.WorkerId, lease.Generation, selected.Entry.Revision, UtcNow(), CancellationToken.None).ConfigureAwait(false);
            return new TriggerWorkerRunResult(selected.Status, released.Status, released.Entry);
        }
        if (begun.Status is not (TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed) || begun.Entry is null)
        {
            return new TriggerWorkerRunResult(selected.Status, begun.Status, begun.Entry);
        }

        using var dispatchCancellation = new CancellationTokenSource();
        using var renewalStop = new CancellationTokenSource();
        var renewalTask = RenewDispatchLeaseAsync(begun.Entry, intent, request.Selection.LeaseDuration, renewalStop.Token);
        (TriggerQueueEntry Entry, TriggerWorkerMutationStatus? FailureStatus, string Detail, DateTimeOffset LastObservedAtUtc)? renewal = null;
        if (renewalTask.IsCompleted)
        {
            renewal = await renewalTask.ConfigureAwait(false);
        }

        var dispatchTask = renewal?.FailureStatus is null
            ? readinessRequiresAttention
                ? Task.FromResult(new TriggerWorkerDispatchResult(
                    TriggerDispatchOutcome.NeedsReview,
                    "Dispatch readiness could not be proved before intent; no governed provider was invoked."))
                : DispatchAsync(selected.Envelope, intent, dispatchCancellation.Token)
            : Task.FromResult(new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, Bound(renewal.Value.Detail)));
        if (renewal is null && await Task.WhenAny(dispatchTask, renewalTask).ConfigureAwait(false) == renewalTask)
        {
            renewal = await renewalTask.ConfigureAwait(false);
            if (renewal.Value.FailureStatus is not null)
            {
                dispatchCancellation.Cancel();
            }
        }

        TriggerWorkerDispatchResult dispatch;
        try
        {
            dispatch = await dispatchTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            dispatch = new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, $"Governed dispatch outcome is ambiguous: {exception.GetType().Name}.");
        }
        finally
        {
            renewalStop.Cancel();
            renewal ??= await renewalTask.ConfigureAwait(false);
        }

        var completionEntry = renewal.Value.Entry;
        var renewalFailure = renewal.Value.FailureStatus;
        var renewalFailureDetail = renewal.Value.Detail;
        var ownershipObservedAtUtc = renewal.Value.LastObservedAtUtc;
        try
        {
            ownershipObservedAtUtc = UtcNow();
            if (renewalFailure is null && !HasLiveDispatchOwnership(completionEntry, intent, lease, ownershipObservedAtUtc))
            {
                renewalFailure = TriggerWorkerMutationStatus.StaleOwner;
                renewalFailureDetail = "Dispatch ownership expired or changed before terminal completion; the governed invocation outcome requires review.";
            }
        }
        catch (Exception exception)
        {
            renewalFailure = TriggerWorkerMutationStatus.Unavailable;
            renewalFailureDetail = $"{renewalFailureDetail} The dispatch ownership clock failed closed before terminal completion: {exception.GetType().Name}.";
        }

        var completedAtUtc = ownershipObservedAtUtc;
        try
        {
            completedAtUtc = UtcNow();
        }
        catch (Exception exception)
        {
            renewalFailure = TriggerWorkerMutationStatus.Unavailable;
            renewalFailureDetail = $"{renewalFailureDetail} The terminal completion clock failed closed: {exception.GetType().Name}.";
        }

        if (renewalFailure is not null)
        {
            dispatch = new TriggerWorkerDispatchResult(TriggerDispatchOutcome.NeedsReview, Bound(renewalFailureDetail));
        }

        var outcome = intent with { Outcome = dispatch.Outcome, OutcomeRecordedAtUtc = completedAtUtc, Detail = Bound(dispatch.Detail), GovernedInvocation = dispatch.GovernedInvocation };
        var completed = await CompleteDispatchFailClosedAsync(completionEntry, lease, intent, outcome).ConfigureAwait(false);
        return new TriggerWorkerRunResult(selected.Status, completed.Status, completed.Entry);
    }

    private async Task<TriggerWorkerMutationResult> CompleteDispatchFailClosedAsync(TriggerQueueEntry knownEntry, TriggerWorkerLease originalLease, TriggerDispatchEvidence intent, TriggerDispatchEvidence outcome)
    {
        TriggerWorkerMutationResult? first = null;
        string failure;
        try
        {
            first = await _state.CompleteDispatchAsync(knownEntry.DeliveryId, originalLease.WorkerId, originalLease.Generation, knownEntry.Revision, outcome, CancellationToken.None).ConfigureAwait(false);
            if ((first.Status is TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed) && first.Entry is not null)
            {
                return first;
            }

            failure = $"status {first.Status}";
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name;
        }

        var latest = knownEntry;
        if (first?.Entry is { } observed)
        {
            if (!IsExactDispatchingEntry(observed, knownEntry, originalLease, intent))
            {
                return first;
            }

            latest = observed;
        }

        var needsReviewRecordedAtUtc = outcome.OutcomeRecordedAtUtc!.Value;
        try
        {
            var observedAtUtc = UtcNow();
            if (observedAtUtc > needsReviewRecordedAtUtc)
            {
                needsReviewRecordedAtUtc = observedAtUtc;
            }
        }
        catch (Exception exception)
        {
            failure = $"{failure}; retry clock {exception.GetType().Name}";
        }

        var needsReview = intent with
        {
            Outcome = TriggerDispatchOutcome.NeedsReview,
            OutcomeRecordedAtUtc = needsReviewRecordedAtUtc,
            Detail = Bound($"Terminal dispatch completion failed closed with {failure}; the governed invocation outcome requires review."),
            GovernedInvocation = null
        };
        try
        {
            return await _state.CompleteDispatchAsync(latest.DeliveryId, originalLease.WorkerId, originalLease.Generation, latest.Revision, needsReview, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.Unavailable, first?.QueueGeneration ?? 0, latest);
        }
    }

    private static bool IsExactDispatchingEntry(TriggerQueueEntry candidate, TriggerQueueEntry knownEntry, TriggerWorkerLease originalLease, TriggerDispatchEvidence intent)
    {
        return candidate.DeliveryId.Equals(knownEntry.DeliveryId)
            && candidate.State == TriggerQueueEntryState.Dispatching
            && candidate.Revision >= knownEntry.Revision
            && candidate.Dispatch == intent
            && candidate.WorkerLease is { ReleasedAtUtc: null } lease
            && string.Equals(lease.WorkerId, originalLease.WorkerId, StringComparison.Ordinal)
            && lease.Generation == originalLease.Generation
            && lease.AcquiredAtUtc == originalLease.AcquiredAtUtc;
    }

    private async Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken)
    {
        var dispatch = await _dispatcher.DispatchAsync(envelope, intent, cancellationToken).ConfigureAwait(false);
        ValidateDispatch(dispatch, envelope, intent);
        return dispatch;
    }

    private async Task<(TriggerQueueEntry Entry, TriggerWorkerMutationStatus? FailureStatus, string Detail, DateTimeOffset LastObservedAtUtc)> RenewDispatchLeaseAsync(TriggerQueueEntry begun, TriggerDispatchEvidence intent, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        var current = begun;
        var lastObservedAtUtc = intent.IntentRecordedAtUtc;
        while (true)
        {
            try
            {
                var observedAtUtc = UtcNow();
                lastObservedAtUtc = observedAtUtc;
                if (current.WorkerLease is not { ReleasedAtUtc: null } currentLease || observedAtUtc >= currentLease.ExpiresAtUtc)
                {
                    return (current, TriggerWorkerMutationStatus.StaleOwner, "Dispatch ownership expired before its next renewal could be scheduled; the governed invocation outcome requires review.", lastObservedAtUtc);
                }

                await Task.Delay(RenewalInterval(currentLease, observedAtUtc, leaseDuration), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (current, null, "Dispatch lease renewal stopped before terminal completion.", lastObservedAtUtc);
            }
            catch (Exception exception)
            {
                return (current, TriggerWorkerMutationStatus.Unavailable, $"Dispatch lease renewal scheduling failed closed before the governed invocation settled: {exception.GetType().Name}.", lastObservedAtUtc);
            }

            TriggerWorkerMutationResult renewed;
            var renewedAtUtc = lastObservedAtUtc;
            try
            {
                renewedAtUtc = UtcNow();
                lastObservedAtUtc = renewedAtUtc;
                var lease = current.WorkerLease!;
                renewed = await _state.RenewAsync(current.DeliveryId, lease.WorkerId, lease.Generation, current.Revision, renewedAtUtc, leaseDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (current, null, "Dispatch lease renewal stopped before terminal completion.", lastObservedAtUtc);
            }
            catch (Exception exception)
            {
                return (current, TriggerWorkerMutationStatus.Unavailable, $"Dispatch lease renewal failed closed before the governed invocation settled: {exception.GetType().Name}.", lastObservedAtUtc);
            }

            bool exactRenewal;
            try
            {
                exactRenewal = renewed.Status is TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed
                    && renewed.Entry is { } exactEntry
                    && IsExactRenewal(current, exactEntry, intent, renewedAtUtc, leaseDuration);
            }
            catch (Exception exception)
            {
                return (renewed.Entry ?? current, TriggerWorkerMutationStatus.Unavailable, $"Dispatch lease renewal validation failed closed before the governed invocation settled: {exception.GetType().Name}.", lastObservedAtUtc);
            }

            if (exactRenewal)
            {
                current = renewed.Entry!;
                continue;
            }

            var failureStatus = renewed.Status is TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed ? TriggerWorkerMutationStatus.InvalidState : renewed.Status;
            return (renewed.Entry ?? current, failureStatus, $"Dispatch lease renewal lost exact durable ownership with status {failureStatus}; the governed invocation outcome requires review.", lastObservedAtUtc);
        }
    }

    private static bool IsExactRenewal(TriggerQueueEntry prior, TriggerQueueEntry renewed, TriggerDispatchEvidence intent, DateTimeOffset renewedAtUtc, TimeSpan leaseDuration)
    {
        var priorLease = prior.WorkerLease;
        var renewedLease = renewed.WorkerLease;
        return renewed.DeliveryId.Equals(prior.DeliveryId)
            && renewed.State == TriggerQueueEntryState.Dispatching
            && renewed.Revision == checked(prior.Revision + 1)
            && renewed.Dispatch == intent
            && priorLease is not null
            && renewedLease is not null
            && string.Equals(renewedLease.WorkerId, priorLease.WorkerId, StringComparison.Ordinal)
            && renewedLease.Generation == priorLease.Generation
            && renewedLease.AcquiredAtUtc == priorLease.AcquiredAtUtc
            && renewedLease.ReleasedAtUtc is null
            && renewedLease.RenewalCount == checked(priorLease.RenewalCount + 1)
            && renewedLease.ExpiresAtUtc == renewedAtUtc + leaseDuration;
    }

    private static bool HasLiveDispatchOwnership(TriggerQueueEntry entry, TriggerDispatchEvidence intent, TriggerWorkerLease originalLease, DateTimeOffset observedAtUtc)
    {
        return entry.State == TriggerQueueEntryState.Dispatching
            && entry.Dispatch == intent
            && entry.WorkerLease is { } lease
            && string.Equals(lease.WorkerId, originalLease.WorkerId, StringComparison.Ordinal)
            && lease.Generation == originalLease.Generation
            && lease.ReleasedAtUtc is null
            && observedAtUtc < lease.ExpiresAtUtc;
    }

    private static TimeSpan RenewalInterval(TriggerWorkerLease lease, DateTimeOffset observedAtUtc, TimeSpan leaseDuration)
    {
        var remainingTicks = Math.Min((lease.ExpiresAtUtc - observedAtUtc).Ticks, leaseDuration.Ticks);
        return TimeSpan.FromTicks(Math.Max(1, remainingTicks / 2));
    }

    private DateTimeOffset UtcNow()
    {
        var now = _timeProvider.GetUtcNow();
        return now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
    }

    private static void ValidateAuthorization(TriggerDispatchAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!Enum.IsDefined(authorization.Status) || !IsHash(authorization.EvidenceHash) || string.IsNullOrWhiteSpace(authorization.Detail))
        {
            throw new InvalidOperationException("The dispatch authorizer returned malformed current evidence.");
        }
    }

    private static void ValidateDispatch(TriggerWorkerDispatchResult dispatch, TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var requiresReceipt = dispatch.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal;
        var hasExactLoopReferenceHash = TriggerLoopReferenceHash.TryCompute(envelope.Loop, out var loopReferenceHash, out _);
        if (dispatch.Outcome is not (TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal or TriggerDispatchOutcome.Rejected or TriggerDispatchOutcome.NeedsReview)
            || string.IsNullOrWhiteSpace(dispatch.Detail)
            || requiresReceipt != (dispatch.GovernedInvocation is not null)
            || requiresReceipt && !hasExactLoopReferenceHash
            || dispatch.GovernedInvocation is { } governed && (!string.Equals(governed.OperationId, intent.OperationId, StringComparison.Ordinal)
                || !IsArtifactId(governed.RunId, TriggerWorkerLimits.MaxGovernedRunIdCharacters)
                || !IsHash(governed.AdmissionRequestHash)
                || !string.Equals(governed.LoopId, envelope.Loop.LoopId, StringComparison.Ordinal)
                || !IsHash(governed.LoopReferenceHash)
                || !string.Equals(governed.LoopReferenceHash, loopReferenceHash, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The governed dispatcher returned an unsupported outcome.");
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsArtifactId(string value, int maximumLength) => !string.IsNullOrEmpty(value) && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string Bound(string detail)
    {
        var normalized = string.IsNullOrWhiteSpace(detail) ? "No outcome detail was supplied." : detail.Trim();
        return normalized.Length <= TriggerWorkerLimits.MaxOutcomeDetailCharacters ? normalized : normalized[..TriggerWorkerLimits.MaxOutcomeDetailCharacters];
    }
}
