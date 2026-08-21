using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Effects;

public sealed class GovernedActuatorOperationContractTests
{
    [Fact]
    public void Operation_metadata_is_canonical_and_does_not_duplicate_capability_lifecycle_or_schema()
    {
        var descriptor = Create();
        var second = Create();

        Assert.Null(GovernedActuatorOperationContract.Validate(descriptor));
        Assert.Equal(descriptor.ContentHash, second.ContentHash);
        Assert.Equal(64, descriptor.ContentHash.Length);
        Assert.DoesNotContain(
            typeof(GovernedActuatorOperationDescriptor).GetProperties(),
            property => property.Name.Contains("Lifecycle", StringComparison.Ordinal)
                || property.Name is "InputSchema" or "OutputSchema"
                || property.Name.Contains("Trust", StringComparison.Ordinal)
                || property.Name.Contains("Health", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_enumerations_malformed_pins_and_hash_substitution_fail_closed()
    {
        var descriptor = Create();
        Assert.Equal("operation-posture-invalid", GovernedActuatorOperationContract.Validate(descriptor with { Approval = (GovernedActuatorApprovalPosture)99 }));
        Assert.Equal("operation-content-hash-mismatch", GovernedActuatorOperationContract.Validate(descriptor with { RiskSummary = "Changed risk." }));
        Assert.Equal("operation-id-invalid", GovernedActuatorOperationContract.Validate(descriptor with { OperationId = "UPPER" }));
        Assert.Equal("operation-capability-pin-invalid", GovernedActuatorOperationContract.Validate(descriptor with { Capability = null! }));
        Assert.Throws<ArgumentException>(() => GovernedActuatorOperationContract.Compute(descriptor with { TargetSemantics = GovernedActuatorTargetSemantics.Unknown }));
    }

    [Fact]
    public void Schema_one_requires_outcome_evidence_while_other_evidence_and_approval_axes_remain_orthogonal()
    {
        var descriptor = Create(
            approval: GovernedActuatorApprovalPosture.GovernedApprovalRequired,
            unattendedEligible: true,
            requiresAfterEvidence: true,
            requiresOutcomeEvidence: true);

        Assert.Null(GovernedActuatorOperationContract.Validate(descriptor));
        Assert.True(descriptor.UnattendedEligible);
        Assert.True(descriptor.RequiresAfterEvidence);
        Assert.True(descriptor.RequiresOutcomeEvidence);

        var withoutOutcomeEvidence = descriptor with { RequiresOutcomeEvidence = false };
        Assert.Equal("operation-outcome-evidence-required", GovernedActuatorOperationContract.Validate(withoutOutcomeEvidence));
        Assert.Throws<ArgumentException>(() => Create(requiresOutcomeEvidence: false));
    }

    internal static GovernedActuatorOperationDescriptor Create(
        GovernedActuatorApprovalPosture approval = GovernedActuatorApprovalPosture.AuthorityOnly,
        bool unattendedEligible = false,
        bool requiresAfterEvidence = true,
        bool requiresOutcomeEvidence = true)
    {
        var capability = CapabilityContractTestData.ValidDescriptor();
        Assert.True(CapabilityDescriptorIdentity.TryCreate(capability, out var identity, out var validation), validation.Errors.FirstOrDefault()?.Message);
        return GovernedActuatorOperationContract.Create(
            1,
            identity!,
            capability.Implementation,
            "probe/observe",
            "Emits deterministic probe evidence without a concrete workspace or command effect.",
            GovernedActuatorTargetSemantics.ExactOpaqueFingerprint,
            GovernedActuatorIdempotencyPosture.StableOperationIdentity,
            requiresOptimisticPrecondition: true,
            approval,
            unattendedEligible,
            GovernedActuatorCancellationPosture.BeforeBoundaryOnly,
            GovernedActuatorAmbiguityPosture.ReconciliationRequired,
            requiresBeforeEvidence: true,
            requiresAfterEvidence,
            requiresOutcomeEvidence);
    }
}
