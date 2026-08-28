using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class ResponseLostHumanReviewContinuationPublicationStore : IHumanReviewContinuationPublicationStore
{
    private readonly IHumanReviewContinuationPublicationStore _inner;
    private bool _loseResponse = true;

    public ResponseLostHumanReviewContinuationPublicationStore(IHumanReviewContinuationPublicationStore inner) => _inner = inner;

    public int CommitCount { get; private set; }

    public async Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationState continuation, CancellationToken cancellationToken = default)
    {
        var result = await _inner.PublishAsync(runId, expectedLifecycleVersion, continuation, cancellationToken);
        if (result.Status == HumanReviewContinuationStoreMutationStatus.Committed)
        {
            CommitCount++;
            if (_loseResponse)
            {
                _loseResponse = false;
                throw new IOException("The canonical mutation committed but its response was lost.");
            }
        }

        return result;
    }
}
