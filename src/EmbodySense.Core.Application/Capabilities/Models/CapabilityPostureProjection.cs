namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one bounded redacted human or administrative capability posture.</summary>
/// <param name="Id">The canonical capability identifier.</param>
/// <param name="Version">The exact capability version.</param>
/// <param name="DescriptorHash">The exact canonical descriptor hash.</param>
/// <param name="Kind">The closed capability kind.</param>
/// <param name="Purpose">The bounded public capability purpose.</param>
/// <param name="ProviderId">The public provider identity.</param>
/// <param name="ImplementationId">The public implementation identity, never private configuration.</param>
/// <param name="ProvenanceKind">The safe provenance category.</param>
/// <param name="SourceUri">The safe source URI without user information, query, fragment, or local file path.</param>
/// <param name="SourceRevision">The optional bounded source revision.</param>
/// <param name="Integrity">The optional public provenance integrity digest.</param>
/// <param name="HostVersionRange">The declared compatible host range.</param>
/// <param name="SupportedPlatforms">The bounded canonical platform tuples.</param>
/// <param name="IsCurrentHostCompatible">Whether the current host and platform satisfy the declaration.</param>
/// <param name="SideEffectClass">The declared maximum side-effect class; this is not an authority grant.</param>
/// <param name="DataClasses">The declared data classes.</param>
/// <param name="EgressMode">The declared network-egress posture.</param>
/// <param name="EgressDestinations">The declared restricted destinations.</param>
/// <param name="SecretRequirements">Secret reference names only, never resolved values.</param>
/// <param name="State">The current summarized posture.</param>
/// <param name="Declaration">The server-owned declaration state.</param>
/// <param name="Installation">The server-owned installation state.</param>
/// <param name="Enablement">The server-owned enablement state.</param>
/// <param name="Health">The server-owned health state.</param>
/// <param name="Retirement">The server-owned retirement state.</param>
/// <param name="Trust">The server-owned trust state.</param>
/// <param name="IsLifecycleEnabled">Whether the authenticated lifecycle aggregate currently permits admission.</param>
/// <param name="IsRemoved">Whether catalog or lifecycle evidence tombstones the identity.</param>
/// <param name="EntryRevision">The exact catalog entry revision.</param>
/// <param name="LifecycleRevision">The optional exact lifecycle aggregate entry revision.</param>
/// <param name="IsRecovered">Whether any projected evidence came from an explicit last-proved read.</param>
/// <param name="Dependents">The deterministic bounded dependent projections.</param>
/// <param name="AreDependentsAvailable">Whether the complete registered dependent set was proved.</param>
/// <param name="DependentsTruncated">Whether additional safe dependent projections exceeded the response bound.</param>
public sealed record CapabilityPostureProjection(
    string Id,
    string Version,
    string DescriptorHash,
    string Kind,
    string Purpose,
    string ProviderId,
    string ImplementationId,
    string ProvenanceKind,
    string SourceUri,
    string? SourceRevision,
    string? Integrity,
    string HostVersionRange,
    IReadOnlyList<string> SupportedPlatforms,
    bool IsCurrentHostCompatible,
    string SideEffectClass,
    IReadOnlyList<string> DataClasses,
    string EgressMode,
    IReadOnlyList<string> EgressDestinations,
    IReadOnlyList<string> SecretRequirements,
    CapabilityPostureState State,
    string Declaration,
    string Installation,
    string Enablement,
    string Health,
    string Retirement,
    string Trust,
    bool IsLifecycleEnabled,
    bool IsRemoved,
    long EntryRevision,
    long? LifecycleRevision,
    bool IsRecovered,
    IReadOnlyList<CapabilityPostureDependentProjection> Dependents,
    bool AreDependentsAvailable,
    bool DependentsTruncated)
{
    /// <summary>Gets a defensive read-only platform snapshot.</summary>
    public IReadOnlyList<string> SupportedPlatforms { get; } = Array.AsReadOnly((SupportedPlatforms ?? []).ToArray());

    /// <summary>Gets a defensive read-only data-class snapshot.</summary>
    public IReadOnlyList<string> DataClasses { get; } = Array.AsReadOnly((DataClasses ?? []).ToArray());

    /// <summary>Gets a defensive read-only egress-destination snapshot.</summary>
    public IReadOnlyList<string> EgressDestinations { get; } = Array.AsReadOnly((EgressDestinations ?? []).ToArray());

    /// <summary>Gets a defensive read-only secret-reference snapshot.</summary>
    public IReadOnlyList<string> SecretRequirements { get; } = Array.AsReadOnly((SecretRequirements ?? []).ToArray());

    /// <summary>Gets a defensive read-only dependent snapshot.</summary>
    public IReadOnlyList<CapabilityPostureDependentProjection> Dependents { get; } = Array.AsReadOnly((Dependents ?? []).ToArray());
}
