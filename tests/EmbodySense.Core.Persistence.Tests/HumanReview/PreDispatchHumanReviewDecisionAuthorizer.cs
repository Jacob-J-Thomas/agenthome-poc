using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

internal sealed class PreDispatchHumanReviewDecisionAuthorizer : IHumanReviewDecisionAuthorizer
{
    public Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HumanReviewDecisionAuthorization(
            true,
            request.RequestHash,
            request.DecisionOperationId,
            request.ProposalHash,
            request.EvaluatedAtUtc,
            "reviewer-user",
            GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
            ImmutableArray.Create("pre-dispatch-effect"),
            "process-effect-authorization"));
    }
}
