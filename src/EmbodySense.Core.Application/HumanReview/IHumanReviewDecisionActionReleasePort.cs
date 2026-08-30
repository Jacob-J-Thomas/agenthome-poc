using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Executes one already-claimed non-approval action without becoming an authority source.</summary>
public interface IHumanReviewDecisionActionReleasePort
{
    /// <summary>Applies the exact prepared action only after canonical reread confirms its current claim.</summary>
    Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default);
}
