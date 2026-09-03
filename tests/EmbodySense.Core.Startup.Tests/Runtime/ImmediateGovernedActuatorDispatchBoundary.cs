using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class ImmediateGovernedActuatorDispatchBoundary : IGovernedActuatorDispatchBoundary
{
    internal static ImmediateGovernedActuatorDispatchBoundary Instance { get; } = new();

    private ImmediateGovernedActuatorDispatchBoundary()
    {
    }

    public Task<GovernedActuatorExternalOutcome> CrossAsync(
        Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return callback(cancellationToken);
    }
}
