using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.CancellationHost.Persistence;

/// <summary>Prevents the restart publication fixture from composing a release path outside this bounded reservation-recovery test.</summary>
internal sealed class HumanReviewDecisionActionReservationRecoveryUnavailableConsumer : IHumanReviewDecisionActionConsumer
{
    public Task<HumanReviewContinuationConsumptionResult> ConsumeDecisionActionAsync(HumanReviewContinuationCandidate candidate, HumanReviewDecisionReference decision, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewContinuationConsumptionResult(HumanReviewContinuationConsumptionStatus.Unavailable));
}
