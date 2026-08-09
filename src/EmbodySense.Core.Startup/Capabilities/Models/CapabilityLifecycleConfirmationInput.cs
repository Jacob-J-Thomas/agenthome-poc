namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Confirms one exact durable lifecycle preview without accepting trusted descriptors or artifact digests.</summary>
/// <param name="OperationId">The idempotent operation identity used for the preview.</param>
/// <param name="Operation">The selected lifecycle operation.</param>
/// <param name="CapabilityId">The selected capability identity.</param>
/// <param name="TargetVersion">The optional exact target version.</param>
/// <param name="BaselineCatalogRevision">The catalog revision observed in the preview.</param>
/// <param name="BaselineActivationRevision">The activation revision observed in the preview.</param>
/// <param name="LifecycleRevision">The lifecycle revision observed in the preview.</param>
/// <param name="DependentSetRevision">The dependent-set revision observed in the preview.</param>
/// <param name="DependentSetHash">The dependent-set hash observed in the preview.</param>
/// <param name="PreviewHash">The exact preview hash observed by the caller.</param>
/// <param name="Confirmed">Whether the user explicitly confirmed this exact preview.</param>
public sealed record CapabilityLifecycleConfirmationInput(
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
    bool Confirmed);
