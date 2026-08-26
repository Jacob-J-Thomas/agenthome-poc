using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewContinuationRecoveryTestReleasePort(HumanReviewContinuationReleaseResult result, Exception? exception = null) : IHumanReviewContinuationReleasePort
{
    public int Count { get; private set; }

    public Task<HumanReviewContinuationReleaseResult> ReleaseAsync(HumanReviewContinuationActionIntent action, HumanReviewContinuationCompletionIntent completion, CancellationToken cancellationToken = default)
    {
        Count++;
        return exception is null ? Task.FromResult(result) : Task.FromException<HumanReviewContinuationReleaseResult>(exception);
    }
}
