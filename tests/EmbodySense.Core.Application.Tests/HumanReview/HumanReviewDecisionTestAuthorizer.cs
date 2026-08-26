using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class HumanReviewDecisionTestAuthorizer : IHumanReviewDecisionAuthorizer
{
    public List<HumanReviewDecisionAuthorizationRequest> Requests { get; } = [];
    public Func<HumanReviewDecisionAuthorizationRequest, CancellationToken, Task<HumanReviewDecisionAuthorization?>>? Handler { get; init; }
    public bool IsAuthorized { get; set; } = true;
    public string? ActorId { get; set; } = "reviewer-user";
    public string? ReviewerRoleId { get; set; } = "reviewer-role-one";
    public ImmutableArray<string> ScopeIds { get; set; } = ["scope-alpha", "scope-beta"];
    public string? CorrelationId { get; set; } = "authorization-one";

    public async Task<HumanReviewDecisionAuthorization> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var result = Handler is null
            ? new HumanReviewDecisionAuthorization(IsAuthorized, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, ActorId, ReviewerRoleId, ScopeIds, CorrelationId)
            : await Handler(request, cancellationToken);
        return result!;
    }
}
