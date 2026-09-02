using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Exposes bounded registered reconciliation probe metadata and exact immutable pin resolution.</summary>
public interface IGovernedLoopEffectReconciliationProbeRegistry
{
    /// <summary>Reads one bounded deterministic page of registered actuator and probe contracts.</summary>
    /// <param name="request">The finite registry page request and optional opaque continuation.</param>
    /// <param name="cancellationToken">A token that cancels the registry list read.</param>
    /// <returns>The detached registered metadata page or a fail-closed disposition.</returns>
    Task<GovernedLoopEffectReconciliationProbeRegistryPage> ListAsync(GovernedLoopEffectReconciliationProbeRegistryListRequest request, CancellationToken cancellationToken = default);

    /// <summary>Resolves one exact immutable registered actuator and probe pin.</summary>
    /// <param name="request">The exact pinned actuator and probe contract.</param>
    /// <param name="cancellationToken">A token that cancels the registry read.</param>
    /// <returns>The exact read-only probe only when the complete immutable pin matches.</returns>
    Task<GovernedLoopEffectReconciliationProbeRegistryReadResult> ReadAsync(GovernedLoopEffectReconciliationProbeRegistryReadRequest request, CancellationToken cancellationToken = default);
}
