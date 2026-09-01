using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class HumanReviewDecisionAuthorizationProviderTestDouble : IHumanReviewDecisionAuthorizationProvider
{
    public Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(HumanReviewDecisionAuthorizationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult<HumanReviewDecisionAuthorizationResult?>(new(
            HumanReviewDecisionAuthorizationStatus.Denied,
            request.RequestId,
            request.RequestHash,
            request.DecisionKind,
            request.DecisionOperationId,
            request.ProposalHash,
            request.EvaluatedAtUtc,
            null,
            null,
            ImmutableArray<string>.Empty,
            null));
}
