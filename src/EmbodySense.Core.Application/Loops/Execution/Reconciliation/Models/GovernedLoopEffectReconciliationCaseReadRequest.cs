namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests one exact immutable reconciliation case without following a replacement identity.</summary>
/// <param name="Reference">The exact case identity and immutable version reference.</param>
public sealed record GovernedLoopEffectReconciliationCaseReadRequest(GovernedLoopEffectReconciliationCaseReference Reference)
{
    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Reference { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Reference, nameof(Reference));
}
