namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns one bounded reread result and, only when current, its detached continuation candidate.</summary>
/// <param name="Status">The closed canonical-read posture.</param>
/// <param name="Candidate">The detached candidate only when <paramref name="Status"/> is <see cref="HumanReviewContinuationCandidateReadStatus.Current"/>.</param>
public sealed record HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus Status, HumanReviewContinuationCandidate? Candidate = null);
