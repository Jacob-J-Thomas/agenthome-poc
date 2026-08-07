using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Holds one validated internal schema-version-1 ledger entry.</summary>
internal sealed record TriggerQueueLedgerEntry(TriggerDeliveryEnvelope Envelope, string CanonicalEnvelope, TriggerDeliveryAdmissionReceipt? Receipt, TriggerAdmissionStatus AdmissionStatus, TriggerAdmissionReason AdmissionReason, string CanonicalEnvelopeHash, TriggerQueuePriority Priority, TriggerQueueEntryState State, TriggerQueueTerminalReason TerminalReason, long Revision, DateTimeOffset RecordedAtUtc, DateTimeOffset? TerminalAtUtc);
