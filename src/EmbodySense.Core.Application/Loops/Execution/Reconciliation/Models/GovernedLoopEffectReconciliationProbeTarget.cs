namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies the registered value-free subject that a read-only probe may inspect.</summary>
/// <param name="TargetFingerprint">The canonical SHA-256 fingerprint of the registered target.</param>
/// <param name="PreconditionEvidenceHash">The optional exact optimistic-precondition evidence hash.</param>
/// <param name="BeforeEvidenceId">The optional exact value-free before-state evidence reference.</param>
public sealed record GovernedLoopEffectReconciliationProbeTarget(
    string TargetFingerprint,
    string? PreconditionEvidenceHash,
    string? BeforeEvidenceId)
{
    /// <summary>Gets the canonical target fingerprint.</summary>
    public string TargetFingerprint { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(TargetFingerprint, nameof(TargetFingerprint));

    /// <summary>Gets the optional canonical optimistic-precondition hash.</summary>
    public string? PreconditionEvidenceHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireOptionalSha256(PreconditionEvidenceHash, nameof(PreconditionEvidenceHash));

    /// <summary>Gets the optional bounded before-state evidence reference.</summary>
    public string? BeforeEvidenceId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireOptionalEvidenceIdentifier(BeforeEvidenceId, nameof(BeforeEvidenceId));
}
