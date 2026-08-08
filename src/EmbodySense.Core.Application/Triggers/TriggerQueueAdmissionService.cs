using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Evaluates delivery evidence and commits accepted queue-mode outcomes without selecting or dispatching work.</summary>
public sealed class TriggerQueueAdmissionService : ITriggerQueueAdmissionPort
{
    private readonly ITriggerDeliveryAdmissionPort _deliveryAdmission;
    private readonly ITriggerQueueMutationPort _queue;

    /// <summary>Initializes the application boundary with composition-owned delivery and durability ports.</summary>
    /// <param name="deliveryAdmission">The exact delivery-admission boundary.</param>
    /// <param name="queue">The durable queue mutation boundary.</param>
    public TriggerQueueAdmissionService(ITriggerDeliveryAdmissionPort deliveryAdmission, ITriggerQueueMutationPort queue)
    {
        ArgumentNullException.ThrowIfNull(deliveryAdmission);
        ArgumentNullException.ThrowIfNull(queue);
        _deliveryAdmission = deliveryAdmission;
        _queue = queue;
    }

    /// <inheritdoc />
    public async Task<TriggerQueueAdmissionResult> AdmitAsync(TriggerQueueAdmissionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = request.DeliveryRequest.Envelope;
        if (request.Mode == TriggerQueueAdmissionMode.ImmediateOnly)
        {
            return Result(TriggerQueueAdmissionStatus.ImmediateRejected, TriggerQueueAdmissionReason.ImmediateModeBusy, envelope, null, null);
        }

        var admission = await _deliveryAdmission.AdmitAsync(request.DeliveryRequest, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (admission.Status == TriggerAdmissionStatus.Unavailable)
        {
            return Result(TriggerQueueAdmissionStatus.Unavailable, TriggerQueueAdmissionReason.AdmissionUnavailable, envelope, admission, null);
        }

        TriggerDeliveryAdmissionReceipt? receipt = null;
        if (admission.Status != TriggerAdmissionStatus.NotYetEligible
            && !TriggerDeliveryAdmissionReceiptFactory.TryCreate(envelope, admission.Status, admission.Reason, request.DeliveryRequest.EvaluatedAtUtc, out receipt, out _))
        {
            return Result(TriggerQueueAdmissionStatus.Unavailable, TriggerQueueAdmissionReason.AdmissionUnavailable, envelope, admission, null);
        }

        if (string.IsNullOrEmpty(admission.CanonicalEnvelopeHash))
        {
            return Result(TriggerQueueAdmissionStatus.Unavailable, TriggerQueueAdmissionReason.AdmissionUnavailable, envelope, admission, null);
        }

        var commit = new TriggerQueueCommitRequest(envelope, receipt, admission.Status, admission.Reason, admission.CanonicalEnvelopeHash, request.Priority, request.DeliveryRequest.EvaluatedAtUtc);
        return await _queue.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
    }

    private static TriggerQueueAdmissionResult Result(TriggerQueueAdmissionStatus status, TriggerQueueAdmissionReason reason, TriggerDeliveryEnvelope envelope, TriggerDeliveryAdmissionResult? admission, TriggerQueueEntry? entry)
    {
        return new TriggerQueueAdmissionResult(status, reason, envelope.DeliveryId, envelope.DeduplicationId, admission?.CanonicalEnvelopeHash, entry, admission?.Status, admission?.Reason);
    }
}
