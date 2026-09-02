using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one bounded deterministic reconciliation case page.</summary>
/// <param name="Status">The closed page-read disposition.</param>
/// <param name="Cases">The ordered immutable case summaries from one canonical snapshot.</param>
/// <param name="NextCursor">The opaque continuation, or <see langword="null"/> when no page remains.</param>
public sealed record GovernedLoopEffectReconciliationCaseListPage(
    GovernedLoopEffectReconciliationCaseListStatus Status,
    IReadOnlyList<GovernedLoopEffectReconciliationCaseSummary> Cases,
    string? NextCursor)
{
    /// <summary>Gets the validated closed page-read disposition.</summary>
    public GovernedLoopEffectReconciliationCaseListStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets detached immutable case summaries.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationCaseSummary> Cases { get; } = GovernedLoopEffectReconciliationModelGuard.CaptureResultPage(Status, GovernedLoopEffectReconciliationCaseListStatus.Ready, Cases, NextCursor, item => new GovernedLoopEffectReconciliationCaseSummary(item.CaseId, item.CaseVersion, item.ContentHash, item.BindingHash, item.Status), nameof(Cases));

    /// <summary>Gets the bounded opaque continuation.</summary>
    public string? NextCursor { get; } = GovernedLoopEffectReconciliationModelGuard.CaptureResultCursor(Status, GovernedLoopEffectReconciliationCaseListStatus.Ready, NextCursor, nameof(NextCursor));
}
