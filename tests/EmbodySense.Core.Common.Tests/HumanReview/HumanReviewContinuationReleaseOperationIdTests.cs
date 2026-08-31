using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewContinuationReleaseOperationIdTests
{
    [Fact]
    public void Release_identity_is_stable_across_claim_takeover_but_binds_the_exact_release_kind()
    {
        var request = new HumanReviewRequestReference("request-one", HumanReviewTestData.Hash('a'));
        var wake = new HumanReviewContinuationWakeReference("wake-one", HumanReviewTestData.Hash('b'));
        var reservation = new HumanReviewContinuationReservationReference("reservation-one", HumanReviewTestData.Hash('c'));

        var continuation = HumanReviewContinuationReleaseOperationId.Create(request, wake, reservation, 1, HumanReviewContinuationReleaseKind.Continuation);
        var effect = HumanReviewContinuationReleaseOperationId.Create(request, wake, reservation, 1, HumanReviewContinuationReleaseKind.PreDispatchEffect);

        Assert.NotNull(continuation);
        Assert.NotNull(effect);
        Assert.NotEqual(continuation, effect);
        Assert.True(HumanReviewContinuationReleaseOperationId.Matches(continuation, request, wake, reservation, 1, HumanReviewContinuationReleaseKind.Continuation));
        Assert.False(HumanReviewContinuationReleaseOperationId.Matches(continuation, request, wake, reservation, 1, HumanReviewContinuationReleaseKind.PreDispatchEffect));
        Assert.Null(HumanReviewContinuationReleaseOperationId.Create(request, wake, reservation, 0, HumanReviewContinuationReleaseKind.Continuation));
    }
}
