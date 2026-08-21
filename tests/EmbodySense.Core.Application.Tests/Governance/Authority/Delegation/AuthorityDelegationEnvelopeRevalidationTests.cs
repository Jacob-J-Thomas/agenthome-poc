using EmbodySense.Core.Application.Governance.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

public sealed class AuthorityDelegationEnvelopeRevalidationTests
{
    [Fact]
    public async Task RevalidateAsync_ReturnsSameImmutableEnvelopeAfterExactCurrentProof()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var created = await harness.CreateService().CreateAsync(harness.Request);
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>(created.Envelope);
        harness.Calls.Clear();

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.Valid, result.Status);
        var validated = Assert.IsType<AuthorityDelegationEnvelope>(result.Envelope);
        Assert.Equal(envelope.ContentHash, validated.ContentHash);
        Assert.True(EmbodySense.Core.Common.Authority.Delegation.AuthorityDelegationContractValidator.Validate(validated).IsValid);
        Assert.Equal(["transaction", "completion", "grant", "origin", "target"], harness.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsCrossWorkspaceReuseBeforeAnySourceCall()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.Calls.Clear();
        var use = harness.UseRequest(envelope) with
        {
            WorkspaceId = "workspace-sha256:" + new string('b', 64),
        };

        var result = await harness.CreateService().RevalidateAsync(use);

        Assert.Equal(AuthorityDelegationServiceStatus.OriginMismatch, result.Status);
        Assert.Null(result.Envelope);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsCrossTargetReuseBeforeAnySourceCall()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.Calls.Clear();
        var changedTarget = envelope.Target with { BindingEvidenceHash = AuthorityDelegationServiceTestHarness.Hash('5') };
        var use = harness.UseRequest(envelope) with { Target = changedTarget };

        var result = await harness.CreateService().RevalidateAsync(use);

        Assert.Equal(AuthorityDelegationServiceStatus.TargetMismatch, result.Status);
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsEveryCrossScopeReplayBeforeAnySourceCall()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var use = harness.UseRequest(envelope);
        var parent = envelope.ParentEvidence.ParentExecution;
        var otherRevision = GovernedLoopRevisionReference.Create(
            parent.Revision.SchemaVersion,
            parent.Revision.GraphId,
            "other-revision",
            parent.Revision.ExecutableHash);
        var changedRole = envelope.Target with
        {
            Role = envelope.Target.Role with { ContentHash = AuthorityDelegationServiceTestHarness.Hash('9') },
        };
        var loopTarget = new AuthorityDelegationTargetBinding(
            AuthorityDelegationTargetKind.Loop,
            envelope.Target.Role,
            harness.Grant.Binding.Loop,
            null,
            envelope.Target.BindingEvidenceHash);
        var nodeTarget = new AuthorityDelegationTargetBinding(
            AuthorityDelegationTargetKind.Node,
            envelope.Target.Role,
            harness.Grant.Binding.Loop,
            "other-node",
            envelope.Target.BindingEvidenceHash);
        Assert.True(AuthorityPurpose.TryParse("A different exact delegated purpose.", out var changedPurpose, out _));
        var replays = new[]
        {
            (use with { WorkspaceId = "workspace-sha256:" + new string('b', 64) }, AuthorityDelegationServiceStatus.OriginMismatch),
            (use with { ParentExecution = GovernedLoopExecutionBinding.Create(parent.SchemaVersion, "other-run", parent.Revision, parent.ExecutionGeneration) }, AuthorityDelegationServiceStatus.OriginMismatch),
            (use with { ParentExecution = GovernedLoopExecutionBinding.Create(parent.SchemaVersion, parent.RunId, otherRevision, parent.ExecutionGeneration) }, AuthorityDelegationServiceStatus.OriginMismatch),
            (use with { ParentExecution = GovernedLoopExecutionBinding.Create(parent.SchemaVersion, parent.RunId, parent.Revision, parent.ExecutionGeneration + 1) }, AuthorityDelegationServiceStatus.OriginMismatch),
            (use with { OriginNodeId = "other-origin" }, AuthorityDelegationServiceStatus.OriginMismatch),
            (use with { OriginNodeAttempt = use.OriginNodeAttempt + 1 }, AuthorityDelegationServiceStatus.OriginMismatch),
            (use with { Target = changedRole }, AuthorityDelegationServiceStatus.TargetMismatch),
            (use with { Target = loopTarget }, AuthorityDelegationServiceStatus.TargetMismatch),
            (use with { Target = nodeTarget }, AuthorityDelegationServiceStatus.TargetMismatch),
            (use with { TargetClass = "other-target-class" }, AuthorityDelegationServiceStatus.TargetMismatch),
            (use with { OperationClass = "other-operation-class" }, AuthorityDelegationServiceStatus.TargetMismatch),
            (use with { Purpose = changedPurpose! }, AuthorityDelegationServiceStatus.TargetMismatch),
        };

        foreach (var (replay, expected) in replays)
        {
            harness.Calls.Clear();
            var result = await harness.CreateService().RevalidateAsync(replay);
            Assert.Equal(expected, result.Status);
            Assert.Null(result.Envelope);
            Assert.Empty(harness.Calls);
        }
    }

    [Fact]
    public async Task RevalidateAsync_BeforeInclusiveEffectiveInstantIsNotEffective()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        harness.Request = harness.Request with
        {
            Boundary = new AuthorityDelegationBoundary(
                harness.Time.UtcNow.AddMinutes(1),
                harness.Time.UtcNow.AddMinutes(20),
                AuthorityDelegationCompletionConstraintKind.None),
        };
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.Calls.Clear();

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.EnvelopeNotEffective, result.Status);
        Assert.Equal(["transaction"], harness.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_AtExclusiveExpiryInstantIsExpired()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.Time.UtcNow = envelope.Boundary.ExpiresAtUtc!.Value;
        harness.Calls.Clear();

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.EnvelopeExpired, result.Status);
        Assert.Equal(["transaction"], harness.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_OneTickBeforeExpiryRemainsValid()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.Time.UtcNow = envelope.Boundary.ExpiresAtUtc!.Value.AddTicks(-1);
        harness.GrantResolution = harness.GrantResolution with { EvaluatedAtUtc = harness.Time.UtcNow };
        harness.Calls.Clear();

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.Valid, result.Status);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsEnvelopeThatExpiresWhileSourcesAreResolving()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var expiry = Assert.IsType<DateTimeOffset>(envelope.Boundary.ExpiresAtUtc);
        harness.TargetCallback = (_, _) =>
        {
            harness.Time.UtcNow = expiry;
            return Task.FromResult(harness.TargetResolution);
        };
        harness.Calls.Clear();

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.EnvelopeExpired, result.Status);
        Assert.Null(result.Envelope);
        Assert.Equal(["transaction", "completion", "grant", "origin", "target"], harness.Calls);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsSubstitutedTargetMaximumEvidence()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        harness.TargetResolution = harness.CreateTargetResolution(
            AuthorityDelegationTargetResolutionStatus.Active,
            AuthorityDelegationServiceTestHarness.Hash('9'));

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(envelope));

        Assert.Equal(AuthorityDelegationServiceStatus.ParentReplaced, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsRehashedForgedTargetMaximumProof()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var forgedProof = AuthorityDelegationContractHash.Apply(new AuthorityDelegationSubsetProof(
            envelope.SubsetProof.ParentEvidenceHash,
            envelope.SubsetProof.ParentAuthorityScopeHash,
            envelope.SubsetProof.DelegatedAuthorityScopeHash,
            AuthorityDelegationServiceTestHarness.Hash('9'),
            envelope.SubsetProof.NarrowingDimensions,
            string.Empty));
        var forgedEnvelope = AuthorityDelegationContractHash.Apply(new AuthorityDelegationEnvelope(
            envelope.SchemaVersion,
            envelope.EnvelopeId,
            envelope.ParentEvidence,
            envelope.Target,
            envelope.DelegatedCeiling,
            envelope.DelegatedCapabilityPins,
            envelope.TargetClass,
            envelope.OperationClass,
            envelope.Purpose,
            envelope.Boundary,
            envelope.RevocationLink,
            forgedProof,
            envelope.IssuedAtUtc,
            string.Empty));
        Assert.True(AuthorityDelegationContractValidator.Validate(forgedEnvelope).IsValid);

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(forgedEnvelope));

        Assert.Equal(AuthorityDelegationServiceStatus.ParentReplaced, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsRehashedFalseNarrowingProof()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        Assert.Empty(envelope.SubsetProof.NarrowingDimensions);
        var forgedProof = AuthorityDelegationContractHash.Apply(new AuthorityDelegationSubsetProof(
            envelope.SubsetProof.ParentEvidenceHash,
            envelope.SubsetProof.ParentAuthorityScopeHash,
            envelope.SubsetProof.DelegatedAuthorityScopeHash,
            envelope.SubsetProof.TargetMaximumEvidenceHash,
            [AuthorityDelegationNarrowingDimension.CapabilityIdentitySet],
            string.Empty));
        var forgedEnvelope = Rehash(envelope, subsetProof: forgedProof);
        Assert.True(AuthorityDelegationContractValidator.Validate(forgedEnvelope).IsValid);

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(forgedEnvelope));

        Assert.Equal(AuthorityDelegationServiceStatus.ParentReplaced, result.Status);
        Assert.Null(result.Envelope);
    }

    [Fact]
    public async Task RevalidateAsync_RejectsFullyRehashedParentEvidenceForgery()
    {
        var harness = await AuthorityDelegationServiceTestHarness.CreateAsync();
        var envelope = Assert.IsType<AuthorityDelegationEnvelope>((await harness.CreateService().CreateAsync(harness.Request)).Envelope);
        var forgedParent = AuthorityDelegationContractHash.Apply(envelope.ParentEvidence with
        {
            OriginBindingEvidenceHash = AuthorityDelegationServiceTestHarness.Hash('9'),
            ContentHash = string.Empty,
        });
        var forgedProof = AuthorityDelegationContractHash.Apply(new AuthorityDelegationSubsetProof(
            forgedParent.ContentHash,
            envelope.SubsetProof.ParentAuthorityScopeHash,
            envelope.SubsetProof.DelegatedAuthorityScopeHash,
            envelope.SubsetProof.TargetMaximumEvidenceHash,
            envelope.SubsetProof.NarrowingDimensions,
            string.Empty));
        var forgedEnvelope = Rehash(envelope, parentEvidence: forgedParent, subsetProof: forgedProof);
        Assert.True(AuthorityDelegationContractValidator.Validate(forgedEnvelope).IsValid);

        var result = await harness.CreateService().RevalidateAsync(harness.UseRequest(forgedEnvelope));

        Assert.Equal(AuthorityDelegationServiceStatus.OriginDrifted, result.Status);
        Assert.Null(result.Envelope);
    }

    private static AuthorityDelegationEnvelope Rehash(
        AuthorityDelegationEnvelope source,
        AuthorityDelegationParentEvidenceReference? parentEvidence = null,
        AuthorityDelegationSubsetProof? subsetProof = null)
        => AuthorityDelegationContractHash.Apply(new AuthorityDelegationEnvelope(
            source.SchemaVersion,
            source.EnvelopeId,
            parentEvidence ?? source.ParentEvidence,
            source.Target,
            source.DelegatedCeiling,
            source.DelegatedCapabilityPins,
            source.TargetClass,
            source.OperationClass,
            source.Purpose,
            source.Boundary,
            source.RevocationLink,
            subsetProof ?? source.SubsetProof,
            source.IssuedAtUtc,
            string.Empty));
}
