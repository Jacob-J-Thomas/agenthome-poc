using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Reconstructs bounded immutable graph and run input for exact reconciliation bindings.</summary>
public interface IGovernedLoopEffectReconciliationInputSource
{
    /// <summary>Reads exact canonical input without changing graph, run, case, or external state.</summary>
    /// <param name="request">The exact immutable case reference and graph, run, effect, and actuator binding.</param>
    /// <param name="cancellationToken">A token that cancels input reconstruction.</param>
    /// <returns>The exact detached input, effect head, frontier, and echoed binding or a fail-closed disposition. A found result guarantees the effect head matches <c>Binding.CurrentAttemptHash</c>, remains reconciliation-required, and belongs to the matching ReviewBlocked frontier activation.</returns>
    Task<GovernedLoopEffectReconciliationInputReadResult> ReadAsync(GovernedLoopEffectReconciliationInputReadRequest request, CancellationToken cancellationToken = default);
}
