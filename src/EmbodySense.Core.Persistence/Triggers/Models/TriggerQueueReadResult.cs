namespace EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Returns the latest immutable ledger content plus bounded live-generation and authenticated tombstone evidence.</summary>
internal sealed record TriggerQueueReadResult(IReadOnlyList<TriggerQueueArtifactSnapshot> Artifacts, byte[]? LatestContent, int TombstoneCount);
