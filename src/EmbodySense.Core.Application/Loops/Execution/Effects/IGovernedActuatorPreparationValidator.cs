using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Reauthenticates one side-effect-free preparation immediately before durable intent publication.</summary>
public interface IGovernedActuatorPreparationValidator
{
    /// <summary>
    /// Returns whether the exact server-derived preparation remains available and current. Implementations must
    /// remain side-effect-free because the attempt store invokes this check while excluding preparation cleanup.
    /// </summary>
    Task<bool> IsPreparationCurrentAsync(
        GovernedActuatorInputEvidence input,
        GovernedActuatorPreparationEvidence preparation,
        CancellationToken cancellationToken = default);
}
