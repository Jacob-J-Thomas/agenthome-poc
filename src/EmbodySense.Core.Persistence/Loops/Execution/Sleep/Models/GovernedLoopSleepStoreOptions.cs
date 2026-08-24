namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

/// <summary>Configures bounded workspace sleep-checkpoint persistence and crash-boundary observation.</summary>
public sealed class GovernedLoopSleepStoreOptions
{
    /// <summary>Gets the default retained sleeping-checkpoint ceiling.</summary>
    public const int DefaultMaximumCheckpoints = 1_024;

    /// <summary>Gets the default canonical catalog byte ceiling.</summary>
    public const int DefaultMaximumCatalogUtf8Bytes = 8 * 1024 * 1024;

    /// <summary>Gets the default authenticated interrupted-cleanup artifact ceiling.</summary>
    public const int DefaultMaximumDurabilityArtifacts = 16;

    /// <summary>Gets the configured retained checkpoint ceiling.</summary>
    public int MaxCheckpoints { get; init; } = DefaultMaximumCheckpoints;

    /// <summary>Gets the configured canonical catalog byte ceiling.</summary>
    public int MaxCatalogUtf8Bytes { get; init; } = DefaultMaximumCatalogUtf8Bytes;

    /// <summary>Gets the configured authenticated interrupted-cleanup artifact ceiling.</summary>
    public int MaxDurabilityArtifacts { get; init; } = DefaultMaximumDurabilityArtifacts;

    /// <summary>Gets an optional synchronous observer invoked at exact durable publication boundaries.</summary>
    /// <remarks>Observer exceptions model abrupt process loss and therefore make the immediate result ambiguous.</remarks>
    public Action<GovernedLoopSleepStorePersistenceBoundary>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked after a native workspace-lock attempt is found contended.</summary>
    /// <remarks>This is an observational verification seam only; it does not participate in durability decisions.</remarks>
    public Action<string>? MutationLockContentionObserver { get; init; }
}
