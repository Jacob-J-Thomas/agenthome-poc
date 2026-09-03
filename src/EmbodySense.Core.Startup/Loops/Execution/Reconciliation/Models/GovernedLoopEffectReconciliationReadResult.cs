namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one exact redacted reconciliation case read.</summary>
/// <param name="Status">The closed read disposition.</param>
/// <param name="Detail">The exact redacted case only when found.</param>
public sealed record GovernedLoopEffectReconciliationReadResult(GovernedLoopEffectReconciliationReadStatus Status, GovernedLoopEffectReconciliationCaseDetail? Detail)
{

    /// <summary>Gets the closed read disposition.</summary>
    public GovernedLoopEffectReconciliationReadStatus Status { get; } = Enum.IsDefined(Status) ? Status : throw new ArgumentOutOfRangeException(nameof(Status));
    /// <summary>Gets the exact redacted case only when found.</summary>
    public GovernedLoopEffectReconciliationCaseDetail? Detail { get; } = GovernedLoopEffectReconciliationSurfaceGuard.ReadDetail(Status, Detail, nameof(Detail));
}
