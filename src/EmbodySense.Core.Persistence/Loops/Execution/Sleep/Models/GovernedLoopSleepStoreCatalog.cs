namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopSleepStoreCatalog(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<GovernedLoopSleepStoreEntry> Entries);
