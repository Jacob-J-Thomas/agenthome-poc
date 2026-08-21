using EmbodySense.Core.Application.Governance.Authority.Delegation;
using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationTargetResolutionTests
{
    public static TheoryData<AuthorityDelegationTargetResolutionStatus, AuthorityDelegationServiceStatus> FailClosedPostures => new()
    {
        { AuthorityDelegationTargetResolutionStatus.Stale, AuthorityDelegationServiceStatus.TargetMismatch },
        { AuthorityDelegationTargetResolutionStatus.Disabled, AuthorityDelegationServiceStatus.TargetMismatch },
        { AuthorityDelegationTargetResolutionStatus.NotFound, AuthorityDelegationServiceStatus.TargetMismatch },
        { AuthorityDelegationTargetResolutionStatus.Invalid, AuthorityDelegationServiceStatus.TargetMismatch },
        { AuthorityDelegationTargetResolutionStatus.Unavailable, AuthorityDelegationServiceStatus.TargetUnavailable },
        { AuthorityDelegationTargetResolutionStatus.Ambiguous, AuthorityDelegationServiceStatus.Ambiguous },
    };

    [Theory]
    [MemberData(nameof(FailClosedPostures))]
    public async Task CreateAsync_MapsEveryTargetPosture(
        AuthorityDelegationTargetResolutionStatus sourceStatus,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TargetResolution = harness.CreateTargetResolution(sourceStatus);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(0, harness.CompletionCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsCrossWorkspaceTargetResolution()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TargetResolution = harness.CreateTargetResolution(
            AuthorityDelegationTargetResolutionStatus.Active,
            workspaceId: "workspace-sha256:" + new string('b', 64));

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.TargetMismatch, result.Status);
        Assert.Equal(0, harness.CompletionCount);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingTargetMaximumEvidenceHash()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.TargetResolution = harness.CreateTargetResolution(
            AuthorityDelegationTargetResolutionStatus.Active,
            maximumEvidenceHash: string.Empty);

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.TargetMismatch, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_RejectsIncomparableTargetMaximumsDuringAuthoritativeProofRecomputation()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var delegatedIds = harness.TargetResolution.NodeCapabilityIds;
        harness.TargetResolution = new AuthorityDelegationTargetResolution(
            AuthorityDelegationTargetResolutionStatus.Active,
            harness.Target,
            harness.Receipt.Intent.WorkspaceId,
            delegatedIds.Concat(["org.embodysense/role-only"]).Order(StringComparer.Ordinal).ToArray(),
            delegatedIds.Concat(["org.embodysense/loop-only"]).Order(StringComparer.Ordinal).ToArray(),
            delegatedIds,
            AuthorityDelegationServiceTestHarness.Hash('7'));

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.OutsideParentAuthority, result.Status);
        Assert.Null(result.Envelope);
    }
}
