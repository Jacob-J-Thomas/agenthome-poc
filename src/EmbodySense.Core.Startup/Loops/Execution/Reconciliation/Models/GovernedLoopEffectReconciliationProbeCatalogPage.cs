namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one bounded value-free registered reconciliation-probe page.</summary>
/// <param name="Status">The closed catalog status.</param>
/// <param name="Contracts">The detached registered contract projections.</param>
/// <param name="NextCursor">The opaque continuation, if another page exists.</param>
public sealed record GovernedLoopEffectReconciliationProbeCatalogPage(GovernedLoopEffectReconciliationProbeCatalogStatus Status, IReadOnlyList<GovernedLoopEffectReconciliationContractProjection> Contracts, string? NextCursor = null)
{

    /// <summary>Gets the closed catalog status.</summary>
    public GovernedLoopEffectReconciliationProbeCatalogStatus Status { get; } = Enum.IsDefined(Status) ? Status : throw new ArgumentOutOfRangeException(nameof(Status));
    /// <summary>Gets the detached registered contract projections.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationContractProjection> Contracts { get; } = GovernedLoopEffectReconciliationSurfaceGuard.CatalogItems(Status, Contracts, NextCursor, nameof(Contracts));
    /// <summary>Gets the opaque continuation, if another page exists.</summary>
    public string? NextCursor { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Cursor(NextCursor, nameof(NextCursor));
}
