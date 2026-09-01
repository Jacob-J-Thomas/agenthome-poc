using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

public sealed class HumanReviewLocalDecisionAuthorizationPolicyTests
{
    [Fact]
    public void Canonical_request_is_ready_with_server_owned_identity_and_detached_scopes()
    {
        var policy = new HumanReviewLocalDecisionAuthorizationPolicy();
        var scopes = new[] { "review-scope", "review-scope-two" };
        var request = CreateRequest(ImmutableCollectionsMarshal.AsImmutableArray(scopes));

        var result = policy.Authorize(request);
        scopes[0] = "forged-scope";

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Ready, result.Status);
        Assert.Equal(WorkspaceActors.Web, result.ActorId);
        Assert.Equal(GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, result.ReviewerRoleId);
        Assert.Equal(["review-scope", "review-scope-two"], result.ScopeIds.ToArray());
        Assert.Matches("^[0-9a-f]{64}$", result.CorrelationId!);
    }

    [Fact]
    public void Correlation_is_deterministic_and_changes_when_an_exact_request_fact_changes()
    {
        var policy = new HumanReviewLocalDecisionAuthorizationPolicy();
        var first = policy.Authorize(CreateRequest());
        var same = policy.Authorize(CreateRequest());
        var changed = policy.Authorize(CreateRequest(operationId: "decision-two"));

        Assert.Equal(first.CorrelationId, same.CorrelationId);
        Assert.NotEqual(first.CorrelationId, changed.CorrelationId);
    }

    [Theory]
    [InlineData("wrong-role", "review-scope")]
    [InlineData("governed-reviewer", "")]
    [InlineData("governed-reviewer", "Review-Scope")]
    [InlineData("governed-reviewer", "review-scope-two,review-scope")]
    public void Noncanonical_role_or_scopes_are_unavailable(string role, string scopeText)
    {
        var policy = new HumanReviewLocalDecisionAuthorizationPolicy();
        var scopes = scopeText.Length == 0 ? ImmutableArray<string>.Empty : ImmutableArray.CreateRange(scopeText.Split(','));
        var request = CreateRequest(scopes, role);

        var result = policy.Authorize(request);

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, result.Status);
        Assert.Null(result.ActorId);
        Assert.Null(result.ReviewerRoleId);
        Assert.Empty(result.ScopeIds);
        Assert.Null(result.CorrelationId);
    }

    [Fact]
    public void Eligibility_must_contain_exactly_one_canonical_entry()
    {
        var policy = new HumanReviewLocalDecisionAuthorizationPolicy();
        var request = CreateRequest() with
        {
            EligibleReviewers = ImmutableArray.Create(
                new HumanReviewDecisionAuthorizationEligibility(GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, ["review-scope"]),
                new HumanReviewDecisionAuthorizationEligibility(GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId, ["review-scope"]))
        };

        var result = policy.Authorize(request);

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, result.Status);
        Assert.Null(result.CorrelationId);
    }

    [Fact]
    public void Null_or_malformed_request_fails_closed_without_authority()
    {
        var policy = new HumanReviewLocalDecisionAuthorizationPolicy();

        var nullResult = policy.Authorize(null);
        var malformedResult = policy.Authorize(CreateRequest(requestId: "Invalid Request"));

        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, nullResult.Status);
        Assert.Equal(HumanReviewDecisionAuthorizationStatus.Unavailable, malformedResult.Status);
        Assert.Null(malformedResult.ActorId);
        Assert.Null(malformedResult.ReviewerRoleId);
    }

    private static HumanReviewDecisionAuthorizationRequest CreateRequest(
        ImmutableArray<string>? scopes = null,
        string? role = null,
        string operationId = "decision-one",
        string requestId = "request-one")
    {
        return new HumanReviewDecisionAuthorizationRequest(
            requestId,
            Hash('a'),
            HumanReviewDecisionKind.Approve,
            operationId,
            Hash('b'),
            new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
            ImmutableArray.Create(new HumanReviewDecisionAuthorizationEligibility(
                role ?? GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                scopes ?? ["review-scope"])));
    }

    private static string Hash(char value) => new(value, 64);
}
