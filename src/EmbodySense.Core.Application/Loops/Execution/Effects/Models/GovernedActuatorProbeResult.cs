namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns one bounded read-only recovery posture and optional conclusive outcome.</summary>
/// <param name="Posture">The exact recovery posture.</param>
/// <param name="Outcome">The conclusive outcome only when observed.</param>
public sealed record GovernedActuatorProbeResult(
    GovernedActuatorProbePosture Posture,
    GovernedActuatorExternalOutcome? Outcome);
