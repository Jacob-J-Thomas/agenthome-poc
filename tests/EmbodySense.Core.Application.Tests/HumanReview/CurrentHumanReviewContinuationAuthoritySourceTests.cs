using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Tests.Loops.Admission;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class CurrentHumanReviewContinuationAuthoritySourceTests
{
    [Fact]
    public async Task Active_resolution_without_the_exact_current_grant_fails_closed_before_capability_revalidation()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var resolver = new FixedHumanReviewAuthorityGrantResolver(new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            context.Receipt.Intent.AuthorityGrant,
            null,
            context.Receipt.Evidence.EffectiveAuthority,
            context.Receipt.Evidence.GrantDependencyEvidenceHash,
            DateTimeOffset.UtcNow));
        var capabilities = new RecordingHumanReviewCapabilityAdmissionService();
        var source = new CurrentHumanReviewContinuationAuthoritySource(resolver, capabilities);

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(Binding(context), context.AdapterBinding, context.Artifact));

        Assert.Equal(HumanReviewContinuationAuthorityReadStatus.Stale, result.Status);
        Assert.Equal(0, capabilities.RevalidateCount);
    }

    [Fact]
    public async Task Current_authority_ceiling_narrowing_blocks_release_without_capability_revalidation()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var binding = Binding(context);
        var resolver = new FixedHumanReviewAuthorityGrantResolver(new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.CeilingExceeded,
            context.Receipt.Intent.AuthorityGrant,
            null,
            AuthorityCeilingIntersection.EmptyCeiling(),
            string.Empty,
            default));
        var capabilities = new RecordingHumanReviewCapabilityAdmissionService();
        var source = new CurrentHumanReviewContinuationAuthoritySource(resolver, capabilities);

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(binding, context.AdapterBinding, context.Artifact));

        Assert.Equal(HumanReviewContinuationAuthorityReadStatus.Narrowed, result.Status);
        Assert.Equal(0, capabilities.RevalidateCount);
    }

    [Fact]
    public async Task Exact_active_grant_and_capability_pins_are_current()
    {
        var (binding, adapterBinding, harness) = await ExactContextAsync();
        var capabilities = new TestCapabilityAdmissionService
        {
            RevalidationResult = new CapabilityRevalidationResult(
                true,
                adapterBinding.AdmissionReceipt.Evidence.CapabilityAdmission.Pins,
                "Exact capability pins remain current.",
                CapabilityRevalidationStatus.Active),
        };
        var source = new CurrentHumanReviewContinuationAuthoritySource(harness, capabilities);

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(binding, adapterBinding, harness.Artifact));

        Assert.Equal(HumanReviewContinuationAuthorityReadStatus.Current, result.Status);
    }

    [Theory]
    [InlineData(AuthorityGrantResolutionStatus.Revoked, HumanReviewContinuationAuthorityReadStatus.Revoked)]
    [InlineData(AuthorityGrantResolutionStatus.Stale, HumanReviewContinuationAuthorityReadStatus.Stale)]
    [InlineData(AuthorityGrantResolutionStatus.Unavailable, HumanReviewContinuationAuthorityReadStatus.Unavailable)]
    public async Task Noncurrent_grant_postures_map_to_closed_authority_results(
        AuthorityGrantResolutionStatus grantStatus,
        HumanReviewContinuationAuthorityReadStatus expectedStatus)
    {
        var (binding, adapterBinding, harness) = await ExactContextAsync();
        harness.GrantResolution = harness.GrantResolution with { Status = grantStatus };
        var source = new CurrentHumanReviewContinuationAuthoritySource(harness, new TestCapabilityAdmissionService());

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(binding, adapterBinding, harness.Artifact));

        Assert.Equal(expectedStatus, result.Status);
    }

    [Fact]
    public async Task Capability_identity_drift_maps_to_a_stale_authority_posture()
    {
        var (binding, adapterBinding, harness) = await ExactContextAsync();
        var capabilities = new TestCapabilityAdmissionService
        {
            RevalidationResult = new CapabilityRevalidationResult(
                false,
                [],
                "One admitted capability identity drifted.",
                CapabilityRevalidationStatus.PinDrifted),
        };
        var source = new CurrentHumanReviewContinuationAuthoritySource(harness, capabilities);

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(binding, adapterBinding, harness.Artifact));

        Assert.Equal(HumanReviewContinuationAuthorityReadStatus.Stale, result.Status);
    }

    [Fact]
    public async Task Active_revalidation_with_duplicate_admitted_pins_is_stale()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var admittedPins = context.AdapterBinding.AdmissionReceipt.Evidence.CapabilityAdmission.Pins;
        var capabilities = new TestCapabilityAdmissionService
        {
            RevalidationResult = new CapabilityRevalidationResult(
                true,
                [.. admittedPins, admittedPins[0]],
                "Duplicated admitted capability pin.",
                CapabilityRevalidationStatus.Active),
        };
        var source = new CurrentHumanReviewContinuationAuthoritySource(GovernedLoopAdmissionTestHarness.Create(), capabilities);

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(Binding(context), context.AdapterBinding, context.Artifact));

        Assert.Equal(HumanReviewContinuationAuthorityReadStatus.Stale, result.Status);
    }

    [Fact]
    public async Task Active_revalidation_with_substituted_admitted_pin_is_stale()
    {
        var context = await GovernedLoopSequentialRunMaterializerTests.ContextAsync();
        var admittedPins = context.AdapterBinding.AdmissionReceipt.Evidence.CapabilityAdmission.Pins;
        var admittedPin = admittedPins[0];
        Assert.True(CapabilityDescriptorHash.TryParse($"sha256:{Hash('a')}", out var substitutedHash, out _));
        var substitutedPin = admittedPin with { DescriptorIdentity = admittedPin.DescriptorIdentity with { Hash = substitutedHash! } };
        var capabilities = new TestCapabilityAdmissionService
        {
            RevalidationResult = new CapabilityRevalidationResult(
                true,
                [substitutedPin, .. admittedPins.Skip(1)],
                "Substituted capability pin.",
                CapabilityRevalidationStatus.Active),
        };
        var source = new CurrentHumanReviewContinuationAuthoritySource(GovernedLoopAdmissionTestHarness.Create(), capabilities);

        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(Binding(context), context.AdapterBinding, context.Artifact));

        Assert.Equal(HumanReviewContinuationAuthorityReadStatus.Stale, result.Status);
    }

    private static HumanReviewBinding Binding(GovernedLoopSequentialRunMaterializerTests.TestContext context)
        => Binding(context.Receipt, context.AdapterBinding);

    private static HumanReviewBinding Binding(GovernedLoopAdmissionReceipt receipt, GovernedLoopSequentialAdapterBinding adapterBinding)
    {
        return HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            adapterBinding.WorkspaceId,
            adapterBinding.ExecutionBinding.RunId,
            adapterBinding.ExecutionBinding.Revision.GraphId,
            adapterBinding.ExecutionBinding.Revision.RevisionId,
            adapterBinding.ExecutionBinding.Revision.ExecutableHash,
            "node-one",
            0,
            null,
            1,
            "frontier-one",
            1,
            Hash('1'),
            receipt.Evidence.GrantProfile.ContentHash.Value["sha256:".Length..],
            receipt.Evidence.GrantDependencyEvidenceHash,
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(receipt.Evidence.CapabilityAdmission),
            receipt.Evidence.ModelRoutingAdmission.ContentHash,
            Hash('2'),
            Hash('3'),
            Hash('4'),
            null,
            string.Empty));
    }

    private static async Task<(HumanReviewBinding Binding, GovernedLoopSequentialAdapterBinding AdapterBinding, GovernedLoopAdmissionTestHarness Harness)> ExactContextAsync()
    {
        var harness = GovernedLoopAdmissionTestHarness.Create();
        var admission = await harness.CreateService().AdmitAsync(harness.Request);
        var outcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(admission.Outcome);
        var receipt = Assert.IsType<GovernedLoopAdmissionReceipt>(outcome.Receipt);
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            receipt.Intent.WorkspaceId,
            receipt.Evidence.Binding,
            receipt.Intent.OperationId,
            receipt,
            receipt.ContentHash,
            receipt.Intent.RequestHash,
            Hash('6'),
            receipt.Intent.GraphArtifactHash,
            receipt.Intent.GraphLayoutHash,
            [],
            string.Empty));
        Assert.True(GovernedLoopSequentialContractValidator.Validate(adapterBinding).IsValid);
        return (Binding(receipt, adapterBinding), adapterBinding, harness);
    }

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

}
