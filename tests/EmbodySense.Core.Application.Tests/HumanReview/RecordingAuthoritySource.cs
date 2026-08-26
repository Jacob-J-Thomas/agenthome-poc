using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class RecordingAuthoritySource(params HumanReviewContinuationAuthorityReadStatus[] statuses) : IHumanReviewContinuationAuthoritySource
{
    private readonly Queue<HumanReviewContinuationAuthorityReadStatus> _statuses = new(statuses);

    public int ReadCount { get; private set; }

    public Action<int>? AfterRead { get; set; }

    public Task<HumanReviewContinuationAuthorityReadResult> ReadAsync(HumanReviewContinuationAuthorityQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        var result = new HumanReviewContinuationAuthorityReadResult(_statuses.Count == 0 ? HumanReviewContinuationAuthorityReadStatus.Unavailable : _statuses.Dequeue());
        AfterRead?.Invoke(ReadCount);
        return Task.FromResult(result);
    }
}
