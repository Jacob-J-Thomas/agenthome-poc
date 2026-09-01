using System.Security.Claims;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

public sealed class WebHumanReviewDecisionAuthorizationProviderCoverageTests
{
    [Fact]
    public async Task Throwing_accessor_and_missing_principal_fail_closed()
    {
        var request = CreateRequest();
        var throwingProvider = new WebHumanReviewDecisionAuthorizationProvider(new ThrowingHttpContextAccessor(), new HumanReviewLocalDecisionAuthorizationPolicy());
        var accessorFailure = await throwingProvider.AuthorizeAsync(request);

        var emptyPrincipal = new DefaultHttpContext { User = new ClaimsPrincipal() };
        var missingPrincipalProvider = new WebHumanReviewDecisionAuthorizationProvider(new HttpContextAccessor { HttpContext = emptyPrincipal }, new HumanReviewLocalDecisionAuthorizationPolicy());
        var principalFailure = await missingPrincipalProvider.AuthorizeAsync(request);

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, accessorFailure!.Status);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, principalFailure!.Status);
        Assert.Null(accessorFailure.ActorId);
        Assert.Null(principalFailure.CorrelationId);
    }

    private static HumanReviewDecisionAuthorizationRequest CreateRequest()
        => new(
            "request-one",
            new string('a', 64),
            HumanReviewDecisionKind.Approve,
            "operation-one",
            new string('b', 64),
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            [new HumanReviewDecisionAuthorizationEligibility(GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, ["review-scope"])]);
}
