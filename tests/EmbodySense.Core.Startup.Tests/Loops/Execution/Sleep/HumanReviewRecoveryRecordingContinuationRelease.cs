using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanReviewRecoveryRecordingContinuationRelease : IHumanReviewContinuationReleasePort
{
    public Task<HumanReviewContinuationReleaseResult> ReleaseAsync(HumanReviewContinuationActionIntent action, HumanReviewContinuationCompletionIntent completion, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Unavailable));
}
