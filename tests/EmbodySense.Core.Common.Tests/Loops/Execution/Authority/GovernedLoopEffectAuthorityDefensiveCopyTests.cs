using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopEffectAuthorityDefensiveCopyTests
{
    [Fact]
    public void Proof_and_decision_snapshot_every_caller_owned_collection()
    {
        var pin = GovernedLoopEffectAuthorityTestFixture.Pin();
        var capabilityArray = new[] { pin.DescriptorIdentity };
        var dataClassArray = GovernedLoopEffectAuthorityTestFixture.AdmittedCeiling(pin).DataClasses.ToArray();
        var proofPins = new[] { pin };
        var observedPins = Array.Empty<CapabilityAdmissionPin>();
        var ceiling = new AuthorityCeiling(
            capabilityArray,
            dataClassArray,
            2,
            CapabilitySideEffectClass.ReadOnly,
            false,
            false,
            false);
        var proof = GovernedLoopEffectAuthorityTestFixture.Proof(ceiling: ceiling, pins: proofPins, observedPins: observedPins);
        var requiredPins = new[] { pin };
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision(proof, proof, requiredPins: requiredPins);

        capabilityArray[0] = null!;
        dataClassArray[0] = null!;
        proofPins[0] = null!;
        requiredPins[0] = null!;

        Assert.NotNull(proof.Ceiling.Capabilities[0]);
        Assert.NotNull(proof.Ceiling.DataClasses[0]);
        Assert.NotNull(proof.CapabilityPins[0]);
        Assert.Empty(proof.ObservedCapabilityPins);
        Assert.NotNull(decision.RequiredCapabilityPins[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityAdmissionPin>)proof.CapabilityPins).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityAdmissionPin>)proof.ObservedCapabilityPins).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CapabilityAdmissionPin>)decision.RequiredCapabilityPins).Clear());
        Assert.True(GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid);
    }

    [Fact]
    public void Oversized_collections_are_capped_at_limit_plus_one_and_rejected()
    {
        var pin = GovernedLoopEffectAuthorityTestFixture.Pin();
        var oversizedProofPins = Enumerable.Repeat(pin, GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins + 100).ToArray();
        var oversizedRequiredPins = Enumerable.Repeat(pin, GovernedLoopEffectAuthorityContractLimits.MaxRequiredCapabilityPins + 100).ToArray();
        var proof = GovernedLoopEffectAuthorityTestFixture.Proof(pins: oversizedProofPins);
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision(requiredPins: oversizedRequiredPins, applyHash: false);

        Assert.Equal(GovernedLoopEffectAuthorityContractLimits.MaxCapabilityPins + 1, proof.CapabilityPins.Count);
        Assert.Equal(GovernedLoopEffectAuthorityContractLimits.MaxRequiredCapabilityPins + 1, decision.RequiredCapabilityPins.Count);
        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate(proof).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.LimitExceeded);
        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate(decision).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.LimitExceeded);
    }
}
