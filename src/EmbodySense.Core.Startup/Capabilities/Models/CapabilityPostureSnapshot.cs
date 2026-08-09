namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Contains one bounded redacted human or administrative capability posture.</summary>
/// <param name="Id">The canonical capability identifier.</param>
/// <param name="Version">The exact capability version.</param>
/// <param name="DescriptorHash">The exact canonical descriptor hash.</param>
/// <param name="Kind">The closed capability kind.</param>
/// <param name="Purpose">The bounded public purpose.</param>
/// <param name="ProviderId">The public provider identity.</param>
/// <param name="ImplementationId">The public implementation identity, never private configuration.</param>
/// <param name="ProvenanceKind">The safe provenance category.</param>
/// <param name="SourceUri">The safe source URI with any local file path redacted.</param>
/// <param name="SourceRevision">The optional safe source revision.</param>
/// <param name="Integrity">The optional public provenance digest.</param>
/// <param name="HostVersionRange">The declared compatible host range.</param>
/// <param name="SupportedPlatforms">The canonical supported platform tuples.</param>
/// <param name="IsCurrentHostCompatible">Whether the current host satisfies the declaration.</param>
/// <param name="SideEffectClass">The declared non-granting maximum side-effect class.</param>
/// <param name="DataClasses">The declared data classifications.</param>
/// <param name="EgressMode">The declared egress posture.</param>
/// <param name="EgressDestinations">The declared restricted destinations.</param>
/// <param name="SecretRequirements">Secret reference names only, never secret values.</param>
/// <param name="State">The summarized current posture.</param>
/// <param name="Declaration">The declaration state.</param>
/// <param name="Installation">The installation state.</param>
/// <param name="Enablement">The enablement state.</param>
/// <param name="Health">The health state.</param>
/// <param name="Retirement">The retirement state.</param>
/// <param name="Trust">The trust state.</param>
/// <param name="IsLifecycleEnabled">Whether authenticated lifecycle evidence currently permits admission.</param>
/// <param name="IsRemoved">Whether the identity is tombstoned.</param>
/// <param name="EntryRevision">The exact catalog entry revision.</param>
/// <param name="LifecycleRevision">The optional exact lifecycle revision.</param>
/// <param name="IsRecovered">Whether any evidence came from explicit last-proved state.</param>
/// <param name="Dependents">The deterministic bounded dependent projections.</param>
/// <param name="AreDependentsAvailable">Whether the complete dependent set was proved.</param>
/// <param name="DependentsTruncated">Whether additional safe dependents exceeded the response bound.</param>
public sealed record CapabilityPostureSnapshot(
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
    string State,
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
    IReadOnlyList<CapabilityPostureDependentSnapshot> Dependents,
    bool AreDependentsAvailable,
    bool DependentsTruncated)
{
    /// <summary>Gets a defensive read-only platform snapshot.</summary>
    public IReadOnlyList<string> SupportedPlatforms { get; } = Array.AsReadOnly((SupportedPlatforms ?? []).ToArray());

    /// <summary>Gets a defensive read-only data-class snapshot.</summary>
    public IReadOnlyList<string> DataClasses { get; } = Array.AsReadOnly((DataClasses ?? []).ToArray());

    /// <summary>Gets a defensive read-only destination snapshot.</summary>
    public IReadOnlyList<string> EgressDestinations { get; } = Array.AsReadOnly((EgressDestinations ?? []).ToArray());

    /// <summary>Gets a defensive read-only secret-reference snapshot.</summary>
    public IReadOnlyList<string> SecretRequirements { get; } = Array.AsReadOnly((SecretRequirements ?? []).ToArray());

    /// <summary>Gets a defensive read-only dependent snapshot.</summary>
    public IReadOnlyList<CapabilityPostureDependentSnapshot> Dependents { get; } = Array.AsReadOnly((Dependents ?? []).ToArray());
}
