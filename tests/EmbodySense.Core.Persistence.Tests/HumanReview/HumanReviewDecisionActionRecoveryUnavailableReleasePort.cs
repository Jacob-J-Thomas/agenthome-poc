using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

/// <summary>Supplies an unavailable release port when a persistence test must exercise recovery before action release composition exists.</summary>
internal sealed class HumanReviewDecisionActionRecoveryUnavailableReleasePort : IHumanReviewDecisionActionReleasePort
{
    public Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewDecisionActionReleaseResult(HumanReviewDecisionActionReleaseStatus.Unavailable));
}
