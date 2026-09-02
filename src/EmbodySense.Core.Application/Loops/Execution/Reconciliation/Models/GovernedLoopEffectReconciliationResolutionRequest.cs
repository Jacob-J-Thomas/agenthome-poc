namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests publication of one proof-backed immutable reconciliation resolution.</summary>
/// <param name="OperationId">The stable mutation identity.</param>
/// <param name="Case">The exact expected case reference.</param>
/// <param name="SafeDetail">Optional bounded operator-safe context that never supplies proof.</param>
public sealed record GovernedLoopEffectReconciliationResolutionRequest(
    string? OperationId,
    GovernedLoopEffectReconciliationCaseReference? Case,
    string? SafeDetail = null);
