using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

internal sealed class NullRegistry : IGovernedActuatorOperationRegistry
{
    public IReadOnlyList<GovernedActuatorOperationDescriptor> Descriptors => null!;

    public bool TryResolve(GovernedActuatorOperationDescriptor descriptor, out IGovernedActuatorOperation? operation)
    {
        operation = null;
        return false;
    }
}
