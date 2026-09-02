using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests one legal immutable disposition of the current reconciliation assessment.</summary>
/// <param name="OperationId">The stable mutation identity.</param>
/// <param name="Case">The exact expected case reference.</param>
/// <param name="Kind">The disposition requested for the current assessment.</param>
/// <param name="SafeDetail">Optional bounded operator-safe context that never supplies evidence.</param>
public sealed record GovernedLoopEffectReconciliationDispositionRequest(
    string? OperationId,
    GovernedLoopEffectReconciliationCaseReference? Case,
    GovernedLoopEffectReconciliationDispositionKind Kind,
    string? SafeDetail = null);
