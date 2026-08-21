using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

public sealed class AuthorityDelegationDefensiveCopyTests
{
    [Fact]
    public void Envelope_DefensivelyCopiesCapabilityPins()
    {
        var pins = new[] { AuthorityDelegationTestFixture.Pin() };
        var envelope = AuthorityDelegationTestFixture.Envelope(parentPins: pins, delegatedPins: pins);

        pins[0] = pins[0] with { SafeDescription = "mutated after construction" };

        Assert.NotEqual(pins[0], envelope.DelegatedCapabilityPins[0]);
        Assert.True(AuthorityDelegationContractValidator.Validate(envelope).IsValid);
    }

    [Fact]
    public void Envelope_FailsClosedForThrowingNestedPinCollection()
    {
        var valid = AuthorityDelegationTestFixture.Envelope(applyHash: false);

        var exception = Record.Exception(() =>
        {
            var candidate = new AuthorityDelegationEnvelope(
                valid.SchemaVersion,
                valid.EnvelopeId,
                valid.ParentEvidence,
                valid.Target,
                valid.DelegatedCeiling,
                new ThrowingReadOnlyList<CapabilityAdmissionPin>(),
                valid.TargetClass,
                valid.OperationClass,
                valid.Purpose,
                valid.Boundary,
                valid.RevocationLink,
                valid.SubsetProof,
                valid.IssuedAtUtc,
                string.Empty);
            var result = AuthorityDelegationContractValidator.Validate(candidate);
            Assert.False(result.IsValid);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Envelope_FailsClosedForLyingPinCount()
    {
        var valid = AuthorityDelegationTestFixture.Envelope(applyHash: false);
        var lying = new LyingCountReadOnlyList<CapabilityAdmissionPin>(valid.DelegatedCapabilityPins, valid.DelegatedCapabilityPins.Count + 1);

        var candidate = new AuthorityDelegationEnvelope(
            valid.SchemaVersion,
            valid.EnvelopeId,
            valid.ParentEvidence,
            valid.Target,
            valid.DelegatedCeiling,
            lying,
            valid.TargetClass,
            valid.OperationClass,
            valid.Purpose,
            valid.Boundary,
            valid.RevocationLink,
            valid.SubsetProof,
            valid.IssuedAtUtc,
            string.Empty);

        var result = AuthorityDelegationContractValidator.Validate(candidate);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidationResult_FailsClosedForThrowingErrorCollection()
    {
        AuthorityDelegationContractValidationResult? result = null;

        var exception = Record.Exception(() =>
            result = new AuthorityDelegationContractValidationResult(
                new ThrowingReadOnlyList<AuthorityDelegationContractValidationError>(),
                true));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(AuthorityDelegationContractValidationErrorCode.InvalidCollection, error.Code);
        Assert.Equal("$.errors", error.Path);
    }
}
