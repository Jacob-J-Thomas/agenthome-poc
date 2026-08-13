namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Binds one occurrence to its deterministic trigger delivery and deduplication identities.</summary>
/// <param name="OccurrenceId">The deterministic occurrence identity.</param>
/// <param name="DeliveryId">The deterministic trigger delivery identity.</param>
/// <param name="DeduplicationId">The deterministic trigger idempotency identity.</param>
public sealed record ScheduleOccurrenceIdentity(
    ScheduleOccurrenceId OccurrenceId,
    TriggerDeliveryId DeliveryId,
    TriggerDeduplicationId DeduplicationId);
