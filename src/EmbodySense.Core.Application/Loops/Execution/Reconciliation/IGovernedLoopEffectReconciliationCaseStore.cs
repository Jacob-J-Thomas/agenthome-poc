using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

/// <summary>Provides bounded discovery, exact reads, and immutable compare-exchange for canonical effect reconciliation cases.</summary>
public interface IGovernedLoopEffectReconciliationCaseStore
{
    /// <summary>Reads one bounded deterministic page of canonical reconciliation case summaries.</summary>
    /// <param name="request">The finite page request and optional opaque continuation.</param>
    /// <param name="cancellationToken">A token that cancels the list read.</param>
    /// <returns>The detached page or a fail-closed read disposition.</returns>
    Task<GovernedLoopEffectReconciliationCaseListPage> ListAsync(GovernedLoopEffectReconciliationCaseListRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads one exact immutable reconciliation case without following a replacement identity.</summary>
    /// <param name="request">The exact immutable case reference.</param>
    /// <param name="cancellationToken">A token that cancels the exact read.</param>
    /// <returns>The detached exact case or a fail-closed read disposition.</returns>
    Task<GovernedLoopEffectReconciliationCaseReadResult> ReadAsync(GovernedLoopEffectReconciliationCaseReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Atomically stores one immutable open, assess, dispose, or accepted-resolution case stage and its optional validated effect-head successor when every optimistic identity remains exact.</summary>
    /// <param name="request">The complete optimistic immutable replacement request.</param>
    /// <param name="cancellationToken">A token that cancels work before durable atomic intent begins.</param>
    /// <returns>The detached exact current case and effect head with the atomic compare-exchange disposition.</returns>
    /// <remarks>This is the canonical atomic persistence boundary for each independently authorized case stage. Assessment and disposition remain separate successors. Only an accepted resolution may include an effect-head successor, and implementations must not split that pair across stores, leases, or commits.</remarks>
    Task<GovernedLoopEffectReconciliationCaseMutationResult> CompareExchangeAsync(GovernedLoopEffectReconciliationCaseMutationRequest request, CancellationToken cancellationToken = default);
}
