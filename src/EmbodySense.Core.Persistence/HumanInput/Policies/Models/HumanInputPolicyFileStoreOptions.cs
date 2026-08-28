using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Configures bounded schema-1 Human Input policy artifact persistence.</summary>
public sealed class HumanInputPolicyFileStoreOptions
{
    /// <summary>Gets the maximum number of immutable policy artifacts retained by one workspace source.</summary>
    public int MaximumArtifacts { get; init; } = 128;

    /// <summary>Gets the maximum UTF-8 bytes accepted for one canonical policy artifact.</summary>
    public int MaximumArtifactUtf8Bytes { get; init; } = 16 * 1024;

    /// <summary>Gets an optional observer invoked after each semantic publication boundary.</summary>
    /// <remarks>Observer failures model abrupt process loss and never prove that a policy publication did not commit. POSIX boundaries follow retained-parent directory barriers; Windows recovery does not infer a directory-flush ordering guarantee.</remarks>
    public Action<HumanInputPolicyFileStorePublicationBoundary>? DurableBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked inside exact retained-parent publication and retirement windows.</summary>
    /// <remarks>Observers support deterministic power-loss and cleanup evaluation. Production callers should normally leave this unset.</remarks>
    public Func<HumanInputPolicyFileStorePublicationPart, HumanInputPolicyFileStorePhysicalPersistenceBoundary, CancellationToken, ValueTask>? PhysicalBoundaryObserver { get; init; }

    /// <summary>Gets an optional observer invoked inside retained-handle policy-root resolution.</summary>
    /// <remarks>This deterministic safety seam supports no-follow ancestor-substitution tests. Production callers should normally leave it unset.</remarks>
    public ICapabilityCatalogPathObserver? PathObserver { get; init; }
}
