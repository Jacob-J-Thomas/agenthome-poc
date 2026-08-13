using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers.Models;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Persists one bounded schema-version-1 trigger queue ledger with cross-process atomic mutation.</summary>
/// <remarks>
/// The store records admission evidence plus worker selection, lease, and dispatch state. It has no provider, actuator, or
/// execution dependency and never invokes provider or actuator code.
/// Caller cancellation is honored through the final pre-staging check. Atomic rename is the commit boundary; an exception after
/// publication is an ambiguous caller outcome, and exact retry resolves it by replaying the durable entry.
/// Proved dispatch completion reads a composition-owned UTC clock while holding the mutation lock so stale caller timestamps
/// cannot extend live ownership. The caller timestamp remains the persisted event time and exact replay binding.
/// Terminal evidence is retained without automatic pruning and remains subject to explicit retained count and byte bounds.
/// Unix cleanup uses authenticated tombstones because pathname unlink cannot condition deletion on file identity. Their public
/// quota is inspectable in snapshots, and every mutation reserves its worst-case tombstone use before staging.
/// </remarks>
public sealed class TriggerQueueStore : ITriggerQueueMutationPort, ITriggerQueueQueryPort, ITriggerQueueCancellationPort, ITriggerDeliveryAdmissionHistoryPort, ITriggerWorkerStatePort
{
    private readonly TriggerQueueQuota _quota;
    private readonly TriggerQueueArtifactGuard _guard;
    private readonly int _maximumLedgerBytes;
    private readonly ITriggerQueueDurabilityObserver _observer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a workspace-scoped queue with composition-owned bounds and an optional durability observer.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="quota">The schema-version-1 quota, or conservative defaults.</param>
    /// <param name="observer">An optional diagnostics or crash-test observer.</param>
    /// <param name="timeProvider">The composition-owned UTC clock used for under-lock dispatch-completion liveness checks.</param>
    public TriggerQueueStore(WorkspacePaths paths, TriggerQueueQuota? quota = null, ITriggerQueueDurabilityObserver? observer = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _quota = quota ?? TriggerQueueQuota.Default;
        TriggerQueueQuotaValidator.Validate(_quota);
        _observer = observer ?? NullTriggerQueueDurabilityObserver.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var queueRoot = paths.AgentFile(Path.Combine("triggers", "queue"));
        _guard = new TriggerQueueArtifactGuard(paths.RootPath, queueRoot, _quota.MaxDurabilityTombstones);
        _maximumLedgerBytes = checked((int)Math.Min(int.MaxValue, _quota.MaxRetainedBytes + (long)_quota.MaxRetainedEntries * 2_048 + 4_096));
    }

    /// <inheritdoc />
    public async Task<TriggerQueueAdmissionResult> CommitAsync(TriggerQueueCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCommitRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var (swept, sweepChanged) = SweepExpired(ledger, request.RecordedAtUtc);
            var deliveryMatch = swept.Entries.SingleOrDefault(entry => entry.Envelope.DeliveryId.Equals(request.Envelope.DeliveryId));
            var deduplicationMatch = swept.Entries.SingleOrDefault(entry => entry.Envelope.DeduplicationId.Equals(request.Envelope.DeduplicationId));
            if (deliveryMatch is not null || deduplicationMatch is not null)
            {
                var existing = deliveryMatch ?? deduplicationMatch!;
                if (deliveryMatch is not null && deduplicationMatch is not null && !ReferenceEquals(deliveryMatch, deduplicationMatch))
                {
                    await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                    return Result(TriggerQueueAdmissionStatus.Rejected, TriggerQueueAdmissionReason.IdentityConflict, request, ToEntry(existing));
                }

                if (!IsReplay(request, existing))
                {
                    await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                    return Result(TriggerQueueAdmissionStatus.Rejected, TriggerQueueAdmissionReason.IdentityConflict, request, ToEntry(existing));
                }

                if (existing.State == TriggerQueueEntryState.Queued
                    && !IsQueueAccepted(request.AdmissionStatus)
                    && string.Equals(request.CanonicalEnvelopeHash, existing.CanonicalEnvelopeHash, StringComparison.Ordinal))
                {
                    var revoked = existing with
                    {
                        Receipt = request.Receipt,
                        AdmissionStatus = request.AdmissionStatus,
                        AdmissionReason = request.AdmissionReason,
                        State = TriggerQueueEntryState.Rejected,
                        TerminalReason = TriggerQueueTerminalReason.AdmissionRejected,
                        Revision = checked(existing.Revision + 1),
                        TerminalAtUtc = request.RecordedAtUtc
                    };
                    swept = Replace(swept, existing, revoked);
                    await PersistAsync(swept, identity, mutationLock, cancellationToken).ConfigureAwait(false);
                    var revokedReason = request.AdmissionStatus == TriggerAdmissionStatus.Conflicting ? TriggerQueueAdmissionReason.IdentityConflict : TriggerQueueAdmissionReason.AdmissionRejected;
                    return Result(TriggerQueueAdmissionStatus.Rejected, revokedReason, request, ToEntry(revoked));
                }

                if (existing.Receipt is null && request.Receipt is not null && existing.Envelope.DeliveryId.Equals(request.Envelope.DeliveryId))
                {
                    var promoted = existing with { Receipt = request.Receipt, AdmissionStatus = request.AdmissionStatus, AdmissionReason = request.AdmissionReason, Revision = existing.Revision + 1 };
                    swept = Replace(swept, existing, promoted);
                    await PersistAsync(swept, identity, mutationLock, cancellationToken).ConfigureAwait(false);
                    return Result(TriggerQueueAdmissionStatus.Replayed, TriggerQueueAdmissionReason.ExactReplay, request, ToEntry(promoted));
                }

                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return Result(TriggerQueueAdmissionStatus.Replayed, TriggerQueueAdmissionReason.ExactReplay, request, ToEntry(existing));
            }

            if (!TriggerDeliveryJson.TrySerialize(request.Envelope, out var canonicalEnvelope, out _))
            {
                throw new ArgumentException("The commit request does not contain a valid canonical envelope.", nameof(request));
            }

            var retainedEntries = swept.Entries.Count;
            var retainedBytes = swept.Entries.Sum(ReservedEntryBytes);
            if (retainedEntries >= _quota.MaxRetainedEntries)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return Result(TriggerQueueAdmissionStatus.Backpressured, TriggerQueueAdmissionReason.RetainedEvidenceExceeded, request, null);
            }

            var queueAccepted = IsQueueAccepted(request.AdmissionStatus);
            var state = TriggerQueueEntryState.Rejected;
            var terminalReason = TriggerQueueTerminalReason.AdmissionRejected;
            TriggerQueueAdmissionStatus status;
            TriggerQueueAdmissionReason reason;
            var provisional = new TriggerQueueLedgerEntry(request.Envelope, canonicalEnvelope!, request.Receipt, request.AdmissionStatus, request.AdmissionReason, request.CanonicalEnvelopeHash, request.Priority, TriggerQueueEntryState.Queued, TriggerQueueTerminalReason.None, 1, request.RecordedAtUtc, null);
            if (!queueAccepted)
            {
                status = TriggerQueueAdmissionStatus.Rejected;
                reason = request.AdmissionStatus == TriggerAdmissionStatus.Conflicting ? TriggerQueueAdmissionReason.IdentityConflict : TriggerQueueAdmissionReason.AdmissionRejected;
            }
            else
            {
                var queuedEntries = swept.Entries.Count(IsNonterminal);
                var queuedBytes = swept.Entries.Where(IsNonterminal).Sum(ReservedQueuedEntryBytes);
                var loopEntries = swept.Entries.Count(entry => IsNonterminal(entry) && string.Equals(entry.Envelope.Loop.LoopId, request.Envelope.Loop.LoopId, StringComparison.Ordinal));
                if (queuedEntries >= _quota.MaxQueuedEntries)
                {
                    state = TriggerQueueEntryState.Backpressured;
                    terminalReason = TriggerQueueTerminalReason.QueueCountExceeded;
                    status = TriggerQueueAdmissionStatus.Backpressured;
                    reason = TriggerQueueAdmissionReason.QueueCountExceeded;
                }
                else if (queuedBytes + ReservedQueuedEntryBytes(provisional) > _quota.MaxQueuedBytes)
                {
                    state = TriggerQueueEntryState.Backpressured;
                    terminalReason = TriggerQueueTerminalReason.QueueBytesExceeded;
                    status = TriggerQueueAdmissionStatus.Backpressured;
                    reason = TriggerQueueAdmissionReason.QueueBytesExceeded;
                }
                else if (loopEntries >= _quota.MaxQueuedEntriesPerLoop)
                {
                    state = TriggerQueueEntryState.Backpressured;
                    terminalReason = TriggerQueueTerminalReason.LoopQuotaExceeded;
                    status = TriggerQueueAdmissionStatus.Backpressured;
                    reason = TriggerQueueAdmissionReason.LoopQuotaExceeded;
                }
                else
                {
                    state = TriggerQueueEntryState.Queued;
                    terminalReason = TriggerQueueTerminalReason.None;
                    status = TriggerQueueAdmissionStatus.Queued;
                    reason = TriggerQueueAdmissionReason.Enqueued;
                }
            }

            DateTimeOffset? terminalAtUtc = state == TriggerQueueEntryState.Queued ? null : request.RecordedAtUtc;
            var created = new TriggerQueueLedgerEntry(request.Envelope, canonicalEnvelope!, request.Receipt, request.AdmissionStatus, request.AdmissionReason, request.CanonicalEnvelopeHash, request.Priority, state, terminalReason, 1, request.RecordedAtUtc, terminalAtUtc);
            var createdReservedBytes = ReservedEntryBytes(created);
            if (createdReservedBytes > _quota.MaxEntryBytes)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return Result(TriggerQueueAdmissionStatus.Backpressured, TriggerQueueAdmissionReason.EntryBytesExceeded, request, null);
            }

            if (retainedBytes + createdReservedBytes > _quota.MaxRetainedBytes)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return Result(TriggerQueueAdmissionStatus.Backpressured, TriggerQueueAdmissionReason.RetainedEvidenceExceeded, request, null);
            }

            var updated = swept with { Entries = [.. swept.Entries, created] };
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(updated, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return Result(status, reason, request, ToEntry(created));
        }
        catch (TriggerQueuePersistenceBackpressureException)
        {
            return Result(TriggerQueueAdmissionStatus.Backpressured, TriggerQueueAdmissionReason.DurabilityTombstoneCapacityExceeded, request, null);
        }
    }

    /// <inheritdoc />
    public async Task<TriggerQueueSnapshot> GetSnapshotAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default)
    {
        EnsureUtc(observedAtUtc, nameof(observedAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
        var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
        var (swept, changed) = SweepExpired(ledger, observedAtUtc);
        var dispatchSwept = SweepAbandonedDispatches(swept, observedAtUtc);
        changed |= !ReferenceEquals(dispatchSwept, swept);
        swept = dispatchSwept;
        if (changed)
        {
            if (!CanPersist(identity))
            {
                return ToSnapshot(ledger, identity);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(swept, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            (swept, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
        }

        return ToSnapshot(swept, identity);
    }

    /// <inheritdoc />
    public async Task<TriggerWorkerSelectionResult> SelectAsync(TriggerWorkerSelectionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateSelectionRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            if (ledger.Generation != request.ExpectedQueueGeneration)
            {
                return new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.RevisionConflict, ledger.Generation, null, null);
            }

            if (IsClockRollback(ledger, request.ObservedAtUtc))
            {
                return new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.ClockRollback, ledger.Generation, null, null);
            }

            var (swept, _) = SweepExpired(ledger, request.ObservedAtUtc);
            swept = SweepAbandonedDispatches(swept, request.ObservedAtUtc);
            var candidates = swept.Entries
                .Where(entry => IsSelectable(entry, request.ObservedAtUtc))
                .OrderBy(entry => ToEntry(entry).OrderKey, Comparer<TriggerQueueOrderKey>.Create(TriggerQueueOrdering.Compare))
                .ToArray();
            var selected = ApplyFairness(candidates, request.RecentLoopIds, request.MaxConsecutiveSelectionsPerLoop);
            if (selected is null)
            {
                if (!ReferenceEquals(swept, ledger) || swept.LastWorkerObservedAtUtc != request.ObservedAtUtc)
                {
                    swept = swept with { LastWorkerObservedAtUtc = request.ObservedAtUtc };
                    await PersistAsync(swept, identity, mutationLock, cancellationToken).ConfigureAwait(false);
                    return new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.Empty, ledger.Generation + 1, null, null);
                }

                return new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.Empty, ledger.Generation, null, null);
            }

            var leaseGeneration = checked((selected.WorkerLease?.Generation ?? 0) + 1);
            var lease = new TriggerWorkerLease(request.WorkerId, leaseGeneration, request.ObservedAtUtc, AddLeaseDuration(request.ObservedAtUtc, request.LeaseDuration), 0);
            var owned = selected with { State = TriggerQueueEntryState.WorkerOwned, TerminalReason = TriggerQueueTerminalReason.None, Revision = checked(selected.Revision + 1), TerminalAtUtc = null, WorkerLease = lease, Dispatch = null };
            var updated = Replace(swept, selected, owned) with { LastWorkerObservedAtUtc = request.ObservedAtUtc };
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(updated, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.Acquired, ledger.Generation + 1, ToEntry(owned), owned.Envelope);
        }
        catch (TriggerQueuePersistenceBackpressureException)
        {
            return new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.Unavailable, request.ExpectedQueueGeneration, null, null);
        }
    }

    /// <inheritdoc />
    public Task<TriggerWorkerMutationResult> RenewAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset renewedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        ValidateWorkerMutation(deliveryId, workerId, leaseGeneration, expectedRevision, renewedAtUtc);
        ValidateLeaseDuration(leaseDuration);
        return MutateWorkerAsync(deliveryId, workerId, leaseGeneration, expectedRevision, renewedAtUtc, cancellationToken, entry =>
        {
            if (entry.State is not (TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching) || entry.WorkerLease is not { } lease || lease.ReleasedAtUtc is not null)
            {
                return (TriggerWorkerMutationStatus.InvalidState, entry);
            }

            if (renewedAtUtc >= lease.ExpiresAtUtc
                || lease.RenewalCount >= TriggerWorkerLeaseRenewalPolicy.GetMaxLeaseRenewals(leaseDuration)
                || !TryAddLeaseDuration(renewedAtUtc, leaseDuration, out var renewedExpiry)
                || renewedExpiry - lease.AcquiredAtUtc > TriggerWorkerLimits.MaxLeaseOwnershipDuration)
            {
                return (TriggerWorkerMutationStatus.StaleOwner, entry);
            }

            var renewed = entry with { Revision = checked(entry.Revision + 1), WorkerLease = lease with { ExpiresAtUtc = renewedExpiry, RenewalCount = lease.RenewalCount + 1 } };
            return (TriggerWorkerMutationStatus.Committed, renewed);
        });
    }

    /// <inheritdoc />
    public Task<TriggerWorkerMutationResult> ReleaseAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset releasedAtUtc, CancellationToken cancellationToken = default)
    {
        ValidateWorkerMutation(deliveryId, workerId, leaseGeneration, expectedRevision, releasedAtUtc);
        return MutateWorkerAsync(deliveryId, workerId, leaseGeneration, expectedRevision, releasedAtUtc, cancellationToken, entry =>
        {
            if (entry.State != TriggerQueueEntryState.WorkerOwned || entry.WorkerLease is not { } lease || lease.ReleasedAtUtc is not null)
            {
                return (TriggerWorkerMutationStatus.InvalidState, entry);
            }

            if (releasedAtUtc >= lease.ExpiresAtUtc)
            {
                return (TriggerWorkerMutationStatus.StaleOwner, entry);
            }

            var released = entry with { State = TriggerQueueEntryState.Queued, Revision = checked(entry.Revision + 1), WorkerLease = lease with { ReleasedAtUtc = releasedAtUtc } };
            return (TriggerWorkerMutationStatus.Committed, released);
        });
    }

    /// <inheritdoc />
    public Task<TriggerWorkerMutationResult> BeginDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateWorkerMutation(deliveryId, workerId, leaseGeneration, expectedRevision, intent.IntentRecordedAtUtc);
        ValidateDispatchEvidence(intent, TriggerDispatchOutcome.IntentRecorded);
        return MutateWorkerAsync(deliveryId, workerId, leaseGeneration, expectedRevision, intent.IntentRecordedAtUtc, cancellationToken, entry =>
        {
            if (entry.State == TriggerQueueEntryState.Dispatching && entry.Dispatch == intent)
            {
                return (TriggerWorkerMutationStatus.Replayed, entry);
            }

            if (entry.State != TriggerQueueEntryState.WorkerOwned || entry.WorkerLease is not { } lease || intent.IntentRecordedAtUtc >= lease.ExpiresAtUtc)
            {
                return (TriggerWorkerMutationStatus.InvalidState, entry);
            }

            return (TriggerWorkerMutationStatus.Committed, entry with { State = TriggerQueueEntryState.Dispatching, Revision = checked(entry.Revision + 1), Dispatch = intent });
        });
    }

    /// <inheritdoc />
    public Task<TriggerWorkerMutationResult> RejectBeforeDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence rejection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rejection);
        ValidateWorkerMutation(deliveryId, workerId, leaseGeneration, expectedRevision, rejection.IntentRecordedAtUtc);
        ValidateDispatchEvidence(rejection, TriggerDispatchOutcome.Rejected);
        return MutateWorkerAsync(deliveryId, workerId, leaseGeneration, expectedRevision, rejection.IntentRecordedAtUtc, cancellationToken, entry =>
        {
            if (entry.State == TriggerQueueEntryState.DispatchRejected && entry.Dispatch == rejection)
            {
                return (TriggerWorkerMutationStatus.Replayed, entry);
            }

            if (entry.State != TriggerQueueEntryState.WorkerOwned || entry.WorkerLease is not { } lease || rejection.IntentRecordedAtUtc >= lease.ExpiresAtUtc)
            {
                return (TriggerWorkerMutationStatus.InvalidState, entry);
            }

            var releasedLease = entry.WorkerLease! with { ReleasedAtUtc = rejection.OutcomeRecordedAtUtc };
            var rejected = entry with { State = TriggerQueueEntryState.DispatchRejected, TerminalReason = TriggerQueueTerminalReason.DispatchRejected, Revision = checked(entry.Revision + 1), TerminalAtUtc = rejection.OutcomeRecordedAtUtc, WorkerLease = releasedLease, Dispatch = rejection };
            return (TriggerWorkerMutationStatus.Committed, rejected);
        });
    }

    /// <inheritdoc />
    public Task<TriggerWorkerMutationResult> CompleteDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ValidateWorkerMutation(deliveryId, workerId, leaseGeneration, expectedRevision, outcome.OutcomeRecordedAtUtc ?? default);
        ValidateDispatchEvidence(outcome, outcome.Outcome);
        if (outcome.Outcome is not (TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal or TriggerDispatchOutcome.Rejected or TriggerDispatchOutcome.NeedsReview))
        {
            throw new ArgumentException("A terminal dispatch outcome is required.", nameof(outcome));
        }

        return MutateWorkerAsync(deliveryId, workerId, leaseGeneration, expectedRevision, outcome.OutcomeRecordedAtUtc!.Value, cancellationToken, entry =>
        {
            if (entry.Dispatch == outcome && entry.State is TriggerQueueEntryState.Dispatched or TriggerQueueEntryState.DispatchRejected or TriggerQueueEntryState.NeedsReview)
            {
                return (TriggerWorkerMutationStatus.Replayed, entry);
            }

            if (entry.State != TriggerQueueEntryState.Dispatching || entry.Dispatch is null || !SameDispatchBinding(entry.Dispatch, outcome))
            {
                return (TriggerWorkerMutationStatus.InvalidState, entry);
            }

            DateTimeOffset trustedObservedAtUtc;
            try
            {
                var now = _timeProvider.GetUtcNow();
                trustedObservedAtUtc = now.Offset == TimeSpan.Zero ? now : now.ToUniversalTime();
            }
            catch
            {
                return (TriggerWorkerMutationStatus.Unavailable, entry);
            }

            if (outcome.OutcomeRecordedAtUtc!.Value > trustedObservedAtUtc)
            {
                return (TriggerWorkerMutationStatus.ClockRollback, entry);
            }

            if (outcome.Outcome != TriggerDispatchOutcome.NeedsReview
                && (entry.WorkerLease is not { ReleasedAtUtc: null } liveLease
                    || outcome.OutcomeRecordedAtUtc.Value >= liveLease.ExpiresAtUtc
                    || trustedObservedAtUtc >= liveLease.ExpiresAtUtc))
            {
                return (TriggerWorkerMutationStatus.StaleOwner, entry);
            }

            if (!IsGovernedOutcomeBound(entry, outcome))
            {
                return (TriggerWorkerMutationStatus.InvalidState, entry);
            }

            var (state, reason) = outcome.Outcome switch
            {
                TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal => (TriggerQueueEntryState.Dispatched, TriggerQueueTerminalReason.Dispatched),
                TriggerDispatchOutcome.Rejected => (TriggerQueueEntryState.DispatchRejected, TriggerQueueTerminalReason.DispatchRejected),
                _ => (TriggerQueueEntryState.NeedsReview, TriggerQueueTerminalReason.AmbiguousDispatch)
            };
            var releasedLease = entry.WorkerLease! with { ReleasedAtUtc = outcome.OutcomeRecordedAtUtc };
            return (TriggerWorkerMutationStatus.Committed, entry with { State = state, TerminalReason = reason, Revision = checked(entry.Revision + 1), TerminalAtUtc = outcome.OutcomeRecordedAtUtc, WorkerLease = releasedLease, Dispatch = outcome });
        });
    }

    /// <inheritdoc />
    public async Task<TriggerQueueCancellationResult> CancelAsync(TriggerDeliveryId deliveryId, long expectedRevision, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        if (expectedRevision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }

        EnsureUtc(cancelledAtUtc, nameof(cancelledAtUtc));
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var (swept, sweepChanged) = SweepExpired(ledger, cancelledAtUtc);
            var existing = swept.Entries.SingleOrDefault(entry => entry.Envelope.DeliveryId.Equals(deliveryId));
            if (existing is null)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.NotFound, null);
            }

            if (existing.State is not (TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching))
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.AlreadyTerminal, ToEntry(existing));
            }

            if (existing.Revision != expectedRevision)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.RevisionConflict, ToEntry(existing));
            }

            TriggerQueueLedgerEntry cancelled;
            if (existing.State == TriggerQueueEntryState.Dispatching && existing.Dispatch is { } dispatch)
            {
                cancelled = existing with
                {
                    State = TriggerQueueEntryState.NeedsReview,
                    TerminalReason = TriggerQueueTerminalReason.AmbiguousDispatch,
                    Revision = existing.Revision + 1,
                    TerminalAtUtc = cancelledAtUtc,
                    WorkerLease = existing.WorkerLease! with { ReleasedAtUtc = cancelledAtUtc },
                    Dispatch = dispatch with { Outcome = TriggerDispatchOutcome.NeedsReview, OutcomeRecordedAtUtc = cancelledAtUtc, Detail = "Cancellation raced durable dispatch intent; provider dispatch may have occurred." }
                };
            }
            else
            {
                var releasedLease = existing.WorkerLease is { } lease ? lease with { ReleasedAtUtc = cancelledAtUtc } : null;
                cancelled = existing with { State = TriggerQueueEntryState.Cancelled, TerminalReason = TriggerQueueTerminalReason.Cancelled, Revision = existing.Revision + 1, TerminalAtUtc = cancelledAtUtc, WorkerLease = releasedLease };
            }
            var updated = Replace(swept, existing, cancelled);
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(updated, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.Cancelled, ToEntry(cancelled));
        }
        catch (TriggerQueuePersistenceBackpressureException)
        {
            return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.PersistenceBackpressured, null);
        }
    }

    /// <inheritdoc />
    public async Task<TriggerDeliveryAdmissionHistoryLookupResult> FindAsync(TriggerDeliveryId deliveryId, TriggerDeduplicationId deduplicationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        ArgumentNullException.ThrowIfNull(deduplicationId);
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (ledger, _) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var deliveryMatch = ledger.Entries.SingleOrDefault(entry => entry.Envelope.DeliveryId.Equals(deliveryId));
            var deduplicationMatch = ledger.Entries.SingleOrDefault(entry => entry.Envelope.DeduplicationId.Equals(deduplicationId));
            return new TriggerDeliveryAdmissionHistoryLookupResult(
                TriggerDeliveryAdmissionHistoryLookupStatus.Available,
                ToHistory(deliveryMatch),
                ToHistory(deduplicationMatch));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new TriggerDeliveryAdmissionHistoryLookupResult(TriggerDeliveryAdmissionHistoryLookupStatus.Unavailable, null, null);
        }
    }

    private async Task<TriggerWorkerMutationResult> MutateWorkerAsync(
        TriggerDeliveryId deliveryId,
        string workerId,
        long leaseGeneration,
        long expectedRevision,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken,
        Func<TriggerQueueLedgerEntry, (TriggerWorkerMutationStatus Status, TriggerQueueLedgerEntry Entry)> transition)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var mutationLock = await _guard.AcquireMutationLockAsync(_observer, cancellationToken).ConfigureAwait(false);
            var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            if (IsClockRollback(ledger, observedAtUtc))
            {
                return new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.ClockRollback, ledger.Generation, null);
            }

            var existing = ledger.Entries.SingleOrDefault(entry => entry.Envelope.DeliveryId.Equals(deliveryId));
            if (existing is null)
            {
                return new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.NotFound, ledger.Generation, null);
            }

            if (existing.Revision != expectedRevision)
            {
                return new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.RevisionConflict, ledger.Generation, ToEntry(existing));
            }

            if (existing.WorkerLease is not { } lease || !string.Equals(lease.WorkerId, workerId, StringComparison.Ordinal) || lease.Generation != leaseGeneration)
            {
                return new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.StaleOwner, ledger.Generation, ToEntry(existing));
            }

            var (status, replacement) = transition(existing);
            if (status is not (TriggerWorkerMutationStatus.Committed or TriggerWorkerMutationStatus.Replayed))
            {
                return new TriggerWorkerMutationResult(status, ledger.Generation, ToEntry(replacement));
            }

            if (status == TriggerWorkerMutationStatus.Replayed)
            {
                return new TriggerWorkerMutationResult(status, ledger.Generation, ToEntry(replacement));
            }

            var updated = Replace(ledger, existing, replacement) with { LastWorkerObservedAtUtc = observedAtUtc };
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(updated, identity, mutationLock, cancellationToken).ConfigureAwait(false);
            return new TriggerWorkerMutationResult(status, ledger.Generation + 1, ToEntry(replacement));
        }
        catch (TriggerQueuePersistenceBackpressureException)
        {
            return new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.Unavailable, 0, null);
        }
    }

    private static TriggerQueueLedger SweepAbandonedDispatches(TriggerQueueLedger ledger, DateTimeOffset observedAtUtc)
    {
        var changed = false;
        var entries = ledger.Entries.Select(entry =>
        {
            if (entry.State != TriggerQueueEntryState.Dispatching || entry.WorkerLease is not { } lease || observedAtUtc < lease.ExpiresAtUtc || entry.Dispatch is not { } dispatch)
            {
                return entry;
            }

            changed = true;
            return entry with
            {
                State = TriggerQueueEntryState.NeedsReview,
                TerminalReason = TriggerQueueTerminalReason.AmbiguousDispatch,
                Revision = checked(entry.Revision + 1),
                TerminalAtUtc = observedAtUtc,
                WorkerLease = lease with { ReleasedAtUtc = observedAtUtc },
                Dispatch = dispatch with { Outcome = TriggerDispatchOutcome.NeedsReview, OutcomeRecordedAtUtc = observedAtUtc, Detail = "Dispatch ownership expired before a terminal provider outcome was persisted." }
            };
        }).ToArray();
        return changed ? ledger with { Entries = entries } : ledger;
    }

    private static TriggerQueueLedgerEntry? ApplyFairness(IReadOnlyList<TriggerQueueLedgerEntry> candidates, IReadOnlyList<string> recentLoopIds, int maximumConsecutive)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (recentLoopIds.Count < maximumConsecutive)
        {
            return candidates[0];
        }

        var latest = recentLoopIds[^1];
        var suffix = recentLoopIds.Reverse().TakeWhile(loopId => string.Equals(loopId, latest, StringComparison.Ordinal)).Count();
        return suffix < maximumConsecutive ? candidates[0] : candidates.FirstOrDefault(candidate => !string.Equals(candidate.Envelope.Loop.LoopId, latest, StringComparison.Ordinal)) ?? candidates[0];
    }

    private static bool IsSelectable(TriggerQueueLedgerEntry entry, DateTimeOffset observedAtUtc)
    {
        if (entry.Receipt is null || entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed) || TriggerTemporalEvaluator.Evaluate(entry.Envelope.Temporal, observedAtUtc) != TriggerTemporalState.Eligible)
        {
            return false;
        }

        return entry.State == TriggerQueueEntryState.Queued
            || entry.State == TriggerQueueEntryState.WorkerOwned && entry.WorkerLease is { ReleasedAtUtc: null } lease && observedAtUtc >= lease.ExpiresAtUtc;
    }

    private static bool IsClockRollback(TriggerQueueLedger ledger, DateTimeOffset observedAtUtc)
    {
        return ledger.LastWorkerObservedAtUtc is { } last && observedAtUtc < last;
    }

    private static void ValidateSelectionRequest(TriggerWorkerSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsWorkerId(request.WorkerId)
            || request.ExpectedQueueGeneration < 0
            || request.ObservedAtUtc.Offset != TimeSpan.Zero
            || request.RecentLoopIds is null
            || request.RecentLoopIds.Count > TriggerWorkerLimits.MaxRecentLoopIds
            || request.RecentLoopIds.Any(loopId => string.IsNullOrWhiteSpace(loopId) || loopId.Length > TriggerDeliveryLimits.MaxLoopIdCharacters)
            || request.MaxConsecutiveSelectionsPerLoop is < 1 or > TriggerWorkerLimits.MaxRecentLoopIds)
        {
            throw new ArgumentException("The trigger worker selection request is invalid.", nameof(request));
        }

        ValidateLeaseDuration(request.LeaseDuration);
    }

    private static void ValidateWorkerMutation(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        if (!IsWorkerId(workerId) || leaseGeneration < 1 || expectedRevision < 1 || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The trigger worker mutation identity is invalid.");
        }
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration < TriggerWorkerLimits.MinLeaseDuration || leaseDuration > TriggerWorkerLimits.MaxLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static DateTimeOffset AddLeaseDuration(DateTimeOffset observedAtUtc, TimeSpan leaseDuration)
    {
        if (!TryAddLeaseDuration(observedAtUtc, leaseDuration, out var expiry))
        {
            throw new ArgumentOutOfRangeException(nameof(observedAtUtc), "The lease expiry exceeds the UTC timestamp range.");
        }

        return expiry;
    }

    private static bool TryAddLeaseDuration(DateTimeOffset observedAtUtc, TimeSpan leaseDuration, out DateTimeOffset expiry)
    {
        if (observedAtUtc > DateTimeOffset.MaxValue - leaseDuration)
        {
            expiry = default;
            return false;
        }

        expiry = observedAtUtc + leaseDuration;
        return true;
    }

    private static void ValidateDispatchEvidence(TriggerDispatchEvidence evidence, TriggerDispatchOutcome expectedOutcome)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var terminal = expectedOutcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal or TriggerDispatchOutcome.Rejected or TriggerDispatchOutcome.NeedsReview;
        var requiresGovernedInvocation = expectedOutcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal;
        if (!IsOperationId(evidence.OperationId)
            || !IsHash(evidence.RequestHash)
            || !IsHash(evidence.AuthorityEvidenceHash)
            || evidence.IntentRecordedAtUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(evidence.Outcome)
            || evidence.Outcome != expectedOutcome
            || terminal != (evidence.OutcomeRecordedAtUtc is not null)
            || evidence.OutcomeRecordedAtUtc is { } outcomeRecordedAtUtc && outcomeRecordedAtUtc.Offset != TimeSpan.Zero
            || evidence.OutcomeRecordedAtUtc < evidence.IntentRecordedAtUtc
            || string.IsNullOrWhiteSpace(evidence.Detail)
            || evidence.Detail.Length > TriggerWorkerLimits.MaxOutcomeDetailCharacters
            || requiresGovernedInvocation != (evidence.GovernedInvocation is not null)
            || evidence.GovernedInvocation is { } governed && (!IsOperationId(governed.OperationId)
                || !IsArtifactId(governed.RunId, TriggerWorkerLimits.MaxGovernedRunIdCharacters)
                || !IsHash(governed.AdmissionRequestHash)
                || string.IsNullOrWhiteSpace(governed.LoopId)
                || governed.LoopId.Length > TriggerDeliveryLimits.MaxLoopIdCharacters
                || !IsHash(governed.LoopReferenceHash)))
        {
            throw new ArgumentException("The trigger dispatch evidence is malformed.", nameof(evidence));
        }
    }

    private static bool SameDispatchBinding(TriggerDispatchEvidence intent, TriggerDispatchEvidence outcome)
    {
        return string.Equals(intent.OperationId, outcome.OperationId, StringComparison.Ordinal)
            && string.Equals(intent.RequestHash, outcome.RequestHash, StringComparison.Ordinal)
            && string.Equals(intent.AuthorityEvidenceHash, outcome.AuthorityEvidenceHash, StringComparison.Ordinal)
            && intent.IntentRecordedAtUtc == outcome.IntentRecordedAtUtc;
    }

    private static bool IsGovernedOutcomeBound(TriggerQueueLedgerEntry entry, TriggerDispatchEvidence evidence)
    {
        if (evidence.Outcome is not (TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal))
        {
            return evidence.GovernedInvocation is null;
        }

        return TriggerLoopReferenceHash.TryCompute(entry.Envelope.Loop, out var loopReferenceHash, out _)
            && evidence.GovernedInvocation is { } governed
            && string.Equals(governed.OperationId, evidence.OperationId, StringComparison.Ordinal)
            && string.Equals(governed.LoopId, entry.Envelope.Loop.LoopId, StringComparison.Ordinal)
            && string.Equals(governed.LoopReferenceHash, loopReferenceHash, StringComparison.Ordinal);
    }

    private static bool IsWorkerId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= TriggerWorkerLimits.MaxWorkerIdCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsOperationId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= TriggerWorkerLimits.MaxOperationIdCharacters && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsHash(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsArtifactId(string value, int maximumLength) => !string.IsNullOrEmpty(value) && value.Length <= maximumLength && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private async Task<(TriggerQueueLedger Ledger, TriggerQueueReadResult Identity)> LoadAsync(TriggerQueueMutationLease mutationLease, CancellationToken cancellationToken)
    {
        var read = await _guard.ReadLatestAsync(_maximumLedgerBytes, _observer, mutationLease, cancellationToken).ConfigureAwait(false);
        var ledger = read.LatestContent is null ? new TriggerQueueLedger(0, null, null, _quota, []) : TriggerQueueLedgerCodec.Deserialize(read.LatestContent, _quota);
        if (read.Artifacts.Count > 0 && ledger.Generation != read.Artifacts[^1].Generation)
        {
            throw new FormatException("Trigger queue ledger generation does not match its immutable artifact name.");
        }

        if (read.Artifacts.Count > 1 && !string.Equals(ledger.PreviousGenerationHash, read.Artifacts[^2].ContentHash, StringComparison.Ordinal))
        {
            throw new FormatException("Trigger queue ledger predecessor content does not match the latest generation binding.");
        }

        ValidateLedger(ledger);
        return (ledger, read);
    }

    private async Task PersistAsync(TriggerQueueLedger ledger, TriggerQueueReadResult identity, TriggerQueueMutationLease mutationLease, CancellationToken cancellationToken)
    {
        ValidateLedger(ledger);
        var artifacts = identity.Artifacts;
        var next = ledger with { Generation = checked(ledger.Generation + 1), PreviousGenerationHash = artifacts.Count == 0 ? null : artifacts[^1].ContentHash };
        var content = TriggerQueueLedgerCodec.Serialize(next);
        if (content.Length > _maximumLedgerBytes)
        {
            throw new InvalidOperationException("Trigger queue ledger exceeded its configured artifact byte bound.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _guard.WriteAsync(content, artifacts, identity.TombstoneCount, identity.Precursors, next.Generation, _observer, mutationLease).ConfigureAwait(false);
    }

    private async Task PersistSweepIfNeededAsync(TriggerQueueLedger ledger, TriggerQueueReadResult identity, bool changed, TriggerQueueMutationLease mutationLease, CancellationToken cancellationToken)
    {
        if (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(ledger, identity, mutationLease, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (TriggerQueueLedger Ledger, bool Changed) SweepExpired(TriggerQueueLedger ledger, DateTimeOffset observedAtUtc)
    {
        EnsureUtc(observedAtUtc, nameof(observedAtUtc));
        var changed = false;
        var entries = new List<TriggerQueueLedgerEntry>(ledger.Entries.Count);
        foreach (var entry in ledger.Entries)
        {
            if (entry.State is not (TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned))
            {
                entries.Add(entry);
                continue;
            }

            var expired = entry.Envelope.Temporal.ExpiresAtUtc is { } expiresAtUtc && observedAtUtc >= expiresAtUtc;
            var deadlineExceeded = !expired && entry.Envelope.Temporal.DeadlineUtc is { } deadlineUtc && observedAtUtc > deadlineUtc;
            if (!expired && !deadlineExceeded)
            {
                entries.Add(entry);
                continue;
            }

            changed = true;
            var releasedLease = entry.WorkerLease is { } lease ? lease with { ReleasedAtUtc = observedAtUtc } : null;
            entries.Add(entry with
            {
                State = TriggerQueueEntryState.Expired,
                TerminalReason = expired ? TriggerQueueTerminalReason.Expired : TriggerQueueTerminalReason.DeadlineExceeded,
                Revision = checked(entry.Revision + 1),
                TerminalAtUtc = observedAtUtc,
                WorkerLease = releasedLease
            });
        }

        return (changed ? ledger with { Entries = entries } : ledger, changed);
    }

    private static TriggerQueueLedger Replace(TriggerQueueLedger ledger, TriggerQueueLedgerEntry existing, TriggerQueueLedgerEntry replacement)
    {
        return ledger with { Entries = ledger.Entries.Select(entry => ReferenceEquals(entry, existing) ? replacement : entry).ToArray() };
    }

    private static bool IsReplay(TriggerQueueCommitRequest request, TriggerQueueLedgerEntry existing)
    {
        if (request.Priority != existing.Priority)
        {
            return false;
        }

        var exactDelivery = request.Envelope.DeliveryId.Equals(existing.Envelope.DeliveryId);
        var exactDeduplication = request.Envelope.DeduplicationId.Equals(existing.Envelope.DeduplicationId);
        if (exactDelivery && exactDeduplication)
        {
            return string.Equals(request.CanonicalEnvelopeHash, existing.CanonicalEnvelopeHash, StringComparison.Ordinal);
        }

        if (exactDelivery || !exactDeduplication
            || request.Envelope.Redelivery.Attempt <= existing.Envelope.Redelivery.Attempt
            || request.Envelope.Redelivery.Count <= existing.Envelope.Redelivery.Count
            || request.Envelope.Temporal.ReceivedAtUtc <= existing.Envelope.Temporal.ReceivedAtUtc
            || !request.Envelope.Redelivery.OriginalDeliveryId.Equals(existing.Envelope.Redelivery.OriginalDeliveryId)
            || request.Receipt is null
            || existing.Receipt is null)
        {
            return false;
        }

        return string.Equals(request.Receipt.ReplayBindingHash, existing.Receipt.ReplayBindingHash, StringComparison.Ordinal);
    }

    private void ValidateCommitRequest(TriggerQueueCommitRequest request)
    {
        EnsureUtc(request.RecordedAtUtc, nameof(request.RecordedAtUtc));
        if (!Enum.IsDefined(request.Priority)
            || !TriggerDeliveryJson.TrySerialize(request.Envelope, out _, out _)
            || !TriggerDeliveryHash.TryCompute(request.Envelope, out var hash, out _)
            || !string.Equals(hash, request.CanonicalEnvelopeHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The trigger queue commit request is invalid.", nameof(request));
        }

        if (request.Receipt is null)
        {
            if (request.AdmissionStatus != TriggerAdmissionStatus.NotYetEligible || request.AdmissionReason != TriggerAdmissionReason.NotBefore)
            {
                throw new ArgumentException("Only an authorized not-before outcome may omit a terminal delivery receipt.", nameof(request));
            }

            return;
        }

        if (request.Receipt.Status != request.AdmissionStatus
            || request.Receipt.Reason != request.AdmissionReason
            || !TriggerDeliveryAdmissionReceiptFactory.Validate(request.Receipt, request.Envelope).IsValid)
        {
            throw new ArgumentException("The trigger queue commit receipt is not bound to its envelope and outcome.", nameof(request));
        }
    }

    private void ValidateLedger(TriggerQueueLedger ledger)
    {
        if (ledger.Generation < 0
            || ledger.Quota != _quota
            || ledger.Entries.Count > _quota.MaxRetainedEntries
            || ledger.LastWorkerObservedAtUtc is { } lastWorkerObservedAtUtc && lastWorkerObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new FormatException("Trigger queue ledger generation, quota, or retained count is invalid.");
        }

        var deliveryIds = new HashSet<string>(StringComparer.Ordinal);
        var deduplicationIds = new HashSet<string>(StringComparer.Ordinal);
        long retainedBytes = 0;
        long queuedBytes = 0;
        var queuedEntries = 0;
        var perLoop = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in ledger.Entries)
        {
            var reservedBytes = ReservedEntryBytes(entry);
            retainedBytes = checked(retainedBytes + reservedBytes);
            if (reservedBytes > _quota.MaxEntryBytes
                || !deliveryIds.Add(entry.Envelope.DeliveryId.Value)
                || !deduplicationIds.Add(entry.Envelope.DeduplicationId.Value)
                || !TriggerDeliveryJson.TrySerialize(entry.Envelope, out var canonical, out _)
                || !string.Equals(canonical, entry.CanonicalEnvelope, StringComparison.Ordinal)
                || !TriggerDeliveryHash.TryCompute(entry.Envelope, out var hash, out _)
                || !string.Equals(hash, entry.CanonicalEnvelopeHash, StringComparison.Ordinal)
                || entry.Revision < 1
                || entry.RecordedAtUtc.Offset != TimeSpan.Zero
                || !Enum.IsDefined(entry.Priority)
                || !IsStateValid(entry))
            {
                throw new FormatException("Trigger queue ledger contains an invalid, duplicate, or inconsistent entry.");
            }

            if (IsNonterminal(entry))
            {
                queuedEntries++;
                queuedBytes = checked(queuedBytes + ReservedQueuedEntryBytes(entry));
                perLoop[entry.Envelope.Loop.LoopId] = perLoop.GetValueOrDefault(entry.Envelope.Loop.LoopId) + 1;
            }
        }

        if (retainedBytes > _quota.MaxRetainedBytes
            || queuedEntries > _quota.MaxQueuedEntries
            || queuedBytes > _quota.MaxQueuedBytes
            || perLoop.Values.Any(count => count > _quota.MaxQueuedEntriesPerLoop))
        {
            throw new FormatException("Trigger queue ledger exceeds a persisted count, byte, or per-loop bound.");
        }
    }

    private static bool IsStateValid(TriggerQueueLedgerEntry entry)
    {
        if (!IsWorkerEvidenceValid(entry))
        {
            return false;
        }

        if (entry.Receipt is null)
        {
            if (entry.AdmissionStatus != TriggerAdmissionStatus.NotYetEligible
                || entry.AdmissionReason != TriggerAdmissionReason.NotBefore
                || entry.WorkerLease is not null
                || entry.Dispatch is not null)
            {
                return false;
            }

            return entry.State switch
            {
                TriggerQueueEntryState.Queued => entry.TerminalReason == TriggerQueueTerminalReason.None && entry.TerminalAtUtc is null,
                TriggerQueueEntryState.Backpressured => entry.TerminalAtUtc is not null && entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded or TriggerQueueTerminalReason.QueueBytesExceeded or TriggerQueueTerminalReason.LoopQuotaExceeded,
                TriggerQueueEntryState.Cancelled => entry.TerminalAtUtc is not null && entry.TerminalReason == TriggerQueueTerminalReason.Cancelled,
                TriggerQueueEntryState.Expired => entry.TerminalAtUtc is not null && entry.TerminalReason is TriggerQueueTerminalReason.Expired or TriggerQueueTerminalReason.DeadlineExceeded,
                _ => false
            };
        }

        if (!TriggerDeliveryAdmissionReceiptFactory.Validate(entry.Receipt, entry.Envelope).IsValid
            || entry.Receipt.Status != entry.AdmissionStatus
            || entry.Receipt.Reason != entry.AdmissionReason)
        {
            return false;
        }

        if (entry.State is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching)
        {
            if (entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed) || entry.TerminalReason != TriggerQueueTerminalReason.None || entry.TerminalAtUtc is not null)
            {
                return false;
            }

            return entry.State switch
            {
                TriggerQueueEntryState.Queued => entry.Dispatch is null && (entry.WorkerLease is null || entry.WorkerLease.ReleasedAtUtc is not null),
                TriggerQueueEntryState.WorkerOwned => entry.Dispatch is null && entry.WorkerLease is { ReleasedAtUtc: null },
                TriggerQueueEntryState.Dispatching => entry.Dispatch?.Outcome == TriggerDispatchOutcome.IntentRecorded && entry.WorkerLease is { ReleasedAtUtc: null },
                _ => false
            };
        }

        if (entry.TerminalAtUtc is null || entry.TerminalAtUtc.Value.Offset != TimeSpan.Zero || entry.TerminalAtUtc < entry.RecordedAtUtc)
        {
            return false;
        }

        return entry.State switch
        {
            TriggerQueueEntryState.Rejected => entry.TerminalReason == TriggerQueueTerminalReason.AdmissionRejected && entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed) && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Backpressured => (entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded or TriggerQueueTerminalReason.QueueBytesExceeded or TriggerQueueTerminalReason.LoopQuotaExceeded) && (entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed or TriggerAdmissionStatus.NotYetEligible) && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Cancelled => entry.TerminalReason == TriggerQueueTerminalReason.Cancelled && (entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed) && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Expired => (entry.TerminalReason is TriggerQueueTerminalReason.Expired or TriggerQueueTerminalReason.DeadlineExceeded) && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Dispatched => entry.TerminalReason == TriggerQueueTerminalReason.Dispatched && entry.Dispatch?.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal && entry.WorkerLease?.ReleasedAtUtc is not null,
            TriggerQueueEntryState.DispatchRejected => entry.TerminalReason == TriggerQueueTerminalReason.DispatchRejected && entry.Dispatch?.Outcome == TriggerDispatchOutcome.Rejected && entry.WorkerLease?.ReleasedAtUtc is not null,
            TriggerQueueEntryState.NeedsReview => entry.TerminalReason == TriggerQueueTerminalReason.AmbiguousDispatch && entry.Dispatch?.Outcome == TriggerDispatchOutcome.NeedsReview && entry.WorkerLease?.ReleasedAtUtc is not null,
            _ => false
        };
    }

    private static bool IsWorkerEvidenceValid(TriggerQueueLedgerEntry entry)
    {
        if (entry.WorkerLease is { } lease)
        {
            if (!IsWorkerId(lease.WorkerId)
                || lease.Generation < 1
                || lease.RenewalCount is < 0 or > TriggerWorkerLimits.MaxLeaseRenewals
                || lease.AcquiredAtUtc.Offset != TimeSpan.Zero
                || lease.ExpiresAtUtc.Offset != TimeSpan.Zero
                || lease.AcquiredAtUtc < entry.RecordedAtUtc
                || lease.ExpiresAtUtc <= lease.AcquiredAtUtc
                || lease.ExpiresAtUtc - lease.AcquiredAtUtc > TriggerWorkerLimits.MaxLeaseOwnershipDuration
                || lease.ReleasedAtUtc is { } releasedAtUtc && (releasedAtUtc.Offset != TimeSpan.Zero || releasedAtUtc < lease.AcquiredAtUtc))
            {
                return false;
            }
        }

        if (entry.Dispatch is { } dispatch)
        {
            var terminal = dispatch.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal or TriggerDispatchOutcome.Rejected or TriggerDispatchOutcome.NeedsReview;
            if (!IsOperationId(dispatch.OperationId)
                || !IsHash(dispatch.RequestHash)
                || !IsHash(dispatch.AuthorityEvidenceHash)
                || dispatch.IntentRecordedAtUtc.Offset != TimeSpan.Zero
                || !Enum.IsDefined(dispatch.Outcome)
                || dispatch.Outcome == TriggerDispatchOutcome.None
                || terminal != (dispatch.OutcomeRecordedAtUtc is not null)
                || dispatch.OutcomeRecordedAtUtc is { } outcomeAtUtc && (outcomeAtUtc.Offset != TimeSpan.Zero || outcomeAtUtc < dispatch.IntentRecordedAtUtc)
                || string.IsNullOrWhiteSpace(dispatch.Detail)
                || dispatch.Detail.Length > TriggerWorkerLimits.MaxOutcomeDetailCharacters
                || entry.WorkerLease is null
                || dispatch.IntentRecordedAtUtc < entry.WorkerLease.AcquiredAtUtc
                || !string.Equals(dispatch.RequestHash, TriggerWorkerRequestHash.Compute(entry.Envelope, entry.WorkerLease, dispatch.AuthorityEvidenceHash), StringComparison.Ordinal)
                || !string.Equals(dispatch.OperationId, TriggerWorkerRequestHash.ComputeOperationId(entry.Envelope.DeliveryId, entry.WorkerLease.Generation), StringComparison.Ordinal)
                || !IsGovernedOutcomeBound(entry, dispatch))
            {
                return false;
            }
        }

        if (entry.WorkerLease is { } workerLease && entry.Dispatch is null && entry.State is TriggerQueueEntryState.Dispatched or TriggerQueueEntryState.DispatchRejected or TriggerQueueEntryState.NeedsReview)
        {
            return false;
        }

        return true;
    }

    private static bool HasNoLiveWorker(TriggerQueueLedgerEntry entry) => entry.Dispatch is null && (entry.WorkerLease is null || entry.WorkerLease.ReleasedAtUtc is not null);

    private TriggerQueueSnapshot ToSnapshot(TriggerQueueLedger ledger, TriggerQueueReadResult identity)
    {
        var entries = ledger.Entries.Select(ToEntry)
            .OrderBy(entry => entry.OrderKey.EligibleAtUtc)
            .ThenByDescending(entry => entry.OrderKey.Priority)
            .ThenBy(entry => entry.OrderKey.AcceptedAtUtc)
            .ThenBy(entry => entry.OrderKey.DeliveryId, StringComparer.Ordinal)
            .ToArray();
        return new TriggerQueueSnapshot(
            TriggerQueueSnapshot.CurrentSchemaVersion,
            ledger.Generation,
            _quota,
            entries.Count(entry => IsNonterminal(entry.State)),
            entries.Where(entry => IsNonterminal(entry.State)).Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Where(entry => IsNonterminal(entry.State)).Sum(entry => (long)entry.QueuedReservationBytes),
            entries.Length,
            entries.Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Sum(entry => (long)entry.RetainedReservationBytes),
            identity.TombstoneCount,
            !CanPersist(identity),
            entries);
    }

    private bool CanPersist(TriggerQueueReadResult identity)
    {
        var reservedArtifacts = identity.Precursors.Count + (OperatingSystem.IsWindows() ? 0 : identity.TombstoneCount + Math.Max(1, identity.Artifacts.Count));
        return reservedArtifacts <= _quota.MaxDurabilityTombstones;
    }

    private static TriggerQueueEntry ToEntry(TriggerQueueLedgerEntry entry)
    {
        var notBefore = entry.Envelope.Temporal.NotBeforeUtc;
        var eligibleAtUtc = notBefore is not null && notBefore > entry.RecordedAtUtc ? notBefore.Value : entry.RecordedAtUtc;
        var order = new TriggerQueueOrderKey(eligibleAtUtc, entry.Priority, entry.RecordedAtUtc, entry.Envelope.DeliveryId.Value);
        var queuedReservationBytes = IsNonterminal(entry) ? ReservedQueuedEntryBytes(entry) : 0;
        return new TriggerQueueEntry(entry.Envelope.DeliveryId, entry.Envelope.DeduplicationId, entry.Envelope.Loop.LoopId, entry.CanonicalEnvelopeHash, EntryBytes(entry), queuedReservationBytes, ReservedEntryBytes(entry), entry.State, entry.TerminalReason, order, entry.Revision, entry.RecordedAtUtc, entry.TerminalAtUtc, entry.AdmissionStatus, entry.AdmissionReason, entry.WorkerLease, entry.Dispatch);
    }

    private static TriggerDeliveryAdmissionHistoryEntry? ToHistory(TriggerQueueLedgerEntry? entry)
    {
        return entry?.Receipt is null ? null : new TriggerDeliveryAdmissionHistoryEntry(entry.Envelope, entry.Receipt);
    }

    private static TriggerQueueAdmissionResult Result(TriggerQueueAdmissionStatus status, TriggerQueueAdmissionReason reason, TriggerQueueCommitRequest request, TriggerQueueEntry? entry)
    {
        return new TriggerQueueAdmissionResult(status, reason, request.Envelope.DeliveryId, request.Envelope.DeduplicationId, request.CanonicalEnvelopeHash, entry, request.AdmissionStatus, request.AdmissionReason);
    }

    private static int EntryBytes(TriggerQueueLedgerEntry entry) => TriggerQueueLedgerCodec.MeasureEntry(entry);

    private static int ReservedEntryBytes(TriggerQueueLedgerEntry entry)
    {
        var bytes = EntryBytes(entry);
        if (!IsNonterminal(entry))
        {
            return bytes;
        }

        var receipt = RepresentativeTerminalReceipt(entry);
        var rejected = entry with { Receipt = receipt, AdmissionStatus = TriggerAdmissionStatus.Unauthorized, AdmissionReason = TriggerAdmissionReason.AuthorityMismatch, State = TriggerQueueEntryState.Rejected, TerminalReason = TriggerQueueTerminalReason.AdmissionRejected, TerminalAtUtc = entry.RecordedAtUtc };
        var cancelled = entry with { State = TriggerQueueEntryState.Cancelled, TerminalReason = TriggerQueueTerminalReason.Cancelled, TerminalAtUtc = entry.RecordedAtUtc };
        var expired = entry with { State = TriggerQueueEntryState.Expired, TerminalReason = TriggerQueueTerminalReason.DeadlineExceeded, TerminalAtUtc = entry.RecordedAtUtc };
        var lease = RepresentativeWorkerLease(entry);
        var intent = RepresentativeDispatch(entry, lease, TriggerDispatchOutcome.IntentRecorded);
        var owned = entry with { State = TriggerQueueEntryState.WorkerOwned, WorkerLease = lease, Dispatch = null };
        var dispatching = entry with { State = TriggerQueueEntryState.Dispatching, WorkerLease = lease, Dispatch = intent };
        var accepted = entry with { State = TriggerQueueEntryState.Dispatched, TerminalReason = TriggerQueueTerminalReason.Dispatched, TerminalAtUtc = entry.RecordedAtUtc, WorkerLease = lease with { ReleasedAtUtc = entry.RecordedAtUtc }, Dispatch = RepresentativeDispatch(entry, lease, TriggerDispatchOutcome.Accepted) };
        var needsReview = entry with { State = TriggerQueueEntryState.NeedsReview, TerminalReason = TriggerQueueTerminalReason.AmbiguousDispatch, TerminalAtUtc = entry.RecordedAtUtc, WorkerLease = lease with { ReleasedAtUtc = entry.RecordedAtUtc }, Dispatch = RepresentativeDispatch(entry, lease, TriggerDispatchOutcome.NeedsReview) };
        return new[] { ReservedQueuedEntryBytes(entry), EntryBytes(rejected), EntryBytes(cancelled), EntryBytes(expired), EntryBytes(owned), EntryBytes(dispatching), EntryBytes(accepted), EntryBytes(needsReview) }.Max();
    }

    private static int ReservedQueuedEntryBytes(TriggerQueueLedgerEntry entry)
    {
        if (entry.Receipt is not null)
        {
            var lease = RepresentativeWorkerLease(entry);
            var intent = RepresentativeDispatch(entry, lease, TriggerDispatchOutcome.IntentRecorded);
            return Math.Max(EntryBytes(entry), Math.Max(EntryBytes(entry with { State = TriggerQueueEntryState.WorkerOwned, WorkerLease = lease }), EntryBytes(entry with { State = TriggerQueueEntryState.Dispatching, WorkerLease = lease, Dispatch = intent })));
        }

        var promoted = entry with { Receipt = RepresentativeTerminalReceipt(entry), AdmissionStatus = TriggerAdmissionStatus.Unauthorized, AdmissionReason = TriggerAdmissionReason.AuthorityMismatch };
        return Math.Max(EntryBytes(entry), EntryBytes(promoted));
    }

    private static TriggerDeliveryAdmissionReceipt RepresentativeTerminalReceipt(TriggerQueueLedgerEntry entry)
    {
        if (!TriggerDeliveryAdmissionReceiptFactory.TryCreate(entry.Envelope, TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityMismatch, entry.RecordedAtUtc, out var receipt, out _))
        {
            throw new InvalidOperationException("A validated queue entry could not reserve its terminal receipt representation.");
        }

        return receipt!;
    }

    private static bool IsQueueAccepted(TriggerAdmissionStatus status) => status is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed or TriggerAdmissionStatus.NotYetEligible;

    private static TriggerWorkerLease RepresentativeWorkerLease(TriggerQueueLedgerEntry entry)
    {
        return new TriggerWorkerLease(new string('w', TriggerWorkerLimits.MaxWorkerIdCharacters), long.MaxValue, entry.RecordedAtUtc, entry.RecordedAtUtc + TriggerWorkerLimits.MaxLeaseDuration, TriggerWorkerLimits.MaxLeaseRenewals);
    }

    private static TriggerDispatchEvidence RepresentativeDispatch(TriggerQueueLedgerEntry entry, TriggerWorkerLease lease, TriggerDispatchOutcome outcome)
    {
        var terminal = outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal or TriggerDispatchOutcome.Rejected or TriggerDispatchOutcome.NeedsReview;
        var governed = outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal ? RepresentativeGovernedInvocation(entry, lease) : null;
        return new TriggerDispatchEvidence(new string('o', TriggerWorkerLimits.MaxOperationIdCharacters), new string('a', 64), new string('b', 64), lease.AcquiredAtUtc, outcome, terminal ? lease.AcquiredAtUtc : null, new string('d', TriggerWorkerLimits.MaxOutcomeDetailCharacters), governed);
    }

    private static TriggerGovernedInvocationEvidence RepresentativeGovernedInvocation(TriggerQueueLedgerEntry entry, TriggerWorkerLease lease)
    {
        if (!TriggerLoopReferenceHash.TryCompute(entry.Envelope.Loop, out var loopReferenceHash, out _))
        {
            throw new InvalidOperationException("A validated queue entry could not reserve its exact governed target receipt representation.");
        }

        return new TriggerGovernedInvocationEvidence(TriggerWorkerRequestHash.ComputeOperationId(entry.Envelope.DeliveryId, lease.Generation), new string('r', TriggerWorkerLimits.MaxGovernedRunIdCharacters), new string('a', 64), entry.Envelope.Loop.LoopId, loopReferenceHash!);
    }

    private static bool IsNonterminal(TriggerQueueLedgerEntry entry) => IsNonterminal(entry.State);

    private static bool IsNonterminal(TriggerQueueEntryState state) => state is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching;

    private static void EnsureUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        }
    }
}
