namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one bounded redacted reconciliation attention page.</summary>
/// <param name="Status">The closed page disposition.</param>
/// <param name="Items">The detached ordered attention items.</param>
/// <param name="NextCursor">The opaque continuation, if another page exists.</param>
public sealed record GovernedLoopEffectReconciliationPage(GovernedLoopEffectReconciliationPageStatus Status, IReadOnlyList<GovernedLoopEffectReconciliationCaseSummary> Items, string? NextCursor = null)
{

    /// <summary>Gets the closed page disposition.</summary>
    public GovernedLoopEffectReconciliationPageStatus Status { get; } = Enum.IsDefined(Status) ? Status : throw new ArgumentOutOfRangeException(nameof(Status));

    /// <summary>Gets the detached ordered attention items.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationCaseSummary> Items { get; } = GovernedLoopEffectReconciliationSurfaceGuard.PageItems(Status, Items, NextCursor, nameof(Items));

    /// <summary>Gets the opaque continuation, if another page exists.</summary>
    public string? NextCursor { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Cursor(NextCursor, nameof(NextCursor));
}
