namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one immutable reconciliation assessment without authority evidence or caller text.</summary>
/// <param name="AssessmentId">The stable assessment identity.</param>
/// <param name="Kind">The typed assessment kind.</param>
/// <param name="ObservationHashes">The ordered observation content hashes considered.</param>
/// <param name="AssessedAtUtc">The trusted assessment time.</param>
/// <param name="ContentHash">The exact assessment content hash.</param>
public sealed record GovernedLoopEffectReconciliationAssessmentProjection(
    string AssessmentId,
    GovernedLoopEffectReconciliationAssessmentKind Kind,
    IReadOnlyList<string> ObservationHashes,
    DateTimeOffset AssessedAtUtc,
    string ContentHash)
{

    /// <summary>Gets the stable assessment identity.</summary>
    public string AssessmentId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(AssessmentId, nameof(AssessmentId));
    /// <summary>Gets the typed assessment kind.</summary>
    public GovernedLoopEffectReconciliationAssessmentKind Kind { get; } = Kind != GovernedLoopEffectReconciliationAssessmentKind.Unknown && Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind));
    /// <summary>Gets the ordered observation content hashes considered.</summary>
    public IReadOnlyList<string> ObservationHashes { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Items(
        (ObservationHashes ?? throw new ArgumentNullException(nameof(ObservationHashes))).Select(value => GovernedLoopEffectReconciliationSurfaceGuard.Hash(value, nameof(ObservationHashes))),
        32,
        nameof(ObservationHashes));
    /// <summary>Gets the trusted assessment time.</summary>
    public DateTimeOffset AssessedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(AssessedAtUtc, nameof(AssessedAtUtc));
    /// <summary>Gets the exact assessment content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContentHash, nameof(ContentHash));
}
