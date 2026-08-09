namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Projects one durable server-owned capability lifecycle preview without trusted artifact contents.</summary>
/// <param name="OperationId">The idempotent preview identity.</param>
/// <param name="Operation">The lifecycle operation.</param>
/// <param name="CapabilityId">The exact capability identity.</param>
/// <param name="TargetVersion">The exact proposed version when relevant.</param>
/// <param name="BaselineCatalogRevision">The exact catalog revision bound to the preview.</param>
/// <param name="BaselineActivationRevision">The exact activation revision bound to the preview.</param>
/// <param name="LifecycleRevision">The exact lifecycle revision bound to the preview.</param>
/// <param name="DependentSetRevision">The exact dependent-set revision bound to the preview.</param>
/// <param name="DependentSetHash">The canonical dependent-set identity.</param>
/// <param name="PreviewHash">The canonical preview identity.</param>
/// <param name="IsBlocked">Whether a required dependent blocks the proposed mutation.</param>
/// <param name="HasDegradation">Whether an optional dependent would degrade.</param>
/// <param name="Impacts">The bounded deterministic dependent impacts.</param>
/// <param name="Detail">The bounded operator-facing explanation.</param>
public sealed record CapabilityLifecyclePreviewSnapshot(
    string OperationId,
    string Operation,
    string CapabilityId,
    string? TargetVersion,
    long BaselineCatalogRevision,
    long BaselineActivationRevision,
    long LifecycleRevision,
    long DependentSetRevision,
    string DependentSetHash,
    string PreviewHash,
    bool IsBlocked,
    bool HasDegradation,
    IReadOnlyList<CapabilityLifecycleImpactSnapshot> Impacts,
    string Detail)
{
    /// <summary>Gets a defensive read-only dependent-impact snapshot.</summary>
    public IReadOnlyList<CapabilityLifecycleImpactSnapshot> Impacts { get; } = Array.AsReadOnly((Impacts ?? []).ToArray());
}
