namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopCoordinatorEvidenceStoreCatalog(
    int SchemaVersion,
    long Generation,
    IReadOnlyList<GovernedLoopCoordinatorEvidenceStoreEntry> Entries);
