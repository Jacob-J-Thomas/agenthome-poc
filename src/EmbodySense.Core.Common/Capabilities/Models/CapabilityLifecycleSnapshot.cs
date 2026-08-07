namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Captures server-owned capability lifecycle axes independently from source-supplied descriptor metadata.
/// </summary>
/// <param name="SchemaVersion">The snapshot schema version.</param>
/// <param name="DescriptorIdentity">The exact descriptor identity this state describes.</param>
/// <param name="Declaration">The declaration state.</param>
/// <param name="Installation">The installation state.</param>
/// <param name="Enablement">The enablement state.</param>
/// <param name="Health">The observed health state.</param>
/// <param name="Retirement">The deprecation or removal state.</param>
/// <param name="Trust">The server-owned trust state.</param>
/// <remarks>This snapshot intentionally contains no loop assignment, grant, authorization, or secret value.</remarks>
public sealed record CapabilityLifecycleSnapshot(
    int SchemaVersion,
    CapabilityDescriptorIdentity DescriptorIdentity,
    CapabilityDeclarationState Declaration,
    CapabilityInstallationState Installation,
    CapabilityEnablementState Enablement,
    CapabilityHealthState Health,
    CapabilityRetirementState Retirement,
    CapabilityTrustState Trust)
{
    /// <summary>Gets the only supported experimental lifecycle schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
