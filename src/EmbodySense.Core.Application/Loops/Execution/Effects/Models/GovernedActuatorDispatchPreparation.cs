using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Captures one exact server-derived pre-dispatch snapshot after all side-effect-free checks.</summary>
internal sealed record GovernedActuatorDispatchPreparation(
    CapabilityDescriptor Capability,
    GovernedActuatorOperationDescriptor Descriptor,
    IGovernedActuatorOperation Operation,
    GovernedActuatorInputEvidence Input,
    GovernedActuatorPreparationEvidence Evidence,
    AuthorityCeiling RequiredAuthority,
    bool RequiresGovernedHumanReview);
