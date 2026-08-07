using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Application.Tests.Triggers;

internal sealed class TriggerDeliveryAdmissionHistoryStub : ITriggerDeliveryAdmissionHistoryPort
{
    private readonly IReadOnlyList<TriggerDeliveryAdmissionHistoryEntry> _entries;
    private readonly bool _isAvailable;

    internal TriggerDeliveryAdmissionHistoryStub(params TriggerDeliveryAdmissionHistoryEntry[] entries)
    {
        _entries = entries;
        _isAvailable = true;
    }

    private TriggerDeliveryAdmissionHistoryStub(bool isAvailable)
    {
        _entries = [];
        _isAvailable = isAvailable;
    }

    internal int QueryCount { get; private set; }

    internal TriggerDeliveryId? RequestedDeliveryId { get; private set; }

    internal TriggerDeduplicationId? RequestedDeduplicationId { get; private set; }

    internal static TriggerDeliveryAdmissionHistoryStub Unavailable() => new(false);

    public Task<TriggerDeliveryAdmissionHistoryLookupResult> FindAsync(TriggerDeliveryId deliveryId, TriggerDeduplicationId deduplicationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryCount++;
        RequestedDeliveryId = deliveryId;
        RequestedDeduplicationId = deduplicationId;
        if (!_isAvailable)
        {
            return Task.FromResult(new TriggerDeliveryAdmissionHistoryLookupResult(TriggerDeliveryAdmissionHistoryLookupStatus.Unavailable, null, null));
        }

        var deliveryMatch = _entries.SingleOrDefault(entry => entry.Envelope.DeliveryId.Equals(deliveryId));
        var deduplicationMatch = _entries.SingleOrDefault(entry => entry.Envelope.DeduplicationId.Equals(deduplicationId));
        return Task.FromResult(new TriggerDeliveryAdmissionHistoryLookupResult(TriggerDeliveryAdmissionHistoryLookupStatus.Available, deliveryMatch, deduplicationMatch));
    }
}
