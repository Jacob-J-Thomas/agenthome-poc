using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Validates composition-owned schema-version-1 trigger queue bounds.</summary>
public static class TriggerQueueQuotaValidator
{
    /// <summary>Validates that every bound is positive, internally ordered, and below the schema safety ceilings.</summary>
    /// <param name="quota">The exact quota to validate.</param>
    /// <exception cref="ArgumentNullException">The quota is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is zero, negative, inconsistent, or above a schema safety ceiling.</exception>
    public static void Validate(TriggerQueueQuota quota)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (quota.MaxQueuedEntries is < 1 or > 1_024)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxQueuedEntries));
        }

        if (quota.MaxRetainedEntries < quota.MaxQueuedEntries || quota.MaxRetainedEntries > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxRetainedEntries));
        }

        if (quota.MaxEntryBytes is < 1 or > 128 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxEntryBytes));
        }

        if (quota.MaxQueuedBytes < quota.MaxEntryBytes || quota.MaxQueuedBytes > 256L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxQueuedBytes));
        }

        if (quota.MaxRetainedBytes < quota.MaxQueuedBytes || quota.MaxRetainedBytes > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxRetainedBytes));
        }

        if (quota.MaxQueuedEntriesPerLoop < 1 || quota.MaxQueuedEntriesPerLoop > quota.MaxQueuedEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxQueuedEntriesPerLoop));
        }

        if (quota.MaxDurabilityTombstones is < 1 or > 120)
        {
            throw new ArgumentOutOfRangeException(nameof(quota.MaxDurabilityTombstones));
        }
    }
}
