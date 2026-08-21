namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns a closed structured adapter result without exposing adapter-authored text.</summary>
/// <param name="Status">Whether dispatch did not start or a conclusive outcome was observed.</param>
/// <param name="Outcome">The conclusive external outcome only when observed.</param>
public sealed record GovernedActuatorAdapterResult(
    GovernedActuatorAdapterStatus Status,
    GovernedActuatorExternalOutcome? Outcome);
