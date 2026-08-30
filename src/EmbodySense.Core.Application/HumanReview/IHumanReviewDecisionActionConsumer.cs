using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Consumes one exact claimed non-approval decision into the existing fail-closed Application action intent.</summary>
public interface IHumanReviewDecisionActionConsumer
{
    /// <summary>Evaluates only the supplied exact accepted non-approval decision from a reread canonical candidate.</summary>
    Task<HumanReviewContinuationConsumptionResult> ConsumeDecisionActionAsync(HumanReviewContinuationCandidate candidate, HumanReviewDecisionReference decision, CancellationToken cancellationToken = default);
}
