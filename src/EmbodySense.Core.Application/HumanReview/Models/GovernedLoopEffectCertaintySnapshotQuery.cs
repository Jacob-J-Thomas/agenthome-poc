using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names the exact identity and value-free preparation that a read-only certainty source must re-read from canonical state.</summary>
/// <param name="Identity">The exact immutable reviewed effect-attempt identity.</param>
/// <param name="Preparation">The exact immutable reviewed preparation fingerprint.</param>
public sealed record GovernedLoopEffectCertaintySnapshotQuery(HumanReviewEffectAttemptIdentity Identity, HumanReviewEffectPreparationFingerprint Preparation);
