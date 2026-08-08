namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Provides deterministic later-selection inputs without selecting or dispatching an entry.</summary>
/// <param name="EligibleAtUtc">The first UTC instant at which the admitted entry is eligible.</param>
/// <param name="Priority">The bounded priority, ordered from critical to background.</param>
/// <param name="AcceptedAtUtc">The durable admission instant.</param>
/// <param name="DeliveryId">The stable final ordinal tie-breaker.</param>
public sealed record TriggerQueueOrderKey(DateTimeOffset EligibleAtUtc, TriggerQueuePriority Priority, DateTimeOffset AcceptedAtUtc, string DeliveryId);
