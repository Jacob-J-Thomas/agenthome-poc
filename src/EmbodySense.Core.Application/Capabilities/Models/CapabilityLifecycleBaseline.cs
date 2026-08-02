namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Captures proved current catalog and activation state used only to register an unknown lifecycle identity.</summary>
/// <param name="State">The proved lifecycle baseline.</param>
/// <param name="CatalogRevision">The exact catalog revision observed.</param>
/// <param name="ActivationRevision">The exact activation revision observed.</param>
public sealed record CapabilityLifecycleBaseline(CapabilityLifecycleState State, long CatalogRevision, long ActivationRevision);
