namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Selects one bounded page from a reconciliation attention or contract catalog.</summary>
/// <param name="MaximumCount">The requested finite page size.</param>
/// <param name="Cursor">The opaque continuation, when supplied.</param>
public sealed record GovernedLoopEffectReconciliationPageRequest(int MaximumCount = 50, string? Cursor = null)
{

    /// <summary>Gets the requested page size.</summary>
    public int MaximumCount { get; } = GovernedLoopEffectReconciliationSurfaceGuard.PageSize(MaximumCount, nameof(MaximumCount));

    /// <summary>Gets the opaque continuation, if supplied.</summary>
    public string? Cursor { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Cursor(Cursor, nameof(Cursor));
}
