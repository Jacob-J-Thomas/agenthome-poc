using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Creates bounded trigger queue admission requests.</summary>
public static class TriggerQueueAdmissionRequestFactory
{
    /// <summary>Creates a request after validating enum inputs and the nested application-created delivery request.</summary>
    /// <param name="deliveryRequest">The exact delivery-admission request.</param>
    /// <param name="mode">Whether durable waiting is allowed.</param>
    /// <param name="priority">The bounded later-selection priority.</param>
    /// <returns>The immutable request.</returns>
    /// <exception cref="ArgumentNullException">The delivery request is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is undefined.</exception>
    public static TriggerQueueAdmissionRequest Create(TriggerDeliveryAdmissionRequest deliveryRequest, TriggerQueueAdmissionMode mode = TriggerQueueAdmissionMode.Queued, TriggerQueuePriority priority = TriggerQueuePriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(deliveryRequest);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        return new TriggerQueueAdmissionRequest(deliveryRequest, mode, priority);
    }
}
