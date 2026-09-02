using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Selects one bounded deterministic page of reconciliation case summaries.</summary>
/// <param name="MaximumCount">The requested page size from 1 through 100.</param>
/// <param name="Cursor">An optional opaque continuation no longer than 1024 characters.</param>
public sealed record GovernedLoopEffectReconciliationCaseListRequest(int MaximumCount, string? Cursor = null)
{
    /// <summary>Gets the validated requested page size.</summary>
    public int MaximumCount { get; } = GovernedLoopEffectReconciliationPageLimits.RequirePageSize(MaximumCount, nameof(MaximumCount));

    /// <summary>Gets the validated optional opaque continuation.</summary>
    public string? Cursor { get; } = GovernedLoopEffectReconciliationPageLimits.CaptureCursor(Cursor, nameof(Cursor));
}
