using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionActionRecoveryTestConsumer(HumanReviewContinuationConsumptionResult result) : IHumanReviewDecisionActionConsumer
{
    public int Count { get; private set; }
    public HumanReviewContinuationCandidate? LastCandidate { get; private set; }
    public HumanReviewDecisionReference? LastDecision { get; private set; }

    public Task<HumanReviewContinuationConsumptionResult> ConsumeDecisionActionAsync(HumanReviewContinuationCandidate candidate, HumanReviewDecisionReference decision, CancellationToken cancellationToken = default)
    {
        Count++;
        LastCandidate = candidate;
        LastDecision = decision;
        return Task.FromResult(result);
    }
}
