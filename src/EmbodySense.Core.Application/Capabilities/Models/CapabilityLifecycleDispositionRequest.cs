namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies one exact caller-observed durable lifecycle preview for terminal disposition.</summary>
public sealed record CapabilityLifecycleDispositionRequest(
    CapabilityLifecycleSelectionRequest Selection,
    long BaselineCatalogRevision,
    long BaselineActivationRevision,
    long LifecycleRevision,
    long DependentSetRevision,
    string DependentSetHash,
    string PreviewHash);
