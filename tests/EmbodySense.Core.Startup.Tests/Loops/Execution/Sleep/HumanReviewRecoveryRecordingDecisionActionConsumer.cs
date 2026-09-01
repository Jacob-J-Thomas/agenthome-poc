using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingDecisionActionConsumer : IHumanReviewDecisionActionConsumer
{
    public Task<HumanReviewContinuationConsumptionResult> ConsumeDecisionActionAsync(HumanReviewContinuationCandidate candidate, HumanReviewDecisionReference decision, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewContinuationConsumptionResult(HumanReviewContinuationConsumptionStatus.Unavailable));
}
