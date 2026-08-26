using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Captures one bounded, value-free current certainty reading for an exact reviewed effect attempt.</summary>
/// <param name="SchemaVersion">The snapshot schema version, which must be 1.</param>
/// <param name="Identity">The exact canonical attempt identity.</param>
/// <param name="Preparation">The exact value-free preparation fingerprint.</param>
/// <param name="DispatchAuthorityEvidenceHash">The fresh dispatch-authority evidence hash, when one has been attached.</param>
/// <param name="Phase">The canonical durable effect phase.</param>
/// <param name="Certainty">The closed current certainty posture derived from the canonical phase and evidence axes.</param>
/// <param name="ObservedAtUtc">The trusted UTC instant when canonical state was read.</param>
/// <param name="SnapshotHash">The canonical hash of every prior snapshot field.</param>
public sealed record HumanReviewEffectCertaintySnapshot(
    int SchemaVersion,
    HumanReviewEffectAttemptIdentity Identity,
    HumanReviewEffectPreparationFingerprint Preparation,
    string? DispatchAuthorityEvidenceHash,
    GovernedLoopEffectPhase Phase,
    HumanReviewEffectCertainty Certainty,
    DateTimeOffset ObservedAtUtc,
    string SnapshotHash);
