using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Exposes only immutable server-registered actuator operations.</summary>
public interface IGovernedActuatorOperationRegistry
{
    /// <summary>Gets the bounded deterministic operation metadata snapshot.</summary>
    IReadOnlyList<GovernedActuatorOperationDescriptor> Descriptors { get; }

    /// <summary>Resolves one exact operation descriptor to its server adapter.</summary>
    bool TryResolve(GovernedActuatorOperationDescriptor descriptor, out IGovernedActuatorOperation? operation);
}
