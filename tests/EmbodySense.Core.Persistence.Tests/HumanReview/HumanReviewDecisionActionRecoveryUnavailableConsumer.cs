using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

/// <summary>Supplies an unavailable consumer when a persistence test must exercise recovery before action release composition exists.</summary>
internal sealed class HumanReviewDecisionActionRecoveryUnavailableConsumer : IHumanReviewDecisionActionConsumer
{
    public Task<HumanReviewContinuationConsumptionResult> ConsumeDecisionActionAsync(HumanReviewContinuationCandidate candidate, HumanReviewDecisionReference decision, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewContinuationConsumptionResult(HumanReviewContinuationConsumptionStatus.Unavailable));
}
