using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Contains detached server-derived identity and preparation evidence sufficient to build the #570 certainty query without exposing raw effect payloads.</summary>
/// <param name="Identity">The exact current canonical effect-attempt identity.</param>
/// <param name="Preparation">The exact current value-free preparation fingerprint.</param>
public sealed record HumanReviewCurrentEffectAttemptEvidence(HumanReviewEffectAttemptIdentity Identity, HumanReviewEffectPreparationFingerprint Preparation);
