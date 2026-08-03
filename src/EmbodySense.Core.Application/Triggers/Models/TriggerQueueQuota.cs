namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines composition-owned schema-version-1 queue and retained-evidence bounds.</summary>
/// <param name="MaxQueuedEntries">The maximum number of nonterminal entries.</param>
/// <param name="MaxRetainedEntries">The maximum number of nonterminal and terminal entries retained together.</param>
/// <param name="MaxEntryBytes">The maximum retained byte reservation for one entry, including its largest supported metadata transition.</param>
/// <param name="MaxQueuedBytes">The maximum aggregate queued byte reservation, including receiptless promotion metadata.</param>
/// <param name="MaxRetainedBytes">The maximum aggregate retained byte reservation, including supported terminal transitions.</param>
/// <param name="MaxQueuedEntriesPerLoop">The maximum nonterminal entries for one loop identity.</param>
/// <param name="MaxDurabilityTombstones">The maximum authenticated Unix cleanup tombstones retained before persistence mutations are backpressured.</param>
public sealed record TriggerQueueQuota(int MaxQueuedEntries, int MaxRetainedEntries, int MaxEntryBytes, long MaxQueuedBytes, long MaxRetainedBytes, int MaxQueuedEntriesPerLoop, int MaxDurabilityTombstones = 120)
{
    /// <summary>Gets conservative defaults for the first experimental schema.</summary>
    public static TriggerQueueQuota Default { get; } = new(32, 128, 128 * 1024, 4 * 1024 * 1024, 16 * 1024 * 1024, 4, 120);

}
