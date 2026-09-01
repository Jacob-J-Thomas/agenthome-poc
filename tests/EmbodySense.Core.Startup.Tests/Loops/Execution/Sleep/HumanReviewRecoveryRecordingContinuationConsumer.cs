using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingContinuationConsumer : IHumanReviewContinuationConsumer
{
    public Task<HumanReviewContinuationConsumptionResult> ConsumeAsync(HumanReviewContinuationCandidate candidate, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewContinuationConsumptionResult(HumanReviewContinuationConsumptionStatus.Unavailable));
}
