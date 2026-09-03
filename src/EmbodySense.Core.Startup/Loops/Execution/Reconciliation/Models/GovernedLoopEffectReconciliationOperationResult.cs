namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one closed reconciliation operation and optional redacted current case.</summary>
/// <param name="Status">The closed operation status.</param>
/// <param name="Detail">The safely observed current case, when available.</param>
public sealed record GovernedLoopEffectReconciliationOperationResult(GovernedLoopEffectReconciliationOperationStatus Status, GovernedLoopEffectReconciliationCaseDetail? Detail)
{

    /// <summary>Gets the closed operation status.</summary>
    public GovernedLoopEffectReconciliationOperationStatus Status { get; } = Status != GovernedLoopEffectReconciliationOperationStatus.Unknown && Enum.IsDefined(Status) ? Status : throw new ArgumentOutOfRangeException(nameof(Status));
    /// <summary>Gets the safely observed current case, when available.</summary>
    public GovernedLoopEffectReconciliationCaseDetail? Detail { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OperationDetail(Status, Detail, nameof(Detail));
}
