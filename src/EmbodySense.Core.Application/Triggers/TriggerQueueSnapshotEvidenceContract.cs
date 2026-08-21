using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Validates bounded queue snapshots before orchestration or projection consumes adapter evidence.</summary>
public static class TriggerQueueSnapshotEvidenceContract
{
    private static readonly TriggerQueueQuota _schemaEntryCeiling = new(
        1_024,
        4_096,
        128 * 1024,
        256L * 1024 * 1024,
        512L * 1024 * 1024,
        1_024,
        120);

    /// <summary>Gets whether one snapshot is schema-valid, internally consistent, bounded, and canonically ordered.</summary>
    public static bool IsValid(TriggerQueueSnapshot? snapshot)
    {
        if (snapshot is null
            || snapshot.SchemaVersion != TriggerQueueSnapshot.CurrentSchemaVersion
            || snapshot.Generation < 0
            || snapshot.Quota is null
            || snapshot.Entries is null)
        {
            return false;
        }

        try
        {
            TriggerQueueQuotaValidator.Validate(snapshot.Quota);
            var deliveryIds = new HashSet<string>(StringComparer.Ordinal);
            var deduplicationIds = new HashSet<string>(StringComparer.Ordinal);
            var perLoop = new Dictionary<string, int>(StringComparer.Ordinal);
            long queuedBytes = 0;
            long queuedReservations = 0;
            long retainedBytes = 0;
            long retainedReservations = 0;
            var queuedEntries = 0;
            TriggerQueueEntry? previous = null;
            foreach (var entry in snapshot.Entries)
            {
                if (!IsValid(entry, snapshot.Quota)
                    || !deliveryIds.Add(entry.DeliveryId.Value)
                    || !deduplicationIds.Add(entry.DeduplicationId.Value)
                    || previous is not null && Compare(previous, entry) > 0)
                {
                    return false;
                }

                retainedBytes = checked(retainedBytes + entry.SerializedEntryBytes);
                retainedReservations = checked(retainedReservations + entry.RetainedReservationBytes);
                if (IsNonterminal(entry.State))
                {
                    queuedEntries++;
                    queuedBytes = checked(queuedBytes + entry.SerializedEntryBytes);
                    queuedReservations = checked(queuedReservations + entry.QueuedReservationBytes);
                    perLoop[entry.LoopId] = perLoop.GetValueOrDefault(entry.LoopId) + 1;
                }
                previous = entry;
            }

            return snapshot.RetainedEntries == snapshot.Entries.Count
                && snapshot.QueuedEntries == queuedEntries
                && snapshot.QueuedBytes == queuedBytes
                && snapshot.QueuedReservationBytes == queuedReservations
                && snapshot.RetainedBytes == retainedBytes
                && snapshot.RetainedReservationBytes == retainedReservations
                && snapshot.QueuedEntries <= snapshot.Quota.MaxQueuedEntries
                && snapshot.RetainedEntries <= snapshot.Quota.MaxRetainedEntries
                && snapshot.QueuedReservationBytes <= snapshot.Quota.MaxQueuedBytes
                && snapshot.RetainedReservationBytes <= snapshot.Quota.MaxRetainedBytes
                && perLoop.Values.All(count => count <= snapshot.Quota.MaxQueuedEntriesPerLoop)
                && snapshot.DurabilityTombstones is >= 0
                    && snapshot.DurabilityTombstones <= snapshot.Quota.MaxDurabilityTombstones;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Gets whether one detached entry is valid within schema-1 safety ceilings.</summary>
    public static bool IsValid(TriggerQueueEntry? entry)
        => entry is not null && IsValid(entry, _schemaEntryCeiling);

    private static bool IsValid(TriggerQueueEntry entry, TriggerQueueQuota quota)
    {
        if (entry.DeliveryId is null
            || entry.DeduplicationId is null
            || !TriggerDeliveryId.TryParse(entry.DeliveryId.Value, out var deliveryId)
            || deliveryId?.Equals(entry.DeliveryId) != true
            || !TriggerDeduplicationId.TryParse(entry.DeduplicationId.Value, out var deduplicationId)
            || deduplicationId?.Equals(entry.DeduplicationId) != true
            || !CustomLoopArtifactIdentifier.IsValid(entry.LoopId, TriggerDeliveryLimits.MaxLoopIdCharacters)
            || !IsHash(entry.CanonicalEnvelopeHash)
            || entry.SerializedEntryBytes is <= 0
            || entry.RetainedReservationBytes < entry.SerializedEntryBytes
            || entry.RetainedReservationBytes > quota.MaxEntryBytes
            || entry.QueuedReservationBytes is < 0
            || entry.QueuedReservationBytes > entry.RetainedReservationBytes
            || entry.Revision <= 0
            || entry.RecordedAtUtc.Offset != TimeSpan.Zero
            || !Enum.IsDefined(entry.State)
            || !Enum.IsDefined(entry.TerminalReason)
            || !IsAdmissionShapeValid(entry.AdmissionStatus, entry.AdmissionReason)
            || entry.OrderKey is null
            || entry.OrderKey.EligibleAtUtc.Offset != TimeSpan.Zero
            || entry.OrderKey.AcceptedAtUtc.Offset != TimeSpan.Zero
            || entry.OrderKey.AcceptedAtUtc != entry.RecordedAtUtc
            || entry.OrderKey.EligibleAtUtc < entry.OrderKey.AcceptedAtUtc
            || !Enum.IsDefined(entry.OrderKey.Priority)
            || !string.Equals(entry.OrderKey.DeliveryId, entry.DeliveryId.Value, StringComparison.Ordinal)
            || !IsWorkerEvidenceValid(entry))
        {
            return false;
        }

        if (entry.AdmissionStatus == TriggerAdmissionStatus.NotYetEligible)
        {
            if (entry.WorkerLease is not null || entry.Dispatch is not null)
            {
                return false;
            }

            return entry.State switch
            {
                TriggerQueueEntryState.Queued => entry.TerminalReason == TriggerQueueTerminalReason.None
                    && entry.TerminalAtUtc is null
                    && IsNonterminalReservationValid(entry),
                TriggerQueueEntryState.Backpressured => IsTerminalShapeValid(entry)
                    && entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded
                        or TriggerQueueTerminalReason.QueueBytesExceeded
                        or TriggerQueueTerminalReason.LoopQuotaExceeded,
                TriggerQueueEntryState.Cancelled => IsTerminalShapeValid(entry)
                    && entry.TerminalReason == TriggerQueueTerminalReason.Cancelled,
                TriggerQueueEntryState.Expired => IsTerminalShapeValid(entry)
                    && entry.TerminalReason is TriggerQueueTerminalReason.Expired
                        or TriggerQueueTerminalReason.DeadlineExceeded,
                _ => false
            };
        }

        if (IsNonterminal(entry.State))
        {
            if (entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed)
                || entry.TerminalReason != TriggerQueueTerminalReason.None
                || entry.TerminalAtUtc is not null
                || !IsNonterminalReservationValid(entry))
            {
                return false;
            }

            return entry.State switch
            {
                TriggerQueueEntryState.Queued => entry.Dispatch is null
                    && (entry.WorkerLease is null || entry.WorkerLease.ReleasedAtUtc is not null),
                TriggerQueueEntryState.WorkerOwned => entry.Dispatch is null
                    && entry.WorkerLease is { ReleasedAtUtc: null },
                TriggerQueueEntryState.Dispatching => entry.Dispatch?.Outcome == TriggerDispatchOutcome.IntentRecorded
                    && entry.WorkerLease is { ReleasedAtUtc: null },
                _ => false
            };
        }

        if (!IsTerminalShapeValid(entry))
        {
            return false;
        }

        return entry.State switch
        {
            TriggerQueueEntryState.Rejected => entry.TerminalReason == TriggerQueueTerminalReason.AdmissionRejected
                && entry.AdmissionStatus is not (TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed)
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Backpressured => entry.TerminalReason is TriggerQueueTerminalReason.QueueCountExceeded
                    or TriggerQueueTerminalReason.QueueBytesExceeded
                    or TriggerQueueTerminalReason.LoopQuotaExceeded
                && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Cancelled => entry.TerminalReason == TriggerQueueTerminalReason.Cancelled
                && entry.AdmissionStatus is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Expired => entry.TerminalReason is TriggerQueueTerminalReason.Expired
                    or TriggerQueueTerminalReason.DeadlineExceeded
                && HasNoLiveWorker(entry),
            TriggerQueueEntryState.Dispatched => entry.TerminalReason == TriggerQueueTerminalReason.Dispatched
                && entry.Dispatch?.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal
                && entry.WorkerLease?.ReleasedAtUtc is not null,
            TriggerQueueEntryState.DispatchRejected => entry.TerminalReason == TriggerQueueTerminalReason.DispatchRejected
                && entry.Dispatch?.Outcome == TriggerDispatchOutcome.Rejected
                && entry.WorkerLease?.ReleasedAtUtc is not null,
            TriggerQueueEntryState.NeedsReview => entry.TerminalReason == TriggerQueueTerminalReason.AmbiguousDispatch
                && entry.Dispatch?.Outcome == TriggerDispatchOutcome.NeedsReview
                && entry.WorkerLease?.ReleasedAtUtc is not null,
            _ => false
        };
    }

    private static bool IsAdmissionShapeValid(TriggerAdmissionStatus status, TriggerAdmissionReason reason)
        => status switch
        {
            TriggerAdmissionStatus.Admitted => reason == TriggerAdmissionReason.EvidenceAccepted,
            TriggerAdmissionStatus.Replayed => reason == TriggerAdmissionReason.ExactReplay,
            TriggerAdmissionStatus.Conflicting => reason == TriggerAdmissionReason.IdentityConflict,
            TriggerAdmissionStatus.NotYetEligible => reason == TriggerAdmissionReason.NotBefore,
            TriggerAdmissionStatus.Expired => reason is TriggerAdmissionReason.DeadlineExceeded or TriggerAdmissionReason.Expired,
            TriggerAdmissionStatus.Unauthorized => reason is TriggerAdmissionReason.StaleLoop
                or TriggerAdmissionReason.StaleAdapter
                or TriggerAdmissionReason.ActorMismatch
                or TriggerAdmissionReason.SurfaceMismatch
                or TriggerAdmissionReason.WorkspaceMismatch
                or TriggerAdmissionReason.RoleMismatch
                or TriggerAdmissionReason.AuthorityMismatch
                or TriggerAdmissionReason.StaleAuthority
                or TriggerAdmissionReason.AuthorityBoundary
                or TriggerAdmissionReason.StaleDelivery,
            TriggerAdmissionStatus.Invalid => reason == TriggerAdmissionReason.InvalidEnvelope,
            _ => false
        };

    private static bool IsWorkerEvidenceValid(TriggerQueueEntry entry)
    {
        if (entry.WorkerLease is { } lease
            && (!IsWorkerId(lease.WorkerId)
                || lease.Generation < 1
                || lease.RenewalCount is < 0 or > TriggerWorkerLimits.MaxLeaseRenewals
                || lease.AcquiredAtUtc.Offset != TimeSpan.Zero
                || lease.ExpiresAtUtc.Offset != TimeSpan.Zero
                || lease.AcquiredAtUtc < entry.RecordedAtUtc
                || lease.ExpiresAtUtc <= lease.AcquiredAtUtc
                || lease.ExpiresAtUtc - lease.AcquiredAtUtc > TriggerWorkerLimits.MaxLeaseOwnershipDuration
                || lease.ReleasedAtUtc is { } releasedAtUtc
                    && (releasedAtUtc.Offset != TimeSpan.Zero || releasedAtUtc < lease.AcquiredAtUtc)))
        {
            return false;
        }

        if (entry.Dispatch is { } dispatch)
        {
            var terminal = dispatch.Outcome is TriggerDispatchOutcome.Accepted
                or TriggerDispatchOutcome.Terminal
                or TriggerDispatchOutcome.Rejected
                or TriggerDispatchOutcome.NeedsReview;
            var requiresGovernedInvocation = dispatch.Outcome is TriggerDispatchOutcome.Accepted or TriggerDispatchOutcome.Terminal;
            if (entry.WorkerLease is not { } dispatchLease
                || !IsOperationId(dispatch.OperationId)
                || !string.Equals(dispatch.OperationId, TriggerWorkerRequestHash.ComputeOperationId(entry.DeliveryId, dispatchLease.Generation), StringComparison.Ordinal)
                || !IsHash(dispatch.RequestHash)
                || !IsHash(dispatch.AuthorityEvidenceHash)
                || dispatch.IntentRecordedAtUtc.Offset != TimeSpan.Zero
                || dispatch.IntentRecordedAtUtc < dispatchLease.AcquiredAtUtc
                || !Enum.IsDefined(dispatch.Outcome)
                || dispatch.Outcome == TriggerDispatchOutcome.None
                || terminal != (dispatch.OutcomeRecordedAtUtc is not null)
                || dispatch.OutcomeRecordedAtUtc is { } outcomeRecordedAtUtc
                    && (outcomeRecordedAtUtc.Offset != TimeSpan.Zero || outcomeRecordedAtUtc < dispatch.IntentRecordedAtUtc)
                || string.IsNullOrWhiteSpace(dispatch.Detail)
                || dispatch.Detail.Length > TriggerWorkerLimits.MaxOutcomeDetailCharacters
                || requiresGovernedInvocation != (dispatch.GovernedInvocation is not null)
                || dispatch.GovernedInvocation is { } governed
                    && (!IsOperationId(governed.OperationId)
                        || !string.Equals(governed.OperationId, dispatch.OperationId, StringComparison.Ordinal)
                        || !IsArtifactId(governed.RunId, TriggerWorkerLimits.MaxGovernedRunIdCharacters)
                        || !IsHash(governed.AdmissionRequestHash)
                        || !string.Equals(governed.LoopId, entry.LoopId, StringComparison.Ordinal)
                        || !IsHash(governed.LoopReferenceHash)))
            {
                return false;
            }
        }

        return entry.WorkerLease is not { }
            || entry.Dispatch is not null
            || entry.State is not (TriggerQueueEntryState.Dispatched
                or TriggerQueueEntryState.DispatchRejected
                or TriggerQueueEntryState.NeedsReview);
    }

    private static int Compare(TriggerQueueEntry left, TriggerQueueEntry right)
    {
        var comparison = left.OrderKey.EligibleAtUtc.CompareTo(right.OrderKey.EligibleAtUtc);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = right.OrderKey.Priority.CompareTo(left.OrderKey.Priority);
        if (comparison != 0)
        {
            return comparison;
        }
        comparison = left.OrderKey.AcceptedAtUtc.CompareTo(right.OrderKey.AcceptedAtUtc);
        return comparison != 0 ? comparison : string.Compare(left.OrderKey.DeliveryId, right.OrderKey.DeliveryId, StringComparison.Ordinal);
    }

    private static bool IsNonterminal(TriggerQueueEntryState state)
        => state is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching;

    private static bool IsNonterminalReservationValid(TriggerQueueEntry entry)
        => entry.QueuedReservationBytes >= entry.SerializedEntryBytes;

    private static bool IsTerminalShapeValid(TriggerQueueEntry entry)
        => entry.TerminalAtUtc is { } terminalAtUtc
            && terminalAtUtc.Offset == TimeSpan.Zero
            && terminalAtUtc >= entry.RecordedAtUtc
            && entry.QueuedReservationBytes == 0
            && entry.RetainedReservationBytes == entry.SerializedEntryBytes;

    private static bool HasNoLiveWorker(TriggerQueueEntry entry)
        => entry.Dispatch is null && (entry.WorkerLease is null || entry.WorkerLease.ReleasedAtUtc is not null);

    private static bool IsWorkerId(string? value)
        => IsToken(value, TriggerWorkerLimits.MaxWorkerIdCharacters);

    private static bool IsOperationId(string? value)
        => IsToken(value, TriggerWorkerLimits.MaxOperationIdCharacters);

    private static bool IsArtifactId(string? value, int maximumLength)
        => !string.IsNullOrEmpty(value)
            && value.Length <= maximumLength
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsHash(string? value)
        => value is { Length: TriggerDeliveryLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsToken(string? value, int maximumLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
