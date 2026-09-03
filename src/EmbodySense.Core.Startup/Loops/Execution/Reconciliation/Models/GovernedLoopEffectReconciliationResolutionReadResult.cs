namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Returns one immutable redacted reconciliation resolution.</summary>
/// <param name="Status">The closed resolution-read status.</param>
/// <param name="Resolution">The immutable resolution only when found.</param>
public sealed record GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus Status, GovernedLoopEffectReconciliationResolutionProjection? Resolution)
{

    /// <summary>Gets the closed resolution-read status.</summary>
    public GovernedLoopEffectReconciliationResolutionReadStatus Status { get; } = Enum.IsDefined(Status) ? Status : throw new ArgumentOutOfRangeException(nameof(Status));
    /// <summary>Gets the immutable resolution only when found.</summary>
    public GovernedLoopEffectReconciliationResolutionProjection? Resolution { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Resolution(Status, Resolution, nameof(Resolution));
}
