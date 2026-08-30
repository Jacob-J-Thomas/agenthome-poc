using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.CancellationHost.Persistence;

/// <summary>Prevents the restart publication fixture from composing an external action release path.</summary>
internal sealed class HumanReviewDecisionActionReservationRecoveryUnavailableReleasePort : IHumanReviewDecisionActionReleasePort
{
    public Task<HumanReviewDecisionActionReleaseResult> ReleaseAsync(HumanReviewDecisionActionIntent intent, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewDecisionActionReleaseResult(HumanReviewDecisionActionReleaseStatus.Unavailable));
}
