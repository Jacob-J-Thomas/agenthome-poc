using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

/// <summary>Configures bounded server-owned model-profile metadata history.</summary>
public sealed class ModelProfileMetadataStoreOptions
{
    /// <summary>The schema-1 maximum distinct profiles.</summary>
    public const int MaximumProfiles = 512;
    /// <summary>The schema-1 maximum retained revisions across all profiles.</summary>
    public const int MaximumRevisions = 8_192;
    /// <summary>The schema-1 maximum authenticated source-document size.</summary>
    public const int MaximumArtifactUtf8Bytes = 16 * 1024 * 1024;

    /// <summary>Gets the configured distinct-profile ceiling.</summary>
    public int MaxProfiles { get; init; } = MaximumProfiles;
    /// <summary>Gets the configured append-only revision ceiling.</summary>
    public int MaxRevisions { get; init; } = MaximumRevisions;
    /// <summary>Gets the configured authenticated-document byte ceiling.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = MaximumArtifactUtf8Bytes;
    /// <summary>Gets an optional observer invoked after durable publication boundaries.</summary>
    public Func<ModelProfileMetadataPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }
    /// <summary>Gets an optional retained-handle path observer.</summary>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
