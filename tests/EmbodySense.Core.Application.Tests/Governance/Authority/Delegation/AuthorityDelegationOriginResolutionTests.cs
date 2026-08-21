using EmbodySense.Core.Application.Governance.Authority.Delegation;
using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationOriginResolutionTests
{
    public static TheoryData<AuthorityDelegationOriginResolutionStatus, AuthorityDelegationServiceStatus> FailClosedPostures => new()
    {
        { AuthorityDelegationOriginResolutionStatus.Drifted, AuthorityDelegationServiceStatus.OriginDrifted },
        { AuthorityDelegationOriginResolutionStatus.Completed, AuthorityDelegationServiceStatus.OriginDrifted },
        { AuthorityDelegationOriginResolutionStatus.NotFound, AuthorityDelegationServiceStatus.OriginDrifted },
        { AuthorityDelegationOriginResolutionStatus.Invalid, AuthorityDelegationServiceStatus.OriginDrifted },
        { AuthorityDelegationOriginResolutionStatus.Unavailable, AuthorityDelegationServiceStatus.OriginUnavailable },
        { AuthorityDelegationOriginResolutionStatus.Ambiguous, AuthorityDelegationServiceStatus.Ambiguous },
    };

    [Theory]
    [MemberData(nameof(FailClosedPostures))]
    public async Task CreateAsync_MapsEveryOriginPosture(
        AuthorityDelegationOriginResolutionStatus sourceStatus,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.OriginResolution = harness.CreateOriginResolution(sourceStatus);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(0, harness.TargetCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsOriginTargetSubstitution()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var changedTarget = harness.Target with { BindingEvidenceHash = AuthorityDelegationServiceTestHarness.Hash('4') };
        harness.OriginResolution = harness.CreateOriginResolution(AuthorityDelegationOriginResolutionStatus.Current, target: changedTarget);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.OriginMismatch, result.Status);
        Assert.Equal(0, harness.TargetCount);
    }

    [Fact]
    public async Task CreateAsync_AcceptsSeparatelyReconstructedEqualPurpose()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        Assert.True(AuthorityPurpose.TryParse(harness.Request.Purpose.Value, out var reconstructed, out _));
        Assert.NotSame(harness.Request.Purpose, reconstructed);
        harness.OriginResolution = harness.CreateOriginResolution(
            AuthorityDelegationOriginResolutionStatus.Current,
            purpose: reconstructed);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, result.Status);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsChangedSemanticOriginEvidenceWithoutRepinning()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var targetCount = harness.TargetCount;
        harness.OriginResolution = harness.CreateOriginResolution(
            AuthorityDelegationOriginResolutionStatus.Current,
            AuthorityDelegationServiceTestHarness.Hash('9'));

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.OriginDrifted, result.Status);
        Assert.Equal(targetCount, harness.TargetCount);
    }

    [Fact]
    public async Task RevalidateAsync_AcceptsSeparatelyReconstructedEqualPurpose()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        Assert.True(AuthorityPurpose.TryParse(envelope.Purpose.Value, out var reconstructed, out _));
        Assert.NotSame(envelope.Purpose, reconstructed);
        harness.OriginResolution = harness.CreateOriginResolution(
            AuthorityDelegationOriginResolutionStatus.Current,
            purpose: reconstructed);

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.Valid, result.Status);
    }
}
