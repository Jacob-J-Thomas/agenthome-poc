using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class CancellationHumanReviewContinuationPublicationStore : IHumanReviewContinuationPublicationStore
{
    private readonly IHumanReviewContinuationPublicationStore _inner;
    private readonly CancellationTokenSource _cancellation;
    private readonly bool _cancelBeforeCommit;

    public CancellationHumanReviewContinuationPublicationStore(IHumanReviewContinuationPublicationStore inner, CancellationTokenSource cancellation, bool cancelBeforeCommit = false)
    {
        _inner = inner;
        _cancellation = cancellation;
        _cancelBeforeCommit = cancelBeforeCommit;
    }

    public int CommitCount { get; private set; }

    public async Task<HumanReviewContinuationStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewContinuationState continuation, CancellationToken cancellationToken = default)
    {
        if (_cancelBeforeCommit)
        {
            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = await _inner.PublishAsync(runId, expectedLifecycleVersion, continuation, cancellationToken);
        if (result.Status == HumanReviewContinuationStoreMutationStatus.Committed)
        {
            CommitCount++;
            _cancellation.Cancel();
            throw new OperationCanceledException("The canonical mutation committed before cancellation was observed.", _cancellation.Token);
        }

        return result;
    }
}
