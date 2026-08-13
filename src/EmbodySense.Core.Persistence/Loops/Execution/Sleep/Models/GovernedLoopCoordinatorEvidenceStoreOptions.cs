namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

/// <summary>Configures bounded append-only local coordinator evidence persistence.</summary>
public sealed class GovernedLoopCoordinatorEvidenceStoreOptions
{
    /// <summary>Gets the default retained coordinator ceiling.</summary>
    public const int DefaultMaximumCoordinators = 16;

    /// <summary>Gets the default retained evidence item ceiling per coordinator.</summary>
    public const int DefaultMaximumEvidenceItemsPerCoordinator = 4_096;

    /// <summary>Gets the default canonical catalog byte ceiling.</summary>
    public const int DefaultMaximumCatalogUtf8Bytes = 8 * 1024 * 1024;

    /// <summary>Gets the default authenticated interrupted-cleanup artifact ceiling.</summary>
    public const int DefaultMaximumDurabilityArtifacts = 16;

    /// <summary>Gets the configured coordinator count ceiling.</summary>
    public int MaxCoordinators { get; init; } = DefaultMaximumCoordinators;

    /// <summary>Gets the configured aggregate evidence-item ceiling per coordinator.</summary>
    public int MaxEvidenceItemsPerCoordinator { get; init; } = DefaultMaximumEvidenceItemsPerCoordinator;

    /// <summary>Gets the configured canonical catalog byte ceiling.</summary>
    public int MaxCatalogUtf8Bytes { get; init; } = DefaultMaximumCatalogUtf8Bytes;

    /// <summary>Gets the configured authenticated interrupted-cleanup artifact ceiling.</summary>
    public int MaxDurabilityArtifacts { get; init; } = DefaultMaximumDurabilityArtifacts;

    /// <summary>Gets an optional observer invoked at exact durable publication boundaries.</summary>
    public Action<GovernedLoopSleepStorePersistenceBoundary>? DurableBoundaryObserver { get; init; }
}
