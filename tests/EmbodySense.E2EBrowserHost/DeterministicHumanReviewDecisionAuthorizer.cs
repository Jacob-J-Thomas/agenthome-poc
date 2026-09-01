using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.E2EBrowserHost;

internal sealed class DeterministicHumanReviewDecisionAuthorizer : IHumanReviewDecisionAuthorizer
{
    public Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var reviewer = request.Request.EligibleReviewers.SingleOrDefault();
        if (reviewer is null)
        {
            return Task.FromResult(new HumanReviewDecisionAuthorization(false, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, null, null, ImmutableArray<string>.Empty, null));
        }

        return Task.FromResult(new HumanReviewDecisionAuthorization(true, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, WorkspaceActors.Web, reviewer.ReviewerRoleId, reviewer.ScopeIds, "browser-test-clock-authority"));
    }
}
