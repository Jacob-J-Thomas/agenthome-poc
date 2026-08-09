namespace EmbodySense.Core.Startup.Triggers.Models;

/// <summary>Projects one bounded durable queue posture.</summary>
/// <param name="Generation">The exact queue generation.</param>
/// <param name="PersistenceBackpressured">Whether persistence cannot reserve the next mutation.</param>
/// <param name="Entries">The deterministic bounded entry posture.</param>
public sealed record TriggerWorkerQueueSnapshot(long Generation, bool PersistenceBackpressured, IReadOnlyList<TriggerWorkerEntrySnapshot> Entries);
