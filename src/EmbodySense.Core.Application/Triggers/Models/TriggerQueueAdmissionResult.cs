using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Represents one non-dispatching queue-admission outcome without duplicating payload content.</summary>
/// <param name="Status">The closed queue outcome.</param>
/// <param name="Reason">The stable queue reason.</param>
/// <param name="DeliveryId">The delivery identity when available.</param>
/// <param name="DeduplicationId">The deduplication identity when available.</param>
/// <param name="CanonicalEnvelopeHash">The canonical envelope hash when available.</param>
/// <param name="Entry">The bounded durable entry summary when one exists.</param>
/// <param name="AdmissionStatus">The underlying delivery-admission status when evaluated.</param>
/// <param name="AdmissionReason">The underlying delivery-admission reason when evaluated.</param>
public sealed record TriggerQueueAdmissionResult(TriggerQueueAdmissionStatus Status, TriggerQueueAdmissionReason Reason, TriggerDeliveryId? DeliveryId, TriggerDeduplicationId? DeduplicationId, string? CanonicalEnvelopeHash, TriggerQueueEntry? Entry, TriggerAdmissionStatus? AdmissionStatus, TriggerAdmissionReason? AdmissionReason);
