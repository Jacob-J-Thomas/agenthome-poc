using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewContinuationPublicationTestStore : IHumanReviewContinuationPublicationStore
{
    public List<(string RunId, int ExpectedLifecycleVersion, HumanReviewContinuationState Continuation)> Publications { get; } = [];

    public HumanReviewContinuationStoreMutationResult? Result { get; set; } = new(HumanReviewContinuationStoreMutationStatus.Committed);

    public Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationState continuation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publications.Add((runId, expectedLifecycleVersion, continuation));
        return Task.FromResult(Result!);
    }
}
