namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one immutable disposition without authority evidence or caller text.</summary>
/// <param name="DispositionId">The stable disposition identity.</param>
/// <param name="Kind">The legal disposition kind.</param>
/// <param name="AssessmentHash">The exact accepted assessment hash.</param>
/// <param name="DisposedAtUtc">The trusted disposition time.</param>
/// <param name="ContentHash">The exact disposition content hash.</param>
public sealed record GovernedLoopEffectReconciliationDispositionProjection(string DispositionId, GovernedLoopEffectReconciliationDispositionKind Kind, string AssessmentHash, DateTimeOffset DisposedAtUtc, string ContentHash)
{

    /// <summary>Gets the stable disposition identity.</summary>
    public string DispositionId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(DispositionId, nameof(DispositionId));
    /// <summary>Gets the legal disposition kind.</summary>
    public GovernedLoopEffectReconciliationDispositionKind Kind { get; } = Kind != GovernedLoopEffectReconciliationDispositionKind.Unknown && Enum.IsDefined(Kind) ? Kind : throw new ArgumentOutOfRangeException(nameof(Kind));
    /// <summary>Gets the exact accepted assessment hash.</summary>
    public string AssessmentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(AssessmentHash, nameof(AssessmentHash));
    /// <summary>Gets the trusted disposition time.</summary>
    public DateTimeOffset DisposedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(DisposedAtUtc, nameof(DisposedAtUtc));
    /// <summary>Gets the exact disposition content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContentHash, nameof(ContentHash));
}
