namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

/// <summary>Identifies an observable crash boundary in model-usage ledger publication.</summary>
public enum GovernedModelUsageLedgerPersistenceBoundary
{
    /// <summary>The last-proved ledger is durable.</summary>
    ProofPublished = 1,
    /// <summary>The append-only direct successor is durable but trust may not yet be advanced.</summary>
    PrimaryPublished = 2,
    /// <summary>Server-owned monotonic trust recognizes the successor.</summary>
    TrustAdvanced = 3
}
