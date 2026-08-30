using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class CancellationHumanReviewDecisionActionPublicationStore : IHumanReviewDecisionActionPublicationStore
{
    private readonly IHumanReviewDecisionActionPublicationStore _inner;
    private readonly CancellationTokenSource _cancellation;
    private readonly bool _cancelBeforeCommit;

    public CancellationHumanReviewDecisionActionPublicationStore(IHumanReviewDecisionActionPublicationStore inner, CancellationTokenSource cancellation, bool cancelBeforeCommit = false)
    {
        _inner = inner;
        _cancellation = cancellation;
        _cancelBeforeCommit = cancelBeforeCommit;
    }

    public int CommitCount { get; private set; }

    public async Task<HumanReviewDecisionActionStoreMutationResult> PublishAsync(string runId, int expectedLifecycleVersion, HumanReviewDecisionActionState action, CancellationToken cancellationToken = default)
    {
        if (_cancelBeforeCommit)
        {
            _cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var result = await _inner.PublishAsync(runId, expectedLifecycleVersion, action, cancellationToken);
        if (result.Status != HumanReviewDecisionActionStoreMutationStatus.Committed)
        {
            return result;
        }

        CommitCount++;
        _cancellation.Cancel();
        throw new OperationCanceledException("The canonical action wake committed before cancellation was observed.", _cancellation.Token);
    }
}
