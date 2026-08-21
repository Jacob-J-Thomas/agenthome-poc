using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

public sealed class AuthorityDelegationHashTests
{
    [Fact]
    public void Hashes_AreDomainSeparated()
    {
        var envelope = AuthorityDelegationTestFixture.Envelope();

        Assert.NotEqual(envelope.ParentEvidence.ContentHash, envelope.SubsetProof.ContentHash);
        Assert.NotEqual(envelope.ParentEvidence.ContentHash, envelope.ContentHash);
        Assert.NotEqual(envelope.SubsetProof.ContentHash, envelope.ContentHash);
        Assert.NotEqual(envelope.RevocationLink.LinkageHash, envelope.ContentHash);
    }

    [Fact]
    public void Validate_RejectsOneFieldMutation()
    {
        var envelope = AuthorityDelegationTestFixture.Envelope();
        Assert.True(AuthorityPurpose.TryParse("Changed exact delegation purpose.", out var changedPurpose, out _));
        var mutations = new[]
        {
            Copy(envelope, targetClass: "changed-target"),
            Copy(envelope, operationClass: "changed-operation"),
            Copy(envelope, purpose: changedPurpose),
            Copy(envelope, boundary: envelope.Boundary with { ExpiresAtUtc = envelope.Boundary.ExpiresAtUtc!.Value.AddTicks(1) }),
            Copy(envelope, target: envelope.Target with { BindingEvidenceHash = AuthorityDelegationTestFixture.Hash('9') }),
        };

        Assert.All(mutations, changed =>
        {
            var result = AuthorityDelegationContractValidator.Validate(changed);
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Path == "$.contentHash");
        });
    }

    [Fact]
    public void Validate_RejectsSubstitutedTargetMaximumEvidenceHash()
    {
        var envelope = AuthorityDelegationTestFixture.Envelope();
        var changedProof = new AuthorityDelegationSubsetProof(
            envelope.SubsetProof.ParentEvidenceHash,
            envelope.SubsetProof.ParentAuthorityScopeHash,
            envelope.SubsetProof.DelegatedAuthorityScopeHash,
            AuthorityDelegationTestFixture.Hash('9'),
            envelope.SubsetProof.NarrowingDimensions,
            envelope.SubsetProof.ContentHash);
        var changedEnvelope = Copy(envelope, subsetProof: changedProof);

        var result = AuthorityDelegationContractValidator.Validate(changedEnvelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path.Contains("subsetProof", StringComparison.Ordinal));
    }

    [Fact]
    public void Envelope_ContainsNoBroaderParentAuthorityScope()
    {
        var delegatedPin = AuthorityDelegationTestFixture.Pin();
        var parentOnlyPin = AuthorityDelegationTestFixture.ForeignPin();
        var parentPins = new[] { delegatedPin, parentOnlyPin }
            .OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var parent = AuthorityDelegationTestFixture.Ceiling(
            capabilities: parentPins.Select(pin => pin.DescriptorIdentity).ToArray(),
            maxTargetCount: 9);
        var delegated = AuthorityDelegationTestFixture.Ceiling(
            maxTargetCount: 1,
            recurrence: false,
            externalPublication: false,
            irreversible: false);
        var envelope = AuthorityDelegationTestFixture.Envelope(
            parentCeiling: parent,
            delegatedCeiling: delegated,
            parentPins: parentPins,
            delegatedPins: [delegatedPin]);
        var json = JsonSerializer.Serialize(envelope);

        Assert.Equal(1, envelope.DelegatedCeiling.MaxTargetCount);
        Assert.DoesNotContain(parentOnlyPin.DescriptorIdentity.Id.Value, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Hashes_AreInvariantAcrossProcessCulture()
    {
        var envelope = AuthorityDelegationTestFixture.Envelope();
        var expected = AuthorityDelegationContractHash.ComputeEnvelopeHash(envelope);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            foreach (var cultureName in new[] { "tr-TR", "fr-FR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                Assert.Equal(expected, AuthorityDelegationContractHash.ComputeEnvelopeHash(envelope));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Hashes_AreStableAfterIndependentObjectGraphReconstruction()
    {
        var envelope = AuthorityDelegationTestFixture.Envelope();
        Assert.True(AuthorityPurpose.TryParse(envelope.Purpose.Value, out var reconstructedPurpose, out _));
        var reconstructed = Copy(envelope, purpose: reconstructedPurpose, contentHash: string.Empty);

        var rehashed = AuthorityDelegationContractHash.Apply(reconstructed);

        Assert.NotSame(envelope, rehashed);
        Assert.NotSame(envelope.ParentEvidence, rehashed.ParentEvidence);
        Assert.NotSame(envelope.DelegatedCeiling.Capabilities, rehashed.DelegatedCeiling.Capabilities);
        Assert.NotSame(envelope.Purpose, rehashed.Purpose);
        Assert.Equal(envelope.ContentHash, rehashed.ContentHash);
        Assert.True(AuthorityDelegationContractValidator.Validate(rehashed).IsValid);
    }

    [Fact]
    public void AuthorityScopeHash_RejectsNonCanonicalCollectionOrder()
    {
        var first = AuthorityDelegationTestFixture.Pin();
        var second = AuthorityDelegationTestFixture.ForeignPin();
        var orderedPins = new[] { first, second }
            .OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .ToArray();
        var reversedPins = orderedPins.Reverse().ToArray();
        var ceiling = AuthorityDelegationTestFixture.Ceiling(
            capabilities: reversedPins.Select(pin => pin.DescriptorIdentity).ToArray());

        Assert.Throws<ArgumentException>(() => AuthorityDelegationContractHash.ComputeAuthorityScopeHash(ceiling, reversedPins));
    }

    private static AuthorityDelegationEnvelope Copy(
        AuthorityDelegationEnvelope value,
        string? targetClass = null,
        string? operationClass = null,
        AuthorityPurpose? purpose = null,
        AuthorityDelegationBoundary? boundary = null,
        AuthorityDelegationTargetBinding? target = null,
        AuthorityDelegationSubsetProof? subsetProof = null,
        string? contentHash = null)
        => new(
            value.SchemaVersion,
            value.EnvelopeId,
            value.ParentEvidence,
            target ?? value.Target,
            value.DelegatedCeiling,
            value.DelegatedCapabilityPins,
            targetClass ?? value.TargetClass,
            operationClass ?? value.OperationClass,
            purpose ?? value.Purpose,
            boundary ?? value.Boundary,
            value.RevocationLink,
            subsetProof ?? value.SubsetProof,
            value.IssuedAtUtc,
            contentHash ?? value.ContentHash);
}
