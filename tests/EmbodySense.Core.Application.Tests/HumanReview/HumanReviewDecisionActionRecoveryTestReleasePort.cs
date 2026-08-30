using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionActionRecoveryTestReleasePort(HumanReviewDecisionActionReleaseResult result) : IHumanReviewDecisionActionReleasePort
{
    public int Count { get; private set; }

    public Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default)
    {
        Count++;
        return Task.FromResult(result);
    }
}
