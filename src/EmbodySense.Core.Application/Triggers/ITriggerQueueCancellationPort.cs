using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Defines optimistic cancellation of queued trigger evidence without affecting a worker or actuator.</summary>
public interface ITriggerQueueCancellationPort
{
    /// <summary>Cancels one matching nonterminal entry at its expected revision.</summary>
    /// <param name="deliveryId">The delivery identity.</param>
    /// <param name="expectedRevision">The exact entry revision observed by the caller.</param>
    /// <param name="cancelledAtUtc">The exact UTC cancellation instant.</param>
    /// <param name="cancellationToken">A token honored before the durable cancellation commit begins.</param>
    /// <returns>The closed optimistic cancellation outcome.</returns>
    Task<TriggerQueueCancellationResult> CancelAsync(TriggerDeliveryId deliveryId, long expectedRevision, DateTimeOffset cancelledAtUtc, CancellationToken cancellationToken = default);
}
