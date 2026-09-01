using System.Collections.Immutable;
using System.Runtime.InteropServices;
using ApplicationAuthorizationRequest = EmbodySense.Core.Application.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using CommonDecisionKind = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionKind;
using CommonDecisionProposal = EmbodySense.Core.Common.HumanReview.Models.HumanReviewDecisionProposal;
using CommonRequest = EmbodySense.Core.Common.HumanReview.Models.HumanReviewRequest;
using CommonPurpose = EmbodySense.Core.Common.HumanReview.Models.HumanReviewPurpose;
using CommonReviewerScope = EmbodySense.Core.Common.HumanReview.Models.HumanReviewReviewerScope;
using EmbodySense.Core.Startup.HumanReview;
using StartupAuthorizationStatus = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationStatus;
using StartupDecisionKind = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionKind;
using StartupAuthorizationRequest = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;
using EmbodySense.Core.Startup.HumanReview.Models;

namespace EmbodySense.Core.Startup.Tests.HumanReview;

public sealed class HumanReviewAuthorityAdapterTests
{
    [Fact]
    public async Task MissingProviderFailsClosedAsUnavailable()
    {
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(null);
        var request = Request();

        var result = await adapter.AuthorizeAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProviderResponseMustEchoEveryServerBinding()
    {
        var request = Request();
        var provider = new HumanReviewRecordingAuthorizationProvider(request with { });
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var result = await adapter.AuthorizeAsync(request);

        Assert.NotNull(result);
        Assert.Equal(request.RequestHash, result!.RequestHash);
        Assert.Equal(request.DecisionOperationId, result.DecisionOperationId);
        Assert.Equal(request.ProposalHash, result.ProposalHash);
        Assert.Equal(request.EvaluatedAtUtc, result.EvaluatedAtUtc);
        Assert.Equal(["scope"], result.ScopeIds.ToArray());
        Assert.NotNull(provider.Request);
        Assert.Equal(request.Request.RequestId, provider.Request!.RequestId);
        Assert.Equal(request.RequestHash, provider.Request.RequestHash);
        Assert.Equal(StartupDecisionKind.Approve, provider.Request.DecisionKind);
    }

    [Fact]
    public async Task ProviderReceivesTheExactDynamicEligibilityProjection()
    {
        var request = RequestWithEligibility(new CommonReviewerScope("reviewer-role-two", ["scope-alpha", "scope-beta"]));
        var provider = new HumanReviewRecordingAuthorizationProvider(request);
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var result = await adapter.AuthorizeAsync(request);

        Assert.NotNull(result);
        Assert.Equal("reviewer-role-two", result!.ReviewerRoleId);
        Assert.Equal(["scope-alpha", "scope-beta"], result.ScopeIds.ToArray());
        Assert.Equal("reviewer-role-two", provider.Request!.EligibleReviewers[0].ReviewerRoleId);
        Assert.Equal(["scope-alpha", "scope-beta"], provider.Request.EligibleReviewers[0].ScopeIds.ToArray());
    }

    [Fact]
    public async Task ProviderRequestEligibilityIsDetachedFromCanonicalBackingArrays()
    {
        var scopeBacking = new[] { "scope" };
        var reviewerBacking = new[] { new CommonReviewerScope("reviewer", ImmutableCollectionsMarshal.AsImmutableArray(scopeBacking)) };
        var source = RequestWithEligibility(ImmutableCollectionsMarshal.AsImmutableArray(reviewerBacking));
        var provider = new HumanReviewRecordingAuthorizationProvider(source);
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var result = await adapter.AuthorizeAsync(source);

        scopeBacking[0] = "tampered";
        reviewerBacking[0] = new CommonReviewerScope("tampered-role", ImmutableArray.Create("tampered-scope"));

        Assert.NotNull(result);
        Assert.Equal("reviewer", provider.Request!.EligibleReviewers[0].ReviewerRoleId);
        Assert.Equal(["scope"], provider.Request.EligibleReviewers[0].ScopeIds.ToArray());
    }

    [Fact]
    public async Task DeniedProviderResponseIsBoundAndPreservesNoAuthority()
    {
        var request = Request();
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(new HumanReviewRecordingAuthorizationProvider(request, StartupAuthorizationStatus.Denied));

        var result = await adapter.AuthorizeAsync(request);

        Assert.NotNull(result);
        Assert.False(result!.IsAuthorized);
        Assert.Null(result.ActorId);
        Assert.Null(result.ReviewerRoleId);
        Assert.Empty(result.ScopeIds);
        Assert.Null(result.CorrelationId);
    }

    [Fact]
    public async Task UnavailableProviderResponseFailsClosed()
    {
        var request = Request();
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(new HumanReviewRecordingAuthorizationProvider(request, StartupAuthorizationStatus.Unavailable));

        var result = await adapter.AuthorizeAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task UnknownProviderResponseFailsClosed()
    {
        var request = Request();
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(new HumanReviewRecordingAuthorizationProvider(request, StartupAuthorizationStatus.Unknown));

        var result = await adapter.AuthorizeAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task ProviderExceptionFailsClosed()
    {
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(new HumanReviewThrowingAuthorizationProvider());

        var result = await adapter.AuthorizeAsync(Request());

        Assert.Null(result);
    }

    [Fact]
    public async Task Missing_canonical_request_or_proposal_fails_closed_before_provider_call()
    {
        var provider = new HumanReviewRecordingAuthorizationProvider(Request());
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var missingRequest = await adapter.AuthorizeAsync(Request() with { Request = null! });
        var missingProposal = await adapter.AuthorizeAsync(Request() with { Proposal = null! });

        Assert.Null(missingRequest);
        Assert.Null(missingProposal);
        Assert.Null(provider.Request);
    }

    [Fact]
    public async Task Non_matching_canonical_hash_or_operation_fails_closed_before_provider_call()
    {
        var provider = new HumanReviewRecordingAuthorizationProvider(Request());
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var mismatchedRequestHash = await adapter.AuthorizeAsync(Request() with { RequestHash = "other-request-hash" });
        var mismatchedOperation = await adapter.AuthorizeAsync(Request() with { DecisionOperationId = "other-operation" });

        Assert.Null(mismatchedRequestHash);
        Assert.Null(mismatchedOperation);
        Assert.Null(provider.Request);
    }

    [Fact]
    public void Public_authority_provider_contract_contains_no_application_binding_or_authority_types()
    {
        var method = typeof(IHumanReviewDecisionAuthorizationProvider).GetMethod(nameof(IHumanReviewDecisionAuthorizationProvider.AuthorizeAsync));
        var requestType = method!.GetParameters()[0].ParameterType;
        var resultType = method.ReturnType;

        Assert.Equal(typeof(StartupAuthorizationRequest), requestType);
        Assert.Contains("HumanReviewDecisionAuthorizationResult", resultType.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Application", requestType.FullName, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding", string.Join('|', requestType.GetProperties().Select(property => property.Name)), StringComparison.Ordinal);
        Assert.DoesNotContain("Grant", string.Join('|', requestType.GetProperties().Select(property => property.Name)), StringComparison.Ordinal);
        Assert.DoesNotContain("Actor", string.Join('|', requestType.GetProperties().Select(property => property.Name)), StringComparison.Ordinal);
        var eligibilityType = typeof(StartupAuthorizationRequest).GetProperty(nameof(StartupAuthorizationRequest.EligibleReviewers))!.PropertyType;
        Assert.DoesNotContain("Core.Application", eligibilityType.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Common", eligibilityType.ToString(), StringComparison.Ordinal);
        var eligibilityEntryType = eligibilityType.GetGenericArguments()[0];
        Assert.Equal(["ReviewerRoleId", "ScopeIds"], eligibilityEntryType.GetProperties().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task MismatchedProviderResponseFailsClosed()
    {
        var request = Request();
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(new HumanReviewRecordingAuthorizationProvider(request with { }, mismatch: true));

        var result = await adapter.AuthorizeAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadyResponseForUnknownEligibilityFailsClosed()
    {
        var request = Request();
        var provider = new HumanReviewDelegateAuthorizationProvider(providerRequest => new HumanReviewDecisionAuthorizationResult(
            StartupAuthorizationStatus.Ready,
            providerRequest.RequestId,
            providerRequest.RequestHash,
            providerRequest.DecisionKind,
            providerRequest.DecisionOperationId,
            providerRequest.ProposalHash,
            providerRequest.EvaluatedAtUtc,
            "actor",
            "unknown-role",
            ["scope"],
            "correlation"));
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var result = await adapter.AuthorizeAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadyResponseWithMalformedScopesFailsClosed()
    {
        var request = Request();
        var provider = new HumanReviewDelegateAuthorizationProvider(providerRequest => new HumanReviewDecisionAuthorizationResult(
            StartupAuthorizationStatus.Ready,
            providerRequest.RequestId,
            providerRequest.RequestHash,
            providerRequest.DecisionKind,
            providerRequest.DecisionOperationId,
            providerRequest.ProposalHash,
            providerRequest.EvaluatedAtUtc,
            "actor",
            "reviewer",
            ["scope-b", "scope-a"],
            "correlation"));
        var adapter = HumanReviewDecisionAuthorizerTestFactory.Create(provider);

        var result = await adapter.AuthorizeAsync(request);

        Assert.Null(result);
    }

    [Fact]
    public void TimeProviderAdapterReturnsTrustedUtc()
    {
        var expected = new DateTimeOffset(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var adapter = HumanReviewDecisionAuthorizerTestFactory.CreateTrustedClock(new HumanReviewFixedTimeProvider(expected));

        Assert.Equal(expected, adapter.UtcNow);
        Assert.Equal(TimeSpan.Zero, adapter.UtcNow.Offset);
    }

    private static ApplicationAuthorizationRequest Request()
    {
        var now = new DateTimeOffset(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var review = new CommonRequest(1, "request-id", "request-operation", null!, CommonPurpose.Continuation, [], [new CommonReviewerScope("reviewer", ["scope"])], null!, [], null!, null!, "request-hash");
        var proposal = new CommonDecisionProposal(1, "operation-id", CommonDecisionKind.Approve, null, "proposal-hash");
        return new ApplicationAuthorizationRequest(review, proposal, "request-hash", "operation-id", "proposal-hash", now);
    }

    private static ApplicationAuthorizationRequest RequestWithEligibility(params CommonReviewerScope[] eligibleReviewers)
        => RequestWithEligibility(eligibleReviewers.ToImmutableArray());

    private static ApplicationAuthorizationRequest RequestWithEligibility(ImmutableArray<CommonReviewerScope> eligibleReviewers)
    {
        var request = Request();
        return request with { Request = request.Request with { EligibleReviewers = eligibleReviewers } };
    }
}
