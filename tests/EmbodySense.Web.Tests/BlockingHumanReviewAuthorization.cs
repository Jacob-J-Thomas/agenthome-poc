using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Web.Tests;

internal sealed class BlockingHumanReviewAuthorization : IHumanReviewDecisionAuthorizationProvider
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _release.Task.ConfigureAwait(false);
        return new HumanReviewDecisionAuthorizationResult(HumanReviewDecisionAuthorizationStatus.Ready, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "server-reviewer", request.EligibleReviewers[0].ReviewerRoleId, request.EligibleReviewers[0].ScopeIds, "server-correlation");
    }

    public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release() => _release.TrySetResult();
}
