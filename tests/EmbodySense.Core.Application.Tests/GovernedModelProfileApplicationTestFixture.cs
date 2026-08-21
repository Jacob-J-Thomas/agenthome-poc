using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Tests;

internal static class GovernedModelProfileApplicationTestFixture
{
    internal static GovernedModelRoutingPolicy DefaultRoutingPolicy(string profileId = "org.embodysense/model-profile/codex")
    {
        Assert.True(CapabilityId.TryParse(profileId, out var exactProfileId, out _));
        Assert.True(CapabilityDataClass.TryParse("public", out var publicData, out _));
        var privacy = GovernedModelPrivacyRequirement.Create(
            1,
            localOnly: true,
            CapabilityEgressMode.None,
            [],
            [publicData!],
            ["local"],
            GovernedModelRetentionPosture.None,
            GovernedModelTrainingPosture.Prohibited);
        var unbounded = GovernedModelUsageCeiling.Create(
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelUsageLimit.Unbounded,
            GovernedModelMonetaryLimit.Unbounded);
        return GovernedModelRoutingPolicy.Create(
            1,
            GovernedModelRoutingSelector.Exact(exactProfileId!),
            [],
            GovernedModelProfileRequirements.Create(
                1,
                [GovernedModelModality.Text],
                [],
                1,
                1,
                privacy,
                GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
    }

    internal static GovernedLoopAdmissionEvidence EmptyRoutingEvidence(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopExecutionBinding binding,
        AuthorityGrantProfilePin grantProfile,
        AuthorityGrantBoundary grantBoundary,
        string grantDependencyEvidenceHash,
        AuthorityCeiling effectiveAuthority,
        CapabilityAdmissionSnapshot capabilityAdmission,
        DateTimeOffset evaluatedAtUtc)
    {
        var routing = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
            intent,
            binding,
            grantProfile,
            grantBoundary,
            grantDependencyEvidenceHash,
            effectiveAuthority,
            capabilityAdmission,
            evaluatedAtUtc);
        return GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            1,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            binding,
            grantProfile,
            grantBoundary,
            grantDependencyEvidenceHash,
            effectiveAuthority,
            capabilityAdmission,
            routing,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilityAdmission, routing),
            evaluatedAtUtc,
            string.Empty));
    }

    internal static GovernedLoopAdmissionEvidence RoutingEvidenceForInference(
        GovernedLoopAdmissionIntent intent,
        GovernedLoopExecutionBinding binding,
        AuthorityGrantProfilePin grantProfile,
        AuthorityGrantBoundary grantBoundary,
        string grantDependencyEvidenceHash,
        AuthorityCeiling effectiveAuthority,
        CapabilityAdmissionSnapshot capabilityAdmission,
        DateTimeOffset evaluatedAtUtc,
        string nodeId = "step-one",
        string nodeTypeId = "provider-inference",
        string modelId = "pinned-model",
        IReadOnlyList<string>? nodeIds = null)
    {
        var capability = Assert.Single(capabilityAdmission.Pins, pin => pin.Kind == CapabilityKind.ModelProfile);
        Assert.True(CapabilityDataClass.TryParse("public", out var publicData, out _));
        var metadata = GovernedModelProfileMetadata.Create(
            1,
            capability.DescriptorIdentity,
            "openai",
            "codex-app-server",
            modelId,
            "v1",
            1,
            new string('a', 64),
            "Test-only configured model profile.",
            [GovernedModelModality.Text],
            [GovernedModelCapability.ToolCalling, GovernedModelCapability.Streaming],
            1_000_000,
            100_000,
            GovernedModelPrivacyPosture.Create(
                1,
                GovernedModelLocality.LocalProcess,
                CapabilityEgressMode.None,
                [],
                [publicData!],
                ["local"],
                GovernedModelRetentionPosture.None,
                GovernedModelTrainingPosture.Prohibited),
            GovernedModelUsageSupportPolicy.Create(
                GovernedModelUsageSupport.Unavailable,
                GovernedModelUsageSupport.Unavailable,
                GovernedModelUsageSupport.Unavailable,
                GovernedModelUsageSupport.Unavailable,
                GovernedModelUsageSupport.Unavailable),
            [],
            [nodeTypeId]);
        var profile = GovernedModelProfilePin.Create(capability, metadata, new string('b', 64), new string('c', 64));
        var policy = DefaultRoutingPolicy(capability.DescriptorIdentity.Id.Value);
        var routedNodeIds = nodeIds ?? [nodeId];
        var routing = GovernedModelRoutingAdmissionSnapshot.Create(
            1,
            intent.WorkspaceId,
            intent.OperationId,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            GovernedLoopAdmissionContractHash.ComputeExecutionBindingReferenceHash(binding),
            binding.RunId,
            binding.Revision.GraphId,
            binding.Revision.RevisionId,
            binding.Revision.ExecutableHash,
            binding.ExecutionGeneration,
            intent.Role.Identity.RoleId,
            intent.Role.Identity.Revision,
            intent.Role.ContentHash,
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(capabilityAdmission),
            GovernedLoopAdmissionContractHash.ComputeAdmissionAuthorityReferenceHash(grantProfile, grantBoundary, grantDependencyEvidenceHash, effectiveAuthority),
            1,
            null,
            null,
            new string('c', 64),
            evaluatedAtUtc,
            routedNodeIds.Select(routedNodeId => GovernedModelRoutingAdmissionEntry.Create(
                1,
                routedNodeId,
                nodeTypeId,
                policy.ContentHash,
                policy.Requirements,
                false,
                [],
                profile,
                [])).ToArray());
        return GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            1,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            binding,
            grantProfile,
            grantBoundary,
            grantDependencyEvidenceHash,
            effectiveAuthority,
            capabilityAdmission,
            routing,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilityAdmission, routing),
            evaluatedAtUtc,
            string.Empty));
    }
}
