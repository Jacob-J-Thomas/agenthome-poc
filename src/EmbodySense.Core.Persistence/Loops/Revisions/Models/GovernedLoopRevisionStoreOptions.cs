using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Loops.Revisions.Models;

/// <summary>Configures bounded governed-loop revision persistence and optional recovery observation.</summary>
public sealed class GovernedLoopRevisionStoreOptions
{
    /// <summary>Maximum graph lifecycle heads retained by one workspace.</summary>
    public const int MaximumGraphHeads = 1_024;

    /// <summary>Maximum immutable revision artifacts retained by one workspace.</summary>
    public const int MaximumRevisionArtifacts = 4_096;

    /// <summary>Maximum immutable operation-evidence records retained without eviction.</summary>
    public const int MaximumOperationEvidenceRecords = 8_192;

    /// <summary>Maximum UTF-8 bytes accepted for one authenticated store document.</summary>
    public const int MaximumArtifactUtf8Bytes = 4 * 1024 * 1024;

    /// <summary>Gets the configured graph-head ceiling.</summary>
    public int MaxGraphHeads { get; init; } = MaximumGraphHeads;

    /// <summary>Gets the configured immutable-revision ceiling.</summary>
    public int MaxRevisionArtifacts { get; init; } = MaximumRevisionArtifacts;

    /// <summary>Gets the configured immutable operation-evidence ceiling.</summary>
    public int MaxOperationEvidenceRecords { get; init; } = MaximumOperationEvidenceRecords;

    /// <summary>Gets the configured authenticated-document byte ceiling.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = MaximumArtifactUtf8Bytes;

    /// <summary>Gets an optional observer invoked after each named durable boundary.</summary>
    /// <remarks>Observer failures model process loss after the corresponding boundary and are never treated as proof that an outcome did not commit.</remarks>
    public Func<GovernedLoopRevisionPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked inside retained-handle path operations.</summary>
    /// <remarks>This deterministic safety seam supports path-substitution tests. Production callers should normally leave it unset.</remarks>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
