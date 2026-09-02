namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

/// <summary>Configures optional observation of durable reconciliation publication boundaries.</summary>
public sealed record GovernedLoopEffectReconciliationCaseStoreOptions
{
    /// <summary>
    /// Gets or initializes a callback invoked after each named durable publication boundary. An exception or
    /// process termination from the callback represents abrupt process loss; it is never treated as proof that a
    /// mutation did not commit, and callers must use <see cref="GovernedLoopEffectReconciliationCaseStore.RecoverAsync"/>
    /// before retrying.
    /// </summary>
    public Action<GovernedLoopEffectReconciliationPersistenceBoundary>? DurableBoundaryObserver { get; init; }
}
