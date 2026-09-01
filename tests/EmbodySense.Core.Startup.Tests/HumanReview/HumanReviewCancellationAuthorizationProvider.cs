using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class HumanReviewCancellationAuthorizationProvider : IHumanReviewDecisionAuthorizationProvider
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Started.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null;
    }
}
