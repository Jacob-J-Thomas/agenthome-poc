using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class ThrowingHumanReviewContinuationAuthoritySource(Exception exception) : IHumanReviewContinuationAuthoritySource
{
    public Task<HumanReviewContinuationAuthorityReadResult> ReadAsync(HumanReviewContinuationAuthorityQuery query, CancellationToken cancellationToken = default)
        => Task.FromException<HumanReviewContinuationAuthorityReadResult>(exception);
}
