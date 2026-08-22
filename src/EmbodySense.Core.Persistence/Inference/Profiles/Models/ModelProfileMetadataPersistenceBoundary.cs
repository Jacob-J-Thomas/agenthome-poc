namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

/// <summary>Identifies an observable crash boundary in model-profile metadata publication.</summary>
public enum ModelProfileMetadataPersistenceBoundary
{
    /// <summary>The last-proved source document is durable.</summary>
    ProofPublished = 1,
    /// <summary>The direct-successor source document is durable but trust may not yet be advanced.</summary>
    PrimaryPublished = 2,
    /// <summary>Server-owned monotonic trust recognizes the successor.</summary>
    TrustAdvanced = 3
}
