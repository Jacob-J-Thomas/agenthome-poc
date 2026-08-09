namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>
/// Returns bounded server-owned history matches without collapsing delivery and deduplication identities.
/// </summary>
/// <param name="Status">Whether history was inspected safely.</param>
/// <param name="DeliveryMatch">The one entry matching the requested delivery identity, or <see langword="null"/>.</param>
/// <param name="DeduplicationMatch">The one entry matching the requested deduplication identity, or <see langword="null"/>.</param>
public sealed record TriggerDeliveryAdmissionHistoryLookupResult(TriggerDeliveryAdmissionHistoryLookupStatus Status, TriggerDeliveryAdmissionHistoryEntry? DeliveryMatch, TriggerDeliveryAdmissionHistoryEntry? DeduplicationMatch);
