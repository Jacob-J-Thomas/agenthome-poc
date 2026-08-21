using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns one conclusive bounded external actuator outcome after the irreversible boundary without adapter-authored text.</summary>
/// <param name="Outcome">The conclusive success or failure.</param>
/// <param name="OutcomeEvidenceId">The bounded value-free outcome evidence reference.</param>
/// <param name="AfterEvidenceId">The optional bounded value-free after-state evidence reference.</param>
public sealed record GovernedActuatorExternalOutcome(
    GovernedLoopEffectOutcome Outcome,
    string OutcomeEvidenceId,
    string? AfterEvidenceId);
