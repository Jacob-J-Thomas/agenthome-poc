using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationParentLifecycleTests
{
    public static TheoryData<AuthorityGrantResolutionStatus, AuthorityDelegationServiceStatus> FailClosedPostures => new()
    {
        { AuthorityGrantResolutionStatus.NotEffective, AuthorityDelegationServiceStatus.ParentNotEffective },
        { AuthorityGrantResolutionStatus.Suspended, AuthorityDelegationServiceStatus.ParentSuspended },
        { AuthorityGrantResolutionStatus.Revoked, AuthorityDelegationServiceStatus.ParentRevoked },
        { AuthorityGrantResolutionStatus.Expired, AuthorityDelegationServiceStatus.ParentExpired },
        { AuthorityGrantResolutionStatus.Stale, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.ProfileUnavailable, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.RoleUnavailable, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.LoopUnavailable, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.CeilingExceeded, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.NotFound, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.Invalid, AuthorityDelegationServiceStatus.ParentReplaced },
        { AuthorityGrantResolutionStatus.Unavailable, AuthorityDelegationServiceStatus.Unavailable },
        { AuthorityGrantResolutionStatus.Ambiguous, AuthorityDelegationServiceStatus.Ambiguous },
        { AuthorityGrantResolutionStatus.Unknown, AuthorityDelegationServiceStatus.Ambiguous },
    };

    [Theory]
    [MemberData(nameof(FailClosedPostures))]
    public async Task CreateAsync_MapsEveryParentLifecyclePosture(
        AuthorityGrantResolutionStatus sourceStatus,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.GrantResolution = harness.GrantResolution with { Status = sourceStatus };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(0, harness.OriginCount);
    }

    [Theory]
    [MemberData(nameof(FailClosedPostures))]
    public async Task RevalidateAsync_MapsEveryParentLifecyclePosture(
        AuthorityGrantResolutionStatus sourceStatus,
        AuthorityDelegationServiceStatus expected)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(
            (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.GrantResolution = harness.GrantResolution with { Status = sourceStatus };

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsDependencyEvidenceDriftAsParentReplacement()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(
            (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var originCount = harness.OriginCount;
        harness.GrantResolution = harness.GrantResolution with
        {
            DependencyEvidenceHash = AuthorityDelegationServiceTestHarness.Hash('9'),
        };

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.ParentReplaced, result.Status);
        Assert.Equal(originCount, harness.OriginCount);
    }

    [Fact]
    public async Task RevalidateAsync_NeverFollowsNarrowerSuccessorGrant()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(
            (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var successorReference = new AuthorityGrantReference(
            harness.Grant.GrantId,
            harness.Grant.Revision,
            "sha256:" + new string('9', 64));
        harness.GrantResolution = harness.GrantResolution with
        {
            Status = AuthorityGrantResolutionStatus.Stale,
            CurrentGrant = harness.Grant with { ContentHash = successorReference.ContentHash },
        };

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.ParentReplaced, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidCurrentGrantEvenWhenItsReferenceFieldsMatch()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var forgedCurrent = harness.Grant with
        {
            RecordedAtUtc = harness.Grant.RecordedAtUtc.AddTicks(1),
        };
        harness.GrantResolution = harness.GrantResolution with { CurrentGrant = forgedCurrent };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Null(result.Envelope);
    }

    [Theory]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, false)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, false)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, false)]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, true)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, true)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, true)]
    public async Task CreateAsync_RejectsActiveResolutionThatEmbedsTerminalGrant(
        AuthorityGrantLifecycleStatus status,
        bool currentGrantOnly)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var terminal = AuthorityGrantHash.Apply(harness.Grant with
        {
            Status = status,
            ContentHash = string.Empty,
        });
        if (currentGrantOnly)
        {
            harness.GrantResolution = harness.GrantResolution with { CurrentGrant = terminal };
        }
        else
        {
            harness.RebindParentGrant(terminal);
        }

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(0, harness.OriginCount);
    }

    [Theory]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, false)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, false)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, false)]
    [InlineData(AuthorityGrantLifecycleStatus.Suspended, true)]
    [InlineData(AuthorityGrantLifecycleStatus.Revoked, true)]
    [InlineData(AuthorityGrantLifecycleStatus.Expired, true)]
    public async Task RevalidateAsync_RejectsActiveResolutionThatEmbedsTerminalGrant(
        AuthorityGrantLifecycleStatus status,
        bool currentGrantOnly)
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        AuthorityDelegationEnvelope envelope;
        if (currentGrantOnly)
        {
            envelope = Assert.IsType<AuthorityDelegationEnvelope>(
                (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        }
        else
        {
            envelope = null!;
        }

        var terminal = AuthorityGrantHash.Apply(harness.Grant with
        {
            Status = status,
            ContentHash = string.Empty,
        });
        if (currentGrantOnly)
        {
            harness.GrantResolution = harness.GrantResolution with { CurrentGrant = terminal };
        }
        else
        {
            harness.RebindParentGrant(terminal);
            envelope = harness.CreateEnvelopeForCurrentContext();
        }

        var originCount = harness.OriginCount;

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.ParentReplaced, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(originCount, harness.OriginCount);
    }

    [Fact]
    public async Task CreateAsync_AcceptsIndependentlyReconstructedCanonicalCurrentGrant()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        RebindGrantWithNonEmptyAuthority(harness);
        var reconstructed = ReconstructGrant(harness.Grant);
        Assert.NotSame(harness.Grant.RequestedCeiling.DataClasses, reconstructed.RequestedCeiling.DataClasses);
        Assert.True(AuthorityGrantContractValidator.Validate(reconstructed).IsValid);
        harness.GrantResolution = harness.GrantResolution with { CurrentGrant = reconstructed };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Created, result.Status);
    }

    [Fact]
    public async Task RevalidateAsync_AcceptsIndependentlyReconstructedCanonicalCurrentGrant()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        RebindGrantWithNonEmptyAuthority(harness);
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(
            (await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.GrantResolution = harness.GrantResolution with { CurrentGrant = ReconstructGrant(harness.Grant) };

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.Valid, result.Status);
    }

    [Fact]
    public async Task CreateAsync_RejectsRehashedOneFieldCurrentGrantSubstitution()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        Assert.True(AuthorityPurpose.TryParse("A substituted grant reason.", out var changedReason, out _));
        var substituted = AuthorityGrantHash.Apply(harness.Grant with
        {
            Reason = changedReason!,
            ContentHash = string.Empty,
        });
        Assert.True(AuthorityGrantContractValidator.Validate(substituted).IsValid);
        harness.GrantResolution = harness.GrantResolution with { CurrentGrant = substituted };

        var result = await harness.CreateService().CreateAsync(harness.Request);

        Assert.Equal(AuthorityDelegationServiceStatus.Ambiguous, result.Status);
        Assert.Null(result.Envelope);
    }

    private static AuthorityGrant ReconstructGrant(AuthorityGrant source)
        => source with
        {
            RequestedCeiling = new AuthorityCeiling(
                source.RequestedCeiling.Capabilities.ToArray(),
                source.RequestedCeiling.DataClasses.ToArray(),
                source.RequestedCeiling.MaxTargetCount,
                source.RequestedCeiling.MaxSideEffectClass,
                source.RequestedCeiling.AllowsRecurrence,
                source.RequestedCeiling.AllowsExternalPublication,
                source.RequestedCeiling.AllowsIrreversibleAction),
        };

    private static void RebindGrantWithNonEmptyAuthority(AuthorityDelegationServiceTestHarness harness)
    {
        Assert.True(CapabilityDataClass.TryParse("workspace-content", out var dataClass, out _));
        var ceiling = new AuthorityCeiling(
            harness.Grant.RequestedCeiling.Capabilities,
            [dataClass!],
            harness.Grant.RequestedCeiling.MaxTargetCount,
            harness.Grant.RequestedCeiling.MaxSideEffectClass,
            harness.Grant.RequestedCeiling.AllowsRecurrence,
            harness.Grant.RequestedCeiling.AllowsExternalPublication,
            harness.Grant.RequestedCeiling.AllowsIrreversibleAction);
        harness.RebindParentGrant(AuthorityGrantHash.Apply(harness.Grant with
        {
            RequestedCeiling = ceiling,
            ContentHash = string.Empty,
        }));
    }
}
