using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class HumanReviewThrowingAuthorizationProvider : IHumanReviewDecisionAuthorizationProvider
{
    public Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("authority source unavailable");
}
