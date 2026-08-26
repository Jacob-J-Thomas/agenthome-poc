using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryTestConsumer(HumanReviewContinuationConsumptionResult result, Exception? exception = null) : IHumanReviewContinuationConsumer
{
    public int Count { get; private set; }

    public Task<HumanReviewContinuationConsumptionResult> ConsumeAsync(HumanReviewContinuationCandidate candidate, CancellationToken cancellationToken = default)
    {
        Count++;
        return exception is null ? Task.FromResult(result) : Task.FromException<HumanReviewContinuationConsumptionResult>(exception);
    }
}
