using EmbodySense.Core.Application.Governance.Authority.Delegation;
using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationCompletionTests
{
    public static TheoryData<AuthorityDelegationCompletionStatus, AuthorityDelegationServiceStatus> FailClosedPostures => new()
    {
        { AuthorityDelegationCompletionStatus.ParentCompleted, AuthorityDelegationServiceStatus.ParentCompleted },
        { AuthorityDelegationCompletionStatus.Unavailable, AuthorityDelegationServiceStatus.Unavailable },
        { AuthorityDelegationCompletionStatus.Ambiguous, AuthorityDelegationServiceStatus.Ambiguous },
        { AuthorityDelegationCompletionStatus.Conflict, AuthorityDelegationServiceStatus.Ambiguous },
        { AuthorityDelegationCompletionStatus.Unknown, AuthorityDelegationServiceStatus.Ambiguous },
    };

    [Theory]
    [MemberData(nameof(FailClosedPostures))]
    public async Task CreateAsync_MapsEveryCompletionPosture(
        AuthorityDelegationCompletionStatus sourceStatus,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.CompletionResolution = new AuthorityDelegationCompletionResolution(sourceStatus);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Envelope);
    }

    [Theory]
    [InlineData(AuthorityDelegationCompletionStatus.ParentCompleted, AuthorityDelegationServiceStatus.ParentCompleted)]
    public async Task RevalidateAsync_CompletionStopsBeforeGrantRevalidation(
        AuthorityDelegationCompletionStatus sourceStatus,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.CompletionResolution = new AuthorityDelegationCompletionResolution(sourceStatus);
        harness.Calls.Clear();

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(expected, result.Status);
        Assert.Equal(["transaction", "completion"], harness.Calls);
        Assert.Null(result.Envelope);
    }

    [Theory]
    [InlineData(AuthorityDelegationCompletionConstraintKind.None, AuthorityDelegationServiceStatus.Created)]
    [InlineData(AuthorityDelegationCompletionConstraintKind.TargetCompletion, AuthorityDelegationServiceStatus.EnvelopeCompleted)]
    public async Task CreateAsync_TargetCompletionHonorsTheDeclaredCompletionConstraint(
        AuthorityDelegationCompletionConstraintKind constraint,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.Request = harness.Request with
        {
            Boundary = constraint == AuthorityDelegationCompletionConstraintKind.None
                ? harness.Request.Boundary
                : new AuthorityDelegationBoundary(harness.Time.UtcNow, null, constraint),
        };
        harness.OriginResolution = harness.CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current);
        harness.CompletionResolution = new AuthorityDelegationCompletionResolution(AuthorityDelegationCompletionStatus.TargetCompleted);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == AuthorityDelegationServiceStatus.Created, result.Envelope is not null);
    }

    [Theory]
    [InlineData(AuthorityDelegationCompletionConstraintKind.None, AuthorityDelegationServiceStatus.Valid)]
    [InlineData(AuthorityDelegationCompletionConstraintKind.TargetCompletion, AuthorityDelegationServiceStatus.EnvelopeCompleted)]
    public async Task RevalidateAsync_TargetCompletionHonorsTheEnvelopeCompletionConstraint(
        AuthorityDelegationCompletionConstraintKind constraint,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.Request = harness.Request with
        {
            Boundary = constraint == AuthorityDelegationCompletionConstraintKind.None
                ? harness.Request.Boundary
                : new AuthorityDelegationBoundary(harness.Time.UtcNow, null, constraint),
        };
        harness.OriginResolution = harness.CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current);
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.CompletionResolution = new AuthorityDelegationCompletionResolution(AuthorityDelegationCompletionStatus.TargetCompleted);

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == AuthorityDelegationServiceStatus.Valid, result.Envelope is not null);
    }
}
