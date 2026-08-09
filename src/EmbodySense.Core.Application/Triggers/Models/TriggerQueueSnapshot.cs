namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Returns bounded durable queue evidence and fairness inputs without selecting an entry.</summary>
/// <param name="SchemaVersion">The only supported persisted schema version.</param>
/// <param name="Generation">The monotonically increasing ledger generation.</param>
/// <param name="Quota">The exact composition-owned quota persisted with the ledger.</param>
/// <param name="QueuedEntries">The current nonterminal count.</param>
/// <param name="QueuedBytes">The current nonterminal canonical serialized entry bytes.</param>
/// <param name="QueuedReservationBytes">The current aggregate queued byte reservation enforced for capacity admission.</param>
/// <param name="RetainedEntries">The current retained count.</param>
/// <param name="RetainedBytes">The current retained canonical serialized entry bytes.</param>
/// <param name="RetainedReservationBytes">The current aggregate retained byte reservation enforced for capacity admission.</param>
/// <param name="DurabilityTombstones">The current authenticated Unix cleanup tombstone count.</param>
/// <param name="PersistenceBackpressured">Whether the configured tombstone quota cannot reserve the worst-case next persistence mutation.</param>
/// <param name="Entries">The stable entry summaries ordered by eligibility, descending bounded priority, acceptance time, then delivery identity.</param>
public sealed record TriggerQueueSnapshot(int SchemaVersion, long Generation, TriggerQueueQuota Quota, int QueuedEntries, long QueuedBytes, long QueuedReservationBytes, int RetainedEntries, long RetainedBytes, long RetainedReservationBytes, int DurabilityTombstones, bool PersistenceBackpressured, IReadOnlyList<TriggerQueueEntry> Entries)
{
    /// <summary>Gets the only supported experimental queue-ledger schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
