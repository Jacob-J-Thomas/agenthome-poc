using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Captures deterministic impact evidence that must match exactly at mutation time.</summary>
/// <param name="Status">The preview outcome.</param>
/// <param name="WorkspaceIdentity">The physical workspace identity binding.</param>
/// <param name="OperationId">The preview and mutation operation identity.</param>
/// <param name="Kind">The proposed transition.</param>
/// <param name="CapabilityId">The target capability.</param>
/// <param name="LifecycleRevision">The expected authenticated lifecycle revision.</param>
/// <param name="DependentSetRevision">The expected dependent-set revision.</param>
/// <param name="DependentSetHash">The canonical dependent-set hash.</param>
/// <param name="PreviewHash">The canonical preview identity.</param>
/// <param name="Impacts">The sorted exact impact evidence.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
/// <param name="BaselineCatalogRevision">The exact current catalog revision bound to the preview.</param>
/// <param name="BaselineActivationRevision">The exact current legacy activation revision bound to the preview.</param>
/// <param name="TargetDescriptor">The exact immutable upgrade or rollback target.</param>
/// <param name="TargetArtifactDigest">The exact immutable upgrade or rollback artifact.</param>
public sealed record CapabilityLifecyclePreview(CapabilityLifecyclePreviewStatus Status, string WorkspaceIdentity, string OperationId, CapabilityLifecycleOperationKind Kind, CapabilityId CapabilityId, long LifecycleRevision, long DependentSetRevision, string DependentSetHash, string PreviewHash, IReadOnlyList<CapabilityLifecycleImpact> Impacts, string Detail, long BaselineCatalogRevision = 0, long BaselineActivationRevision = 0, CapabilityDescriptor? TargetDescriptor = null, CapabilityIntegrityDigest? TargetArtifactDigest = null)
{
    /// <summary>Gets a defensive read-only impact snapshot.</summary>
    public IReadOnlyList<CapabilityLifecycleImpact> Impacts { get; } = Array.AsReadOnly((Impacts ?? []).ToArray());
}
