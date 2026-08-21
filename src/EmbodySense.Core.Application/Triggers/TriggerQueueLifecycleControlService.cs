using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Orchestrates trusted-time delivery and bounded all-pending cancellation over canonical queue ports.</summary>
/// <remarks>
/// This service never interrupts a provider directly. Cancelling after durable dispatch intent preserves the queue port's
/// fail-closed <c>NeedsReview</c> outcome. Batch operations are intentionally finite and report partial durable progress.
/// </remarks>
public sealed class TriggerQueueLifecycleControlService
{
    /// <summary>Gets the maximum matching entries admitted by one all-pending request.</summary>
    public const int MaximumPendingCancellationCount = 100;

    private readonly ITriggerQueueCancellationPort _cancellation;
    private readonly ITriggerQueueQueryPort _query;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates one lifecycle-control service over exact queue query and mutation ports.</summary>
    public TriggerQueueLifecycleControlService(ITriggerQueueQueryPort query, ITriggerQueueCancellationPort cancellation, TimeProvider? timeProvider = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Cancels one exact delivery revision through the canonical optimistic queue boundary.</summary>
    public async Task<TriggerQueueDeliveryCancellationResult> CancelDeliveryAsync(string deliveryId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (!TriggerDeliveryId.TryParse(deliveryId, out var parsed) || expectedRevision is < 1 or long.MaxValue)
        {
            return Delivery(TriggerQueueDeliveryCancellationStatus.Invalid, "trigger-cancellation-request-invalid");
        }

        if (!TryGetUtcNow(out var cancelledAtUtc))
        {
            return Delivery(TriggerQueueDeliveryCancellationStatus.Unavailable, "trigger-cancellation-clock-unavailable");
        }

        TriggerQueueCancellationResult result;
        try
        {
            result = await _cancellation.CancelAsync(parsed!, expectedRevision, cancelledAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Delivery(TriggerQueueDeliveryCancellationStatus.Unavailable, "trigger-cancellation-unavailable");
        }

        return Map(result, parsed!, expectedRevision);
    }

    /// <summary>Cancels every currently nonterminal delivery for one loop when the complete set fits the caller's bound.</summary>
    public async Task<TriggerQueuePendingCancellationResult> CancelPendingForLoopAsync(string loopId, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(loopId, TriggerDeliveryLimits.MaxLoopIdCharacters)
            || maximumCount is < 1 or > MaximumPendingCancellationCount)
        {
            return Pending(TriggerQueuePendingCancellationStatus.Invalid, reasonCode: "trigger-pending-cancellation-request-invalid");
        }

        if (!TryGetUtcNow(out var observedAtUtc))
        {
            return Pending(TriggerQueuePendingCancellationStatus.Unavailable, reasonCode: "trigger-cancellation-clock-unavailable");
        }

        TriggerQueueSnapshot snapshot;
        try
        {
            snapshot = await _query.GetSnapshotAsync(observedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Pending(TriggerQueuePendingCancellationStatus.Unavailable, reasonCode: "trigger-queue-unavailable");
        }

        if (!TriggerQueueSnapshotEvidenceContract.IsValid(snapshot))
        {
            return Pending(TriggerQueuePendingCancellationStatus.Unavailable, reasonCode: "trigger-queue-evidence-invalid");
        }
        if (snapshot.PersistenceBackpressured)
        {
            return Pending(TriggerQueuePendingCancellationStatus.Backpressured, reasonCode: "trigger-queue-backpressured");
        }

        var matches = snapshot.Entries
            .Where(entry => string.Equals(entry.LoopId, loopId, StringComparison.Ordinal) && IsNonterminal(entry.State))
            .ToArray();
        if (matches.Length == 0)
        {
            return Pending(TriggerQueuePendingCancellationStatus.NoMatches, reasonCode: "trigger-pending-cancellation-empty");
        }
        if (matches.Length > maximumCount)
        {
            return Pending(TriggerQueuePendingCancellationStatus.BoundExceeded, matches.Length, reasonCode: "trigger-pending-cancellation-bound-exceeded");
        }

        var applied = 0;
        var needsReview = 0;
        TriggerQueuePendingCancellationStatus? failure = null;
        foreach (var entry in matches)
        {
            TriggerQueueCancellationResult cancellation;
            try
            {
                cancellation = await _cancellation.CancelAsync(entry.DeliveryId, entry.Revision, observedAtUtc, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failure = TriggerQueuePendingCancellationStatus.Unavailable;
                break;
            }

            var mapped = Map(cancellation, entry.DeliveryId, entry.Revision);
            switch (mapped.Status)
            {
                case TriggerQueueDeliveryCancellationStatus.Applied:
                    applied++;
                    if (mapped.Entry?.State == TriggerQueueEntryState.NeedsReview)
                    {
                        needsReview++;
                    }
                    break;
                case TriggerQueueDeliveryCancellationStatus.AlreadyTerminal:
                case TriggerQueueDeliveryCancellationStatus.Conflict:
                case TriggerQueueDeliveryCancellationStatus.NotFound:
                    failure = TriggerQueuePendingCancellationStatus.Conflict;
                    break;
                case TriggerQueueDeliveryCancellationStatus.Backpressured:
                    failure = TriggerQueuePendingCancellationStatus.Backpressured;
                    break;
                default:
                    failure = TriggerQueuePendingCancellationStatus.Unavailable;
                    break;
            }

            if (failure is not null)
            {
                break;
            }
        }

        if (failure is not null)
        {
            return Pending(
                applied > 0 ? TriggerQueuePendingCancellationStatus.PartiallyApplied : failure.Value,
                matches.Length,
                applied,
                needsReview,
                applied > 0 ? "trigger-pending-cancellation-partial" : Reason(failure.Value));
        }

        return Pending(TriggerQueuePendingCancellationStatus.Applied, matches.Length, applied, needsReview, "trigger-pending-cancellation-applied");
    }

    private TriggerQueueDeliveryCancellationResult Map(
        TriggerQueueCancellationResult? result,
        TriggerDeliveryId expectedDelivery,
        long expectedRevision)
    {
        if (result is null
            || !Enum.IsDefined(result.Status)
            || result.Entry is not null
                && (!TriggerQueueSnapshotEvidenceContract.IsValid(result.Entry)
                    || !result.Entry.DeliveryId.Equals(expectedDelivery)))
        {
            return Delivery(TriggerQueueDeliveryCancellationStatus.Unavailable, "trigger-cancellation-evidence-invalid");
        }

        return result.Status switch
        {
            TriggerQueueCancellationStatus.Cancelled when result.Entry is not null
                && result.Entry.Revision == expectedRevision + 1
                && result.Entry.State is TriggerQueueEntryState.Cancelled or TriggerQueueEntryState.NeedsReview
                => Delivery(TriggerQueueDeliveryCancellationStatus.Applied, "trigger-cancellation-applied", result.Entry),
            TriggerQueueCancellationStatus.AlreadyTerminal when result.Entry is not null && !IsNonterminal(result.Entry.State)
                => Delivery(TriggerQueueDeliveryCancellationStatus.AlreadyTerminal, "trigger-cancellation-already-terminal", result.Entry),
            TriggerQueueCancellationStatus.NotFound when result.Entry is null
                => Delivery(TriggerQueueDeliveryCancellationStatus.NotFound, "trigger-cancellation-not-found"),
            TriggerQueueCancellationStatus.RevisionConflict when result.Entry is not null && result.Entry.Revision != expectedRevision
                => Delivery(TriggerQueueDeliveryCancellationStatus.Conflict, "trigger-cancellation-revision-conflict", result.Entry),
            TriggerQueueCancellationStatus.PersistenceBackpressured when result.Entry is null
                => Delivery(TriggerQueueDeliveryCancellationStatus.Backpressured, "trigger-cancellation-backpressured"),
            TriggerQueueCancellationStatus.Unavailable when result.Entry is null
                => Delivery(TriggerQueueDeliveryCancellationStatus.Unavailable, "trigger-cancellation-unavailable"),
            _ => Delivery(TriggerQueueDeliveryCancellationStatus.Unavailable, "trigger-cancellation-evidence-invalid")
        };
    }

    private bool TryGetUtcNow(out DateTimeOffset utcNow)
    {
        try
        {
            utcNow = _timeProvider.GetUtcNow();
        }
        catch (Exception)
        {
            utcNow = default;
            return false;
        }

        return utcNow != default && utcNow.Offset == TimeSpan.Zero;
    }

    private static bool IsNonterminal(TriggerQueueEntryState state)
        => state is TriggerQueueEntryState.Queued or TriggerQueueEntryState.WorkerOwned or TriggerQueueEntryState.Dispatching;

    private static string Reason(TriggerQueuePendingCancellationStatus status)
        => status switch
        {
            TriggerQueuePendingCancellationStatus.Conflict => "trigger-pending-cancellation-conflict",
            TriggerQueuePendingCancellationStatus.Backpressured => "trigger-pending-cancellation-backpressured",
            _ => "trigger-pending-cancellation-unavailable"
        };

    private static TriggerQueueDeliveryCancellationResult Delivery(TriggerQueueDeliveryCancellationStatus status, string reasonCode, TriggerQueueEntry? entry = null)
        => new(status, entry, reasonCode);

    private static TriggerQueuePendingCancellationResult Pending(
        TriggerQueuePendingCancellationStatus status,
        int matchedCount = 0,
        int appliedCount = 0,
        int needsReviewCount = 0,
        string reasonCode = "trigger-pending-cancellation")
        => new(status, matchedCount, appliedCount, needsReviewCount, reasonCode);
}
