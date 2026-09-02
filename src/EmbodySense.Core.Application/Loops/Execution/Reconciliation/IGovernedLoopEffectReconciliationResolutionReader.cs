using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Reads immutable reconciliation resolutions for exact case and execution bindings.</summary>
public interface IGovernedLoopEffectReconciliationResolutionReader
{
    /// <summary>Reads one exact immutable resolution without changing a case or making it eligible for dispatch.</summary>
    /// <param name="request">The exact immutable case reference and reconciliation binding.</param>
    /// <param name="cancellationToken">A token that cancels the resolution read.</param>
    /// <returns>The detached immutable resolution or a fail-closed disposition.</returns>
    Task<GovernedLoopEffectReconciliationResolutionReadResult> ReadAsync(GovernedLoopEffectReconciliationResolutionReadRequest request, CancellationToken cancellationToken = default);
}
