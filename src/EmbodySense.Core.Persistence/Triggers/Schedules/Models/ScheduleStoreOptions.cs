namespace EmbodySense.Core.Persistence.Triggers.Schedules.Models;

/// <summary>Configures bounded workspace schedule persistence and optional crash-boundary observation.</summary>
public sealed class ScheduleStoreOptions
{
    /// <summary>Gets the default maximum number of schedules retained by one workspace.</summary>
    public const int DefaultMaximumSchedules = 256;

    /// <summary>Gets the default maximum canonical UTF-8 bytes retained by one catalog generation.</summary>
    public const int DefaultMaximumCatalogUtf8Bytes = 4 * 1024 * 1024;

    /// <summary>Gets the default maximum authenticated Unix cleanup artifacts retained at one time.</summary>
    public const int DefaultMaximumDurabilityArtifacts = 16;

    /// <summary>Gets the configured retained schedule count ceiling.</summary>
    public int MaxSchedules { get; init; } = DefaultMaximumSchedules;

    /// <summary>Gets the configured canonical catalog byte ceiling.</summary>
    public int MaxCatalogUtf8Bytes { get; init; } = DefaultMaximumCatalogUtf8Bytes;

    /// <summary>Gets the configured simultaneous Unix cleanup-artifact ceiling.</summary>
    public int MaxDurabilityArtifacts { get; init; } = DefaultMaximumDurabilityArtifacts;

    /// <summary>Gets an optional synchronous observer invoked at exact durable publication boundaries.</summary>
    /// <remarks>Observer exceptions model abrupt process loss and never prove that a mutation did not commit.</remarks>
    public Action<ScheduleStorePersistenceBoundary>? DurableBoundaryObserver { get; init; }
}
