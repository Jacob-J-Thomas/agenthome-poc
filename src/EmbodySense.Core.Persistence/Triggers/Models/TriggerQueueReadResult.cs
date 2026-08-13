namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Returns the latest immutable ledger content plus bounded live-generation, tombstone, and exact preserved-precursor evidence.</summary>
internal sealed record TriggerQueueReadResult(
    IReadOnlyList<TriggerQueueArtifactSnapshot> Artifacts,
    byte[]? LatestContent,
    IReadOnlyList<TriggerQueueArtifactSnapshot> Tombstones,
    IReadOnlyList<TriggerQueueArtifactSnapshot> Precursors)
{
    /// <summary>Gets the authenticated Unix cleanup-artifact count.</summary>
    public int TombstoneCount => Tombstones.Count;
}
