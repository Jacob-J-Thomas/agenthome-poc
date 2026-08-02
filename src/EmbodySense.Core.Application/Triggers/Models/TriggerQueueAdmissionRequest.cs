namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Requests fail-closed delivery evaluation and optional durable queue admission.</summary>
public sealed record TriggerQueueAdmissionRequest
{
    internal TriggerQueueAdmissionRequest(TriggerDeliveryAdmissionRequest deliveryRequest, TriggerQueueAdmissionMode mode, TriggerQueuePriority priority)
    {
        DeliveryRequest = deliveryRequest;
        Mode = mode;
        Priority = priority;
    }

    /// <summary>Gets the bounded delivery-admission request.</summary>
    public TriggerDeliveryAdmissionRequest DeliveryRequest { get; }

    /// <summary>Gets whether the request may wait durably.</summary>
    public TriggerQueueAdmissionMode Mode { get; }

    /// <summary>Gets the bounded later-selection priority.</summary>
    public TriggerQueuePriority Priority { get; }
}
