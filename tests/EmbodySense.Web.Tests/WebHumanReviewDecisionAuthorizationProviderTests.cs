using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Claims;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Http;

namespace EmbodySense.Web.Tests;

public sealed class WebHumanReviewDecisionAuthorizationProviderTests
{
    [Fact]
    public async Task Exact_authenticated_web_identity_is_authorized_and_forged_claims_are_ignored()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateContext(new ClaimsIdentity([
                new Claim(ClaimTypes.Name, "forged-actor"),
                new Claim(ClaimTypes.Role, "forged-role"),
                new Claim("scope", "forged-scope")], WebSessionAuthenticationDefaults.Scheme))
        };
        var provider = CreateProvider(accessor);

        var result = await provider.AuthorizeAsync(CreateRequest());

        Assert.NotNull(result);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Ready, result.Status);
        Assert.Equal(WorkspaceActors.Web, result.ActorId);
        Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, result.ReviewerRoleId);
        Assert.Equal(["review-scope"], result.ScopeIds.ToArray());
        Assert.DoesNotContain("forged", result.CorrelationId!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_context_and_multiple_authenticated_identities_fail_closed()
    {
        var accessor = new HttpContextAccessor();
        var provider = CreateProvider(accessor);

        var missing = await provider.AuthorizeAsync(CreateRequest());
        accessor.HttpContext = CreateContext(
            new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme),
            new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme));
        var multiple = await provider.AuthorizeAsync(CreateRequest());
        accessor.HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme), new ClaimsIdentity());
        var mixed = await provider.AuthorizeAsync(CreateRequest());

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, missing!.Status);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, multiple!.Status);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, mixed!.Status);
        Assert.Null(missing.ActorId);
        Assert.Null(multiple.CorrelationId);
    }

    [Fact]
    public async Task Wrong_authentication_scheme_is_denied_without_using_identity_claims()
    {
        var accessor = new HttpContextAccessor { HttpContext = CreateContext(new ClaimsIdentity([new Claim(ClaimTypes.Name, "trusted-looking")], "OtherScheme")) };
        var provider = CreateProvider(accessor);

        var result = await provider.AuthorizeAsync(CreateRequest());

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Denied, result!.Status);
        Assert.Null(result.ActorId);
        Assert.Null(result.ReviewerRoleId);
    }

    [Fact]
    public async Task Provider_reads_current_context_on_every_call_without_retaining_the_first_context()
    {
        var accessor = new HttpContextAccessor { HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var provider = CreateProvider(accessor);

        var first = await provider.AuthorizeAsync(CreateRequest());
        accessor.HttpContext = null;
        var missing = await provider.AuthorizeAsync(CreateRequest());
        accessor.HttpContext = CreateContext(new ClaimsIdentity([], "OtherScheme"));
        var second = await provider.AuthorizeAsync(CreateRequest());

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Ready, first!.Status);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, missing!.Status);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Denied, second!.Status);
    }

    [Fact]
    public async Task Exact_binding_is_echoed_and_correlation_is_deterministic_and_change_sensitive()
    {
        var accessor = new HttpContextAccessor { HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var provider = CreateProvider(accessor);
        var request = CreateRequest();

        var first = await provider.AuthorizeAsync(request);
        var same = await provider.AuthorizeAsync(request);
        var changed = await provider.AuthorizeAsync(request with { ProposalHash = Hash('c') });

        Assert.Equal(request.RequestId, first!.RequestId);
        Assert.Equal(request.RequestHash, first.RequestHash);
        Assert.Equal(request.DecisionKind, first.DecisionKind);
        Assert.Equal(request.DecisionOperationId, first.DecisionOperationId);
        Assert.Equal(request.ProposalHash, first.ProposalHash);
        Assert.Equal(request.EvaluatedAtUtc, first.EvaluatedAtUtc);
        Assert.Equal(first.CorrelationId, same!.CorrelationId);
        Assert.NotEqual(first.CorrelationId, changed!.CorrelationId);
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_authority_evaluation()
    {
        var accessor = new HttpContextAccessor { HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var provider = CreateProvider(accessor);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.AuthorizeAsync(CreateRequest(), cancellation.Token));
    }

    [Fact]
    public async Task Cancelled_request_context_fails_closed_without_authority()
    {
        var context = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.RequestAborted = cancellation.Token;
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = context });

        var result = await provider.AuthorizeAsync(CreateRequest());

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, result!.Status);
        Assert.Null(result.ActorId);
    }

    [Fact]
    public async Task Noncanonical_eligibility_fails_closed_as_unavailable()
    {
        var request = CreateRequest() with
        {
            EligibleReviewers = ImmutableArray.Create(new HumanReviewDecisionAuthorizationEligibility("wrong-role", ["review-scope"]))
        };
        var provider = CreateProvider(new HttpContextAccessor { HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) });

        var result = await provider.AuthorizeAsync(request);

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, result!.Status);
        Assert.Null(result.CorrelationId);
    }

    [Fact]
    public async Task Returned_scope_projection_is_detached_from_mutable_request_storage()
    {
        var scopes = new[] { "review-scope" };
        var request = CreateRequest(ImmutableCollectionsMarshal.AsImmutableArray(scopes));
        var accessor = new HttpContextAccessor { HttpContext = CreateContext(new ClaimsIdentity([], WebSessionAuthenticationDefaults.Scheme)) };
        var provider = CreateProvider(accessor);

        var result = await provider.AuthorizeAsync(request);
        scopes[0] = "forged-scope";

        Assert.Equal(["review-scope"], result!.ScopeIds.ToArray());
    }

    private static WebHumanReviewDecisionAuthorizationProvider CreateProvider(IHttpContextAccessor accessor)
        => new(accessor, new HumanReviewLocalDecisionAuthorizationPolicy());

    private static DefaultHttpContext CreateContext(params ClaimsIdentity[] identities)
        => new() { User = new ClaimsPrincipal(identities) };

    private static HumanReviewDecisionAuthorizationRequest CreateRequest(
        ImmutableArray<string>? scopes = null,
        string operationId = "decision-one")
        => new(
            "request-one",
            Hash('a'),
            HumanReviewDecisionKind.Approve,
            operationId,
            Hash('b'),
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            ImmutableArray.Create(new HumanReviewDecisionAuthorizationEligibility(
                GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                scopes ?? ["review-scope"])));

    private static string Hash(char value) => new(value, 64);
}
