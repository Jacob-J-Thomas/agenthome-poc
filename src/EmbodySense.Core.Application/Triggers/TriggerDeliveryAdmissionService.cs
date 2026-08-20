using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>
/// Classifies bounded trigger-delivery evidence against exact current evidence without side effects.
/// </summary>
public sealed class TriggerDeliveryAdmissionService : ITriggerDeliveryAdmissionPort
{
    private readonly ITriggerDeliveryAdmissionHistoryPort _history;

    /// <summary>
    /// Initializes admission with the composition-owned source of durable terminal history.
    /// </summary>
    /// <param name="history">The server-owned history source. Untrusted requests cannot supply its results.</param>
    public TriggerDeliveryAdmissionService(ITriggerDeliveryAdmissionHistoryPort history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
    }

    /// <inheritdoc />
    public async Task<TriggerDeliveryAdmissionResult> AdmitAsync(TriggerDeliveryAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = TriggerDeliveryValidator.Validate(request.Envelope);
        if (!validation.IsValid || !TriggerDeliveryHash.TryCompute(request.Envelope, out var envelopeHash, out _))
        {
            return Result(TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope, null);
        }

        if (request.Envelope.Adapter == request.CurrentAdapter && !request.IsAdapterAvailable)
        {
            return Result(TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.AdapterUnavailable, envelopeHash);
        }

        var historyLookup = await _history.FindAsync(request.Envelope.DeliveryId, request.Envelope.DeduplicationId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (historyLookup is null || historyLookup.Status != TriggerDeliveryAdmissionHistoryLookupStatus.Available)
        {
            return Result(TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.HistoryUnavailable, envelopeHash);
        }

        if (!TryResolveHistory(request.Envelope, historyLookup, envelopeHash!, out var history, out var historyFailure))
        {
            return historyFailure!;
        }

        if (history is not null)
        {
            var existingHash = history.Receipt.CanonicalEnvelopeHash;
            var exactDelivery = request.Envelope.DeliveryId.Equals(history.Envelope.DeliveryId);
            var exactDeduplication = request.Envelope.DeduplicationId.Equals(history.Envelope.DeduplicationId);
            var exactEnvelope = exactDelivery && exactDeduplication && string.Equals(envelopeHash, existingHash, StringComparison.Ordinal);
            var permittedRedelivery = !exactDelivery && exactDeduplication && IsPermittedRedelivery(request.Envelope, history.Envelope);
            var stableSemantics = TriggerDeliveryReplayBindingHash.TryCompute(request.Envelope, out var replayBindingHash)
                && string.Equals(replayBindingHash, history.Receipt.ReplayBindingHash, StringComparison.Ordinal);
            if (exactEnvelope || permittedRedelivery && stableSemantics)
            {
                if (history.Receipt.Status is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
                    && ValidateCurrentEvidence(request, envelopeHash!, allowHistoricalAuthorityReceipt: true) is { } currentFailure)
                {
                    return currentFailure;
                }

                return Replay(history.Receipt, envelopeHash!);
            }

            return Result(TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict, envelopeHash);
        }

        if (request.Envelope.Redelivery.Attempt != 1 || request.Envelope.Redelivery.Count != 1)
        {
            return Result(TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope, envelopeHash);
        }

        return ValidateCurrentEvidence(request, envelopeHash!, allowHistoricalAuthorityReceipt: false) ?? Result(TriggerAdmissionStatus.Admitted, TriggerAdmissionReason.EvidenceAccepted, envelopeHash);
    }

    private static TriggerDeliveryAdmissionResult? ValidateCurrentEvidence(TriggerDeliveryAdmissionRequest request, string envelopeHash, bool allowHistoricalAuthorityReceipt)
    {
        var temporalState = TriggerTemporalEvaluator.Evaluate(request.Envelope.Temporal, request.EvaluatedAtUtc);
        if (temporalState == TriggerTemporalState.Expired)
        {
            return Result(TriggerAdmissionStatus.Expired, TriggerAdmissionReason.Expired, envelopeHash);
        }

        if (temporalState == TriggerTemporalState.DeadlineExceeded)
        {
            return Result(TriggerAdmissionStatus.Expired, TriggerAdmissionReason.DeadlineExceeded, envelopeHash);
        }

        if (temporalState == TriggerTemporalState.Unknown)
        {
            return Result(TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope, envelopeHash);
        }

        var preparedScheduleRecovery = request.PermitsPreparedScheduleRecovery
            && request.Envelope.Kind == TriggerKind.Time;

        // A durably prepared schedule may recover after the ordinary ingress-age window. Its explicit
        // deadline and expiry remain authoritative above, and all current evidence is still checked below.
        if (request.EvaluatedAtUtc < request.Envelope.Temporal.ReceivedAtUtc
            || !preparedScheduleRecovery
                && request.EvaluatedAtUtc - request.Envelope.Temporal.ReceivedAtUtc > TriggerDeliveryLimits.MaxAdmissionAge)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleDelivery, envelopeHash);
        }

        if (request.Envelope.Loop != request.CurrentLoop)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleLoop, envelopeHash);
        }

        if (request.Envelope.Adapter != request.CurrentAdapter)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAdapter, envelopeHash);
        }

        if (!request.IsAdapterAvailable)
        {
            return Result(TriggerAdmissionStatus.Unavailable, TriggerAdmissionReason.AdapterUnavailable, envelopeHash);
        }

        var mismatch = ActorMismatch(request.Envelope.ActorContext, request.CurrentActorContext);
        if (mismatch is not null)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, mismatch.Value, envelopeHash);
        }

        // Authenticated admitted history and internal prepared-schedule recovery freeze the envelope receipt.
        // They may refresh only its current-time proof; the exact selected profile and every other target,
        // adapter, and actor binding remain unchanged above. The schedule exception is closed to Time envelopes.
        var exactAuthority = allowHistoricalAuthorityReceipt || preparedScheduleRecovery
            ? request.Envelope.Authority.Profile == request.CurrentAuthority.Profile
            : TriggerAuthorityEvidenceEquality.Equals(request.Envelope.Authority, request.CurrentAuthority);
        if (!exactAuthority)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityMismatch, envelopeHash);
        }

        var authorityAge = request.EvaluatedAtUtc - request.CurrentAuthority.BoundaryReceipt.EvaluatedAtUtc;
        if (authorityAge < TimeSpan.Zero || authorityAge > TriggerDeliveryLimits.MaxAuthorityEvidenceAge)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.StaleAuthority, envelopeHash);
        }

        if (request.CurrentAuthority.BoundaryReceipt.Decision != AuthorityBoundaryDecision.Direct)
        {
            return Result(TriggerAdmissionStatus.Unauthorized, TriggerAdmissionReason.AuthorityBoundary, envelopeHash);
        }

        return temporalState == TriggerTemporalState.NotYetEligible
            ? Result(TriggerAdmissionStatus.NotYetEligible, TriggerAdmissionReason.NotBefore, envelopeHash)
            : null;
    }

    private static bool TryResolveHistory(TriggerDeliveryEnvelope envelope, TriggerDeliveryAdmissionHistoryLookupResult lookup, string envelopeHash, out TriggerDeliveryAdmissionHistoryEntry? history, out TriggerDeliveryAdmissionResult? failure)
    {
        history = null;
        failure = null;
        if (!IsValidHistoryMatch(lookup.DeliveryMatch, envelope.DeliveryId, null)
            || !IsValidHistoryMatch(lookup.DeduplicationMatch, null, envelope.DeduplicationId))
        {
            failure = Result(TriggerAdmissionStatus.Invalid, TriggerAdmissionReason.InvalidEnvelope, envelopeHash);
            return false;
        }

        if (lookup.DeliveryMatch is not null && lookup.DeduplicationMatch is not null && !IsSameHistoryEntry(lookup.DeliveryMatch, lookup.DeduplicationMatch))
        {
            failure = Result(TriggerAdmissionStatus.Conflicting, TriggerAdmissionReason.IdentityConflict, envelopeHash);
            return false;
        }

        history = lookup.DeliveryMatch ?? lookup.DeduplicationMatch;
        return true;
    }

    private static bool IsValidHistoryMatch(TriggerDeliveryAdmissionHistoryEntry? entry, TriggerDeliveryId? deliveryId, TriggerDeduplicationId? deduplicationId)
    {
        if (entry is null)
        {
            return true;
        }

        return entry.Envelope is not null
            && entry.Receipt is not null
            && TriggerDeliveryAdmissionReceiptFactory.Validate(entry.Receipt, entry.Envelope).IsValid
            && (deliveryId is null || entry.Envelope.DeliveryId.Equals(deliveryId))
            && (deduplicationId is null || entry.Envelope.DeduplicationId.Equals(deduplicationId));
    }

    private static bool IsSameHistoryEntry(TriggerDeliveryAdmissionHistoryEntry left, TriggerDeliveryAdmissionHistoryEntry right)
    {
        return left.Receipt == right.Receipt
            && string.Equals(left.Receipt.CanonicalEnvelopeHash, right.Receipt.CanonicalEnvelopeHash, StringComparison.Ordinal);
    }

    private static bool IsPermittedRedelivery(TriggerDeliveryEnvelope current, TriggerDeliveryEnvelope existing)
    {
        return current.Redelivery.Attempt > existing.Redelivery.Attempt
            && current.Redelivery.Count > existing.Redelivery.Count
            && current.Redelivery.OriginalDeliveryId.Equals(existing.Redelivery.OriginalDeliveryId)
            && current.Temporal.ReceivedAtUtc > existing.Temporal.ReceivedAtUtc;
    }

    private static TriggerDeliveryAdmissionResult Replay(TriggerDeliveryAdmissionReceipt receipt, string envelopeHash)
    {
        return receipt.Status is TriggerAdmissionStatus.Admitted or TriggerAdmissionStatus.Replayed
            ? new TriggerDeliveryAdmissionResult(TriggerAdmissionStatus.Replayed, TriggerAdmissionReason.ExactReplay, envelopeHash, true, receipt.Status, receipt.Reason)
            : new TriggerDeliveryAdmissionResult(receipt.Status, receipt.Reason, envelopeHash, true, receipt.Status, receipt.Reason);
    }

    private static TriggerAdmissionReason? ActorMismatch(TriggerActorContext expected, TriggerActorContext current)
    {
        if (!expected.ActorId.Equals(current.ActorId))
        {
            return TriggerAdmissionReason.ActorMismatch;
        }

        if (!string.Equals(expected.SurfaceId, current.SurfaceId, StringComparison.Ordinal))
        {
            return TriggerAdmissionReason.SurfaceMismatch;
        }

        if (!string.Equals(expected.WorkspaceId, current.WorkspaceId, StringComparison.Ordinal))
        {
            return TriggerAdmissionReason.WorkspaceMismatch;
        }

        return string.Equals(expected.RoleId, current.RoleId, StringComparison.Ordinal) ? null : TriggerAdmissionReason.RoleMismatch;
    }

    private static TriggerDeliveryAdmissionResult Result(TriggerAdmissionStatus status, TriggerAdmissionReason reason, string? hash) => new(status, reason, hash);
}
