using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessAuthority : IHumanReviewContinuationAuthoritySource
{
    public Task<HumanReviewContinuationAuthorityReadResult> ReadAsync(HumanReviewContinuationAuthorityQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HumanReviewContinuationAuthorityReadResult(HumanReviewContinuationAuthorityReadStatus.Current));
    }
}
