namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Returns server-derived, value-free evidence prepared without crossing an external effect boundary.</summary>
/// <param name="TargetFingerprint">The canonical lowercase target fingerprint derived from input.</param>
/// <param name="PreconditionEvidenceHash">The optional exact optimistic-precondition evidence hash.</param>
/// <param name="BeforeEvidenceId">The optional bounded before-state evidence reference.</param>
public sealed record GovernedActuatorPreparationEvidence(
    string TargetFingerprint,
    string? PreconditionEvidenceHash,
    string? BeforeEvidenceId);
