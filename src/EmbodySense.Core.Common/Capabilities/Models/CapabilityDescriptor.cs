namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Declares safe schema-version-1 capability metadata without installation, enablement, assignment, authority, health, or trust state.
/// </summary>
/// <param name="SchemaVersion">The descriptor schema version.</param>
/// <param name="Id">The stable capability identifier.</param>
/// <param name="Kind">The capability category.</param>
/// <param name="Version">The exact implementation contract version.</param>
/// <param name="Implementation">The provider-owned implementation identity.</param>
/// <param name="Provenance">The safe implementation provenance evidence.</param>
/// <param name="Compatibility">The host and platform compatibility declaration.</param>
/// <param name="Purpose">The stable human-readable purpose.</param>
/// <param name="InputSchema">The machine-readable input schema.</param>
/// <param name="OutputSchema">The machine-readable output schema.</param>
/// <param name="ResourceLimits">The declared resource limits.</param>
/// <param name="SideEffectClass">The maximum side-effect class.</param>
/// <param name="Requirements">The declared data, egress, and secret-reference needs.</param>
public sealed record CapabilityDescriptor(
    int SchemaVersion,
    CapabilityId Id,
    CapabilityKind Kind,
    CapabilityVersion Version,
    CapabilityImplementationIdentity Implementation,
    CapabilityProvenance Provenance,
    CapabilityCompatibility Compatibility,
    string Purpose,
    CapabilityJsonSchema InputSchema,
    CapabilityJsonSchema OutputSchema,
    CapabilityResourceLimits ResourceLimits,
    CapabilitySideEffectClass SideEffectClass,
    CapabilityAccessRequirements Requirements)
{
    /// <summary>Gets the only supported experimental descriptor schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
