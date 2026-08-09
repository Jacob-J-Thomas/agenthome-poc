using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Describes one bounded durable queue entry without granting selection or execution authority.</summary>
/// <param name="DeliveryId">The original delivery identity.</param>
/// <param name="DeduplicationId">The stable deduplication identity.</param>
/// <param name="LoopId">The pinned loop identity used for fairness accounting.</param>
/// <param name="CanonicalEnvelopeHash">The canonical envelope hash.</param>
/// <param name="SerializedEntryBytes">The exact canonical serialized ledger-entry byte length, including envelope and metadata.</param>
/// <param name="QueuedReservationBytes">The byte reservation enforced while the entry is nonterminal, including receiptless promotion metadata, or zero after terminalization.</param>
/// <param name="RetainedReservationBytes">The byte reservation enforced while the entry is retained, including its largest supported terminal metadata transition.</param>
/// <param name="State">The durable queue state.</param>
/// <param name="TerminalReason">The terminal reason, or <see cref="TriggerQueueTerminalReason.None"/>.</param>
/// <param name="OrderKey">The deterministic later-selection inputs.</param>
/// <param name="Revision">The monotonically increasing entry revision.</param>
/// <param name="RecordedAtUtc">The durable record creation instant.</param>
/// <param name="TerminalAtUtc">The terminal transition instant, or <see langword="null"/>.</param>
/// <param name="AdmissionStatus">The delivery admission status recorded as evidence.</param>
/// <param name="AdmissionReason">The delivery admission reason recorded as evidence.</param>
public sealed record TriggerQueueEntry(TriggerDeliveryId DeliveryId, TriggerDeduplicationId DeduplicationId, string LoopId, string CanonicalEnvelopeHash, int SerializedEntryBytes, int QueuedReservationBytes, int RetainedReservationBytes, TriggerQueueEntryState State, TriggerQueueTerminalReason TerminalReason, TriggerQueueOrderKey OrderKey, long Revision, DateTimeOffset RecordedAtUtc, DateTimeOffset? TerminalAtUtc, TriggerAdmissionStatus AdmissionStatus, TriggerAdmissionReason AdmissionReason);
