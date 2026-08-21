using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Persists dispatch-boundary evidence before allowing one structured external callback.</summary>
public interface IGovernedActuatorDispatchBoundary
{
    /// <summary>Crosses the irreversible boundary at most once after its durable marker is committed.</summary>
    Task<GovernedActuatorExternalOutcome> CrossAsync(
        Func<CancellationToken, Task<GovernedActuatorExternalOutcome>> callback,
        CancellationToken cancellationToken = default);
}
