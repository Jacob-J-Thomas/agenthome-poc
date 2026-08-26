using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Fences one append-only continuation claim to an exact bounded discovery candidate.</summary>
/// <param name="Candidate">The exact candidate and whole-run version observed during bounded discovery.</param>
/// <param name="Claim">The immutable coordinator claim to append only if canonical state still matches.</param>
public sealed record HumanReviewContinuationClaimIntent(HumanReviewContinuationRecoveryCandidate Candidate, HumanReviewContinuationClaim Claim);
