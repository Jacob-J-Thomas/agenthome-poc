using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Startup.Tests.Triggers;

internal sealed class TriggerWorkerCurrentEvidenceWorkerHarness : ITriggerWorkerStatePort, ITriggerWorkerDispatcher, ITriggerWorkerDispatchReadinessPort
{
    private TriggerQueueEntry _entry;
    private readonly TriggerDeliveryEnvelope _envelope;

    internal TriggerWorkerCurrentEvidenceWorkerHarness(TriggerDeliveryEnvelope envelope, DateTimeOffset observedAtUtc)
    {
        _envelope = envelope;
        var lease = new TriggerWorkerLease("worker-1", 1, observedAtUtc, observedAtUtc.AddMinutes(1), 0);
        _entry = new TriggerQueueEntry(
            envelope.DeliveryId,
            envelope.DeduplicationId,
            envelope.Loop.LoopId,
            new string('a', 64),
            1,
            1,
            1,
            TriggerQueueEntryState.WorkerOwned,
            TriggerQueueTerminalReason.None,
            new TriggerQueueOrderKey(observedAtUtc, TriggerQueuePriority.Normal, observedAtUtc, envelope.DeliveryId.Value),
            1,
            observedAtUtc,
            null,
            TriggerAdmissionStatus.Admitted,
            TriggerAdmissionReason.EvidenceAccepted,
            lease);
    }

    internal int DurableIntentWrites { get; private set; }

    internal int ProviderCalls { get; private set; }

    internal int RejectionWrites { get; private set; }

    internal int ReleaseWrites { get; private set; }

    public Task<TriggerWorkerSelectionResult> SelectAsync(TriggerWorkerSelectionRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new TriggerWorkerSelectionResult(TriggerWorkerSelectionStatus.Acquired, 1, _entry, _envelope));

    public Task<TriggerWorkerDispatchReadinessResult> CheckAsync(TriggerDeliveryEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.FromResult(new TriggerWorkerDispatchReadinessResult(TriggerWorkerDispatchReadinessStatus.Ready));

    public Task<TriggerWorkerMutationResult> BeginDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
    {
        DurableIntentWrites++;
        throw new InvalidOperationException("A denied or unavailable authorizer must not record durable dispatch intent.");
    }

    public Task<TriggerWorkerMutationResult> RejectBeforeDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence rejection, CancellationToken cancellationToken = default)
    {
        RejectionWrites++;
        _entry = _entry with { State = TriggerQueueEntryState.DispatchRejected, Dispatch = rejection, Revision = _entry.Revision + 1 };
        return Task.FromResult(new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.Committed, 2, _entry));
    }

    public Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default)
    {
        ProviderCalls++;
        throw new InvalidOperationException("A denied or unavailable authorizer must not invoke a governed provider.");
    }

    public Task<TriggerWorkerMutationResult> RenewAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset renewedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("No renewal is valid before a denied or unavailable pre-dispatch authorization.");

    public Task<TriggerWorkerMutationResult> ReleaseAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset releasedAtUtc, CancellationToken cancellationToken = default)
    {
        ReleaseWrites++;
        _entry = _entry with
        {
            State = TriggerQueueEntryState.Queued,
            Revision = _entry.Revision + 1,
            WorkerLease = _entry.WorkerLease! with { ReleasedAtUtc = releasedAtUtc },
        };
        return Task.FromResult(new TriggerWorkerMutationResult(TriggerWorkerMutationStatus.Committed, 2, _entry));
    }

    public Task<TriggerWorkerMutationResult> CompleteDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence outcome, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("No dispatch outcome exists without durable dispatch intent.");
}
