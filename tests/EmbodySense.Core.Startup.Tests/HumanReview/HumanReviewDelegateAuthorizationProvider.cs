using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

internal sealed class HumanReviewDelegateAuthorizationProvider(
    Func<HumanReviewDecisionAuthorizationRequest, HumanReviewDecisionAuthorizationResult?> handler) : IHumanReviewDecisionAuthorizationProvider
{
    public Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(handler(request));
}
