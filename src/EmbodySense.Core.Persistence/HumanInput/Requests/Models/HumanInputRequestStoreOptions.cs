using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

/// <summary>Configures bounded Human Input request persistence and deterministic recovery observation.</summary>
public sealed class HumanInputRequestStoreOptions
{
    /// <summary>Gets the configured request-head ceiling.</summary>
    public int MaxRequests { get; init; } = HumanInputRequestLifecycleContractLimits.MaxRequestsPerStore;

    /// <summary>Gets the configured immutable request-version ceiling.</summary>
    public int MaxRequestVersions { get; init; } = HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerStore;

    /// <summary>Gets the configured append-only operation-evidence ceiling.</summary>
    public int MaxOperations { get; init; } = HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore;

    /// <summary>Gets the configured authenticated-document byte ceiling.</summary>
    public int MaxArtifactUtf8Bytes { get; init; } = HumanInputRequestLifecycleContractLimits.MaxStoreDocumentUtf8Bytes;

    /// <summary>Gets an optional observer invoked after each named durable boundary.</summary>
    /// <remarks>Observer failures model process loss after the corresponding boundary and never prove that no durable outcome exists.</remarks>
    public Func<HumanInputRequestPersistenceBoundary, CancellationToken, ValueTask>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked inside retained-handle path operations.</summary>
    /// <remarks>This deterministic safety seam supports path-substitution tests. Production callers should normally leave it unset.</remarks>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
