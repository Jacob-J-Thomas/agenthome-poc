using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed class HumanReviewBrowserFixtureDecisionAuthorizer : IHumanReviewDecisionAuthorizer
{
    public Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HumanReviewDecisionAuthorization(true, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "server-reviewer", "governed-reviewer", ImmutableArray.Create("review-scope-one"), "server-authorization"));
    }
}
