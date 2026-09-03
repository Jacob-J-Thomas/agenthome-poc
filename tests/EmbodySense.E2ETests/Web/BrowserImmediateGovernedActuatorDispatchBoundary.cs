using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserImmediateGovernedActuatorDispatchBoundary : IGovernedActuatorDispatchBoundary
{
    internal static BrowserImmediateGovernedActuatorDispatchBoundary Instance { get; } = new();

    private BrowserImmediateGovernedActuatorDispatchBoundary()
    {
    }

    public Task<GovernedActuatorExternalOutcome> CrossAsync(
        Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        return callback(cancellationToken);
    }
}
