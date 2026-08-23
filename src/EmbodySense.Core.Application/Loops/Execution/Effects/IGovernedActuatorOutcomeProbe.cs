using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Provides authenticated read-only recovery for an exact actuator attempt after its durable dispatch boundary.</summary>
public interface IGovernedActuatorOutcomeProbe
{
    /// <summary>Reads server-owned evidence without dispatching, retrying, repairing, compensating, or accepting operator disposition.</summary>
    Task<GovernedActuatorProbeResult> ProbeAsync(
        GovernedActuatorInvocation invocation,
        CancellationToken cancellationToken = default);
}
