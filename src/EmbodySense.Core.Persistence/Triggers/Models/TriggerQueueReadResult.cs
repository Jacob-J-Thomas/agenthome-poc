namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Returns the latest immutable ledger content plus bounded live-generation, tombstone, and exact preserved-precursor evidence.</summary>
internal sealed record TriggerQueueReadResult(IReadOnlyList<TriggerQueueArtifactSnapshot> Artifacts, byte[]? LatestContent, int TombstoneCount, IReadOnlyList<TriggerQueueArtifactSnapshot> Precursors);
