namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one closed server-owned reconciliation case-admission posture.</summary>
/// <param name="Status">The closed admission status.</param>
public sealed record GovernedLoopEffectReconciliationAdmissionResult(GovernedLoopEffectReconciliationAdmissionStatus Status)
{
    /// <summary>Gets the validated closed admission status.</summary>
    public GovernedLoopEffectReconciliationAdmissionStatus Status { get; } = Status != GovernedLoopEffectReconciliationAdmissionStatus.Unknown && Enum.IsDefined(Status)
        ? Status
        : throw new ArgumentOutOfRangeException(nameof(Status));
}
