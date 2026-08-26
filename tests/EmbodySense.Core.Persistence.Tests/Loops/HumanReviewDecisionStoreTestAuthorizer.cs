using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class HumanReviewDecisionStoreTestAuthorizer : IHumanReviewDecisionAuthorizer
{
    internal bool IsAuthorized { get; init; } = true;
    internal string? ActorId { get; init; } = "reviewer-user";
    internal string? ReviewerRoleId { get; init; } = "reviewer-role-one";
    internal ImmutableArray<string> ScopeIds { get; init; } = ["scope-alpha", "scope-beta"];
    internal string? CorrelationId { get; init; } = "decision-store-authorization";

    public Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new HumanReviewDecisionAuthorization(
            IsAuthorized,
            request.RequestHash,
            request.DecisionOperationId,
            request.ProposalHash,
            request.EvaluatedAtUtc,
            ActorId,
            ReviewerRoleId,
            ScopeIds,
            CorrelationId));
}
