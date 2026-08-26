using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Supplies one detached canonical Human Review decision/claim candidate for Application-only continuation evaluation.</summary>
/// <remarks>The candidate is evidence, never a release grant. Its producer must re-read canonical state; this type deliberately carries no adapter or dispatch delegate.</remarks>
/// <param name="Run">The current canonical run snapshot.</param>
/// <param name="GraphArtifact">The exact immutable graph artifact when the decision could release work.</param>
/// <param name="Continuation">The current strict continuation state for an approved decision, when published.</param>
/// <param name="Claim">The exact current worker claim for an approved continuation, when claimed.</param>
public sealed record HumanReviewContinuationCandidate(
    CustomLoopRunRecord Run,
    GovernedLoopGraphRevisionArtifact? GraphArtifact,
    HumanReviewContinuationState? Continuation,
    HumanReviewContinuationClaim? Claim);
