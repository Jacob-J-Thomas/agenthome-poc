using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>
/// Pairs one server-owned canonical delivery with its durable terminal admission receipt.
/// </summary>
/// <remarks>The entry is replay evidence only and never grants execution, dispatch, queue, capability, or authority access.</remarks>
/// <param name="Envelope">The exact prior canonical envelope.</param>
/// <param name="Receipt">The terminal receipt bound to that envelope.</param>
public sealed record TriggerDeliveryAdmissionHistoryEntry(TriggerDeliveryEnvelope Envelope, TriggerDeliveryAdmissionReceipt Receipt);
