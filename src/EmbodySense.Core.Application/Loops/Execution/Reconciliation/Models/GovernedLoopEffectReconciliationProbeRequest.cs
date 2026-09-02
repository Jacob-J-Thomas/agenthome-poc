namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests one independent, read-only probe attempt for an exact open reconciliation case.</summary>
/// <param name="OperationId">The independent operation identity for the probe, distinct from the original effect operation.</param>
/// <param name="Case">The exact immutable case version to probe.</param>
public sealed record GovernedLoopEffectReconciliationProbeRequest(string OperationId, GovernedLoopEffectReconciliationCaseReference Case)
{
    /// <summary>Gets the independent bounded probe operation identity.</summary>
    public string OperationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(OperationId, nameof(OperationId));

    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredReference(Case, nameof(Case));
}
