namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one registered reconciliation evidence source without authority material.</summary>
/// <param name="SourceId">The opaque source identity.</param>
/// <param name="Kind">The typed source kind.</param>
/// <param name="ReliabilityPosture">The registered reliability posture.</param>
/// <param name="ContractHash">The exact reconciliation contract hash.</param>
/// <param name="RegisteredAtUtc">The trusted registration time.</param>
/// <param name="RetiredAtUtc">The optional trusted retirement time.</param>
/// <param name="ContentHash">The exact source registration content hash.</param>
public sealed record GovernedLoopEffectReconciliationEvidenceSourceProjection(
    string SourceId,
    GovernedLoopEffectReconciliationEvidenceSourceKind Kind,
    GovernedLoopEffectReconciliationReliabilityPosture ReliabilityPosture,
    string ContractHash,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset? RetiredAtUtc,
    string ContentHash)
{

    /// <summary>Gets the opaque source identity.</summary>
    public string SourceId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(SourceId, nameof(SourceId));
    /// <summary>Gets the source kind.</summary>
    public GovernedLoopEffectReconciliationEvidenceSourceKind Kind { get; } = Kind != GovernedLoopEffectReconciliationEvidenceSourceKind.Unknown && Enum.IsDefined(Kind)
        ? Kind
        : throw new ArgumentOutOfRangeException(nameof(Kind));
    /// <summary>Gets the registered reliability posture.</summary>
    public GovernedLoopEffectReconciliationReliabilityPosture ReliabilityPosture { get; } = ReliabilityPosture != GovernedLoopEffectReconciliationReliabilityPosture.Unknown && Enum.IsDefined(ReliabilityPosture)
        ? ReliabilityPosture
        : throw new ArgumentOutOfRangeException(nameof(ReliabilityPosture));
    /// <summary>Gets the exact reconciliation contract hash.</summary>
    public string ContractHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContractHash, nameof(ContractHash));
    /// <summary>Gets the trusted registration time.</summary>
    public DateTimeOffset RegisteredAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(RegisteredAtUtc, nameof(RegisteredAtUtc));
    /// <summary>Gets the optional trusted retirement time.</summary>
    public DateTimeOffset? RetiredAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalUtc(RetiredAtUtc, nameof(RetiredAtUtc));
    /// <summary>Gets the exact source registration content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContentHash, nameof(ContentHash));
}
