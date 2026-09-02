namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests an immutable assessment of current authoritative observations for one exact case.</summary>
/// <param name="OperationId">The stable mutation identity.</param>
/// <param name="Case">The exact expected case reference.</param>
/// <param name="SafeDetail">Optional bounded operator-safe context that never supplies evidence.</param>
public sealed record GovernedLoopEffectReconciliationAssessmentRequest(
    string? OperationId,
    GovernedLoopEffectReconciliationCaseReference? Case,
    string? SafeDetail = null);
