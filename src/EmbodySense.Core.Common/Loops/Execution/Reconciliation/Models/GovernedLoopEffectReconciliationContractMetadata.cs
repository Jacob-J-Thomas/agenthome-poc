using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Pins one versioned actuator reconciliation and probe contract to the exact admitted implementation.</summary>
/// <param name="SchemaVersion">The metadata schema, which must be 1.</param>
/// <param name="ContractId">The stable reconciliation contract identity.</param>
/// <param name="ContractVersion">The positive reconciliation contract version.</param>
/// <param name="Capability">The exact admitted capability descriptor identity.</param>
/// <param name="Implementation">The exact admitted implementation identity.</param>
/// <param name="ActuatorOperationId">The stable server-owned actuator operation identity.</param>
/// <param name="OperationDescriptorHash">The exact operation descriptor hash.</param>
/// <param name="ProbeContractId">The stable value-free probe contract identity.</param>
/// <param name="ProbeContractVersion">The positive probe contract version.</param>
/// <param name="ProbeContractHash">The exact canonical probe contract hash.</param>
/// <param name="ContentHash">The canonical hash of this metadata except this field.</param>
public sealed record GovernedLoopEffectReconciliationContractMetadata(
    int SchemaVersion,
    string ContractId,
    int ContractVersion,
    CapabilityDescriptorIdentity Capability,
    CapabilityImplementationIdentity Implementation,
    string ActuatorOperationId,
    string OperationDescriptorHash,
    string ProbeContractId,
    int ProbeContractVersion,
    string ProbeContractHash,
    string ContentHash)
{
    /// <summary>Gets a defensive capability identity copy.</summary>
    public CapabilityDescriptorIdentity Capability { get; } = Capability is null
        ? null!
        : new CapabilityDescriptorIdentity(Capability.Id, Capability.Version, Capability.Hash);

    /// <summary>Gets a defensive implementation identity copy.</summary>
    public CapabilityImplementationIdentity Implementation { get; } = Implementation is null
        ? null!
        : new CapabilityImplementationIdentity(Implementation.ProviderId, Implementation.ImplementationId);
}
