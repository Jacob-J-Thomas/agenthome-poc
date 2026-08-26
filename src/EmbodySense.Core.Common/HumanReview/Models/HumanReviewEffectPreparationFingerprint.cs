namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Retains only canonical hashes and bounded evidence references required to prove a reviewed effect preparation has not drifted.</summary>
/// <param name="SchemaVersion">The preparation schema version, which must be 1.</param>
/// <param name="IntentHash">The exact immutable effect intent hash.</param>
/// <param name="OperationDescriptorHash">The exact server-derived actuator operation descriptor hash.</param>
/// <param name="InputFingerprint">The exact canonical input fingerprint; no raw input is retained.</param>
/// <param name="TargetFingerprint">The exact canonical server-resolved target fingerprint.</param>
/// <param name="PreconditionEvidenceHash">The optional exact canonical optimistic-precondition evidence hash.</param>
/// <param name="ReviewTargetHash">The exact Human Review target hash.</param>
/// <param name="ReviewPreconditionHash">The exact Human Review precondition hash.</param>
/// <param name="ReviewPayloadHash">The exact Human Review payload hash; no payload is retained.</param>
/// <param name="BeforeEvidenceId">The optional bounded value-free before-state evidence reference.</param>
/// <param name="AdmissionAuthorityEvidenceHash">The exact admission authority evidence hash.</param>
/// <param name="PreparationHash">The canonical hash of every prior preparation field.</param>
public sealed record HumanReviewEffectPreparationFingerprint(
    int SchemaVersion,
    string IntentHash,
    string OperationDescriptorHash,
    string InputFingerprint,
    string TargetFingerprint,
    string? PreconditionEvidenceHash,
    string ReviewTargetHash,
    string ReviewPreconditionHash,
    string ReviewPayloadHash,
    string? BeforeEvidenceId,
    string AdmissionAuthorityEvidenceHash,
    string PreparationHash);
