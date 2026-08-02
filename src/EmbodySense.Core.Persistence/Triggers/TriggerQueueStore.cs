using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Triggers.Models;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Persists one bounded schema-version-1 trigger queue ledger with cross-process atomic mutation.</summary>
/// <remarks>
/// The store records admission evidence only. It has no provider, actuator, selection, lease, dispatch, or execution dependency.
/// Caller cancellation is honored through the final pre-staging check. Atomic rename is the commit boundary; an exception after
/// publication is an ambiguous caller outcome, and exact retry resolves it by replaying the durable entry.
/// Terminal evidence is retained without automatic pruning and remains subject to explicit retained count and byte bounds.
/// Unix cleanup uses authenticated tombstones because pathname unlink cannot condition deletion on file identity. Their public
/// quota is inspectable in snapshots, and every mutation reserves its worst-case tombstone use before staging.
/// </remarks>
public sealed class TriggerQueueStore : ITriggerQueueMutationPort, ITriggerQueueQueryPort, ITriggerQueueCancellationPort, ITriggerDeliveryAdmissionHistoryPort
{
    private readonly TriggerQueueQuota _quota;
    private readonly TriggerQueueArtifactGuard _guard;
    private readonly int _maximumLedgerBytes;
    private readonly ITriggerQueueDurabilityObserver _observer;

    /// <summary>Initializes a workspace-scoped queue with composition-owned bounds and an optional durability observer.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="quota">The schema-version-1 quota, or conservative defaults.</param>
    /// <param name="observer">An optional diagnostics or crash-test observer.</param>
    public TriggerQueueStore(WorkspacePaths paths, TriggerQueueQuota? quota = null, ITriggerQueueDurabilityObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _quota = quota ?? TriggerQueueQuota.Default;
        TriggerQueueQuotaValidator.Validate(_quota);
        _observer = observer ?? NullTriggerQueueDurabilityObserver.Instance;
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
            using var mutationLock = await _guard.AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
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
                var queuedEntries = swept.Entries.Count(entry => entry.State == TriggerQueueEntryState.Queued);
                var queuedBytes = swept.Entries.Where(entry => entry.State == TriggerQueueEntryState.Queued).Sum(ReservedQueuedEntryBytes);
                var loopEntries = swept.Entries.Count(entry => entry.State == TriggerQueueEntryState.Queued && string.Equals(entry.Envelope.Loop.LoopId, request.Envelope.Loop.LoopId, StringComparison.Ordinal));
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
        using var mutationLock = await _guard.AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
        var (swept, changed) = SweepExpired(ledger, observedAtUtc);
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
            using var mutationLock = await _guard.AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
            var (ledger, identity) = await LoadAsync(mutationLock, cancellationToken).ConfigureAwait(false);
            var (swept, sweepChanged) = SweepExpired(ledger, cancelledAtUtc);
            var existing = swept.Entries.SingleOrDefault(entry => entry.Envelope.DeliveryId.Equals(deliveryId));
            if (existing is null)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.NotFound, null);
            }

            if (existing.State != TriggerQueueEntryState.Queued)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.AlreadyTerminal, ToEntry(existing));
            }

            if (existing.Revision != expectedRevision)
            {
                await PersistSweepIfNeededAsync(swept, identity, sweepChanged, mutationLock, cancellationToken).ConfigureAwait(false);
                return new TriggerQueueCancellationResult(TriggerQueueCancellationStatus.RevisionConflict, ToEntry(existing));
            }

            var cancelled = existing with { State = TriggerQueueEntryState.Cancelled, TerminalReason = TriggerQueueTerminalReason.Cancelled, Revision = existing.Revision + 1, TerminalAtUtc = cancelledAtUtc };
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
            using var mutationLock = await _guard.AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<(TriggerQueueLedger Ledger, TriggerQueueReadResult Identity)> LoadAsync(TriggerQueueMutationLease mutationLease, CancellationToken cancellationToken)
    {
        var read = await _guard.ReadLatestAsync(_maximumLedgerBytes, _observer, mutationLease, cancellationToken).ConfigureAwait(false);
        var ledger = read.LatestContent is null ? new TriggerQueueLedger(0, null, _quota, []) : TriggerQueueLedgerCodec.Deserialize(read.LatestContent, _quota);
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
        await _guard.WriteAsync(content, artifacts, identity.TombstoneCount, next.Generation, _observer, mutationLease).ConfigureAwait(false);
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
            if (entry.State != TriggerQueueEntryState.Queued)
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
            entries.Add(entry with
            {
                State = TriggerQueueEntryState.Expired,
                TerminalReason = expired ? TriggerQueueTerminalReason.Expired : TriggerQueueTerminalReason.DeadlineExceeded,
                Revision = checked(entry.Revision + 1),
                TerminalAtUtc = observedAtUtc
            });
        }

        return (ledger with { Entries = entries }, changed);
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
        if (ledger.Generation < 0 || ledger.Quota != _quota || ledger.Entries.Count > _quota.MaxRetainedEntries)
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

            if (entry.State == TriggerQueueEntryState.Queued)
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
        if (entry.Receipt is null)
        {
            if (entry.AdmissionStatus != TriggerAdmissionStatus.NotYetEligible || entry.AdmissionReason != TriggerAdmissionReason.NotBefore)
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

        if (entry.State == TriggerQueueEntryState.Queued)
        {
            return entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
                && entry.TerminalReason == TriggerQueueTerminalReason.None
                && entry.TerminalAtUtc is null;
        }

        if (entry.TerminalAtUtc is null || entry.TerminalAtUtc.Value.Offset != TimeSpan.Zero || entry.TerminalAtUtc < entry.RecordedAtUtc)
        {
            return false;
        }

        return entry.State switch
        {
            TriggerQueueEntryState.Rejected => entry.TerminalReason == TriggerQueueTerminalReason.AdmissionRejected && entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed),
            TriggerQueueEntryState.Backpressured => (entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded or TriggerQueueTerminalReason.QueueBytesExceeded or TriggerQueueTerminalReason.LoopQuotaExceeded) && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed or TriggerAdmissionStatus.NotYetEligible,
            TriggerQueueEntryState.Cancelled => entry.TerminalReason == TriggerQueueTerminalReason.Cancelled && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed,
            TriggerQueueEntryState.Expired => entry.TerminalReason is TriggerQueueTerminalReason.Expired or TriggerQueueTerminalReason.DeadlineExceeded,
            _ => false
        };
    }

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
            entries.Count(entry => entry.State == TriggerQueueEntryState.Queued),
            entries.Where(entry => entry.State == TriggerQueueEntryState.Queued).Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Where(entry => entry.State == TriggerQueueEntryState.Queued).Sum(entry => (long)entry.QueuedReservationBytes),
            entries.Length,
            entries.Sum(entry => (long)entry.SerializedEntryBytes),
            entries.Sum(entry => (long)entry.RetainedReservationBytes),
            identity.TombstoneCount,
            !CanPersist(identity),
            entries);
    }

    private bool CanPersist(TriggerQueueReadResult identity)
    {
        return OperatingSystem.IsWindows() || identity.TombstoneCount + Math.Max(1, identity.Artifacts.Count) <= _quota.MaxDurabilityTombstones;
    }

    private static TriggerQueueEntry ToEntry(TriggerQueueLedgerEntry entry)
    {
        var notBefore = entry.Envelope.Temporal.NotBeforeUtc;
        var eligibleAtUtc = notBefore is not null && notBefore > entry.RecordedAtUtc ? notBefore.Value : entry.RecordedAtUtc;
        var order = new TriggerQueueOrderKey(eligibleAtUtc, entry.Priority, entry.RecordedAtUtc, entry.Envelope.DeliveryId.Value);
        var queuedReservationBytes = entry.State == TriggerQueueEntryState.Queued ? ReservedQueuedEntryBytes(entry) : 0;
        return new TriggerQueueEntry(entry.Envelope.DeliveryId, entry.Envelope.DeduplicationId, entry.Envelope.Loop.LoopId, entry.CanonicalEnvelopeHash, EntryBytes(entry), queuedReservationBytes, ReservedEntryBytes(entry), entry.State, entry.TerminalReason, order, entry.Revision, entry.RecordedAtUtc, entry.TerminalAtUtc, entry.AdmissionStatus, entry.AdmissionReason);
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
        if (entry.State != TriggerQueueEntryState.Queued)
        {
            return bytes;
        }

        var receipt = RepresentativeTerminalReceipt(entry);
        var rejected = entry with { Receipt = receipt, AdmissionStatus = TriggerAdmissionStatus.Unauthorized, AdmissionReason = TriggerAdmissionReason.AuthorityMismatch, State = TriggerQueueEntryState.Rejected, TerminalReason = TriggerQueueTerminalReason.AdmissionRejected, TerminalAtUtc = entry.RecordedAtUtc };
        var cancelled = entry with { State = TriggerQueueEntryState.Cancelled, TerminalReason = TriggerQueueTerminalReason.Cancelled, TerminalAtUtc = entry.RecordedAtUtc };
        var expired = entry with { State = TriggerQueueEntryState.Expired, TerminalReason = TriggerQueueTerminalReason.DeadlineExceeded, TerminalAtUtc = entry.RecordedAtUtc };
        return Math.Max(ReservedQueuedEntryBytes(entry), Math.Max(EntryBytes(rejected), Math.Max(EntryBytes(cancelled), EntryBytes(expired))));
    }

    private static int ReservedQueuedEntryBytes(TriggerQueueLedgerEntry entry)
    {
        if (entry.Receipt is not null)
        {
            return EntryBytes(entry);
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

    private static void EnsureUtc(DateTimeOffset timestamp, string parameterName)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        }
    }
}
