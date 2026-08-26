using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewDecisionHostAuthorizer : IHumanReviewDecisionAuthorizer
{
    public Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewDecisionAuthorization(
            true,
            request.RequestHash,
            request.DecisionOperationId,
            request.ProposalHash,
            request.EvaluatedAtUtc,
            "reviewer-user",
            "reviewer-role-one",
            ImmutableArray.Create("scope-alpha", "scope-beta"),
            "decision-host-authorization"));
}
