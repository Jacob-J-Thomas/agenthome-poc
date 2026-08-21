using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns one exact capability-owned actuator operation and its server adapter.</summary>
/// <param name="Status">The structured current posture.</param>
/// <param name="Capability">The exact current capability descriptor when available.</param>
/// <param name="Descriptor">The exact operation metadata when registered.</param>
/// <param name="Operation">The server-owned adapter only for an active result.</param>
/// <param name="Detail">The bounded non-sensitive explanation.</param>
public sealed record GovernedActuatorCatalogResolutionResult(
    GovernedActuatorCatalogResolutionStatus Status,
    CapabilityDescriptor? Capability,
    GovernedActuatorOperationDescriptor? Descriptor,
    IGovernedActuatorOperation? Operation,
    string Detail);
